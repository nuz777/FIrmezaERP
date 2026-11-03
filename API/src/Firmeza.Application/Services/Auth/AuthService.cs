using Firmeza.Application.Abstractions;
using Firmeza.Application.Common;
using Firmeza.Application.Dtos.Auth;
using Firmeza.Domain.Entities;
using Firmeza.Domain.Enums;
using Firmeza.Domain.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Firmeza.Application.Services.Auth;

public sealed class AuthService : IAuthService
{
    private readonly IUserService _users;
    private readonly ITokenService _tokens;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ICustomerRequestRepository _requests;
    private readonly ICustomerRepository _customers;
    private readonly ICurrentUserService _currentUser;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMailService? _mailService;
    private readonly IMemoryCache? _cache;

    public AuthService(
        IUserService users,
        ITokenService tokens,
        IRefreshTokenRepository refreshTokens,
        ICustomerRequestRepository requests,
        ICustomerRepository customers,
        ICurrentUserService currentUser,
        IClock clock,
        IUnitOfWork unitOfWork,
        IMailService? mailService = null,
        IMemoryCache? cache = null)
    {
        _users = users;
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _requests = requests;
        _customers = customers;
        _currentUser = currentUser;
        _clock = clock;
        _unitOfWork = unitOfWork;
        _mailService = mailService;
        _cache = cache;
    }

    public async Task<Result<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _users.SignInAsync(request.Email.Trim(), request.Password, cancellationToken);

        switch (result.Outcome)
        {
            case SignInOutcome.LockedOut:
                return Result.Failure<TokenResponse>(Error.Unauthorized("La cuenta esta bloqueada por intentos de acceso fallidos."));
            case SignInOutcome.NotAllowed:
                return Result.Failure<TokenResponse>(Error.Forbidden("El correo no esta confirmado."));
            case SignInOutcome.InvalidCredentials:
                return Result.Failure<TokenResponse>(Error.Unauthorized("Correo o contrasena incorrectos."));
        }

        var user = result.User!;
        var roles = await _users.GetRolesAsync(user.Id, cancellationToken);

        return await IssueTokensAsync(user, roles, cancellationToken);
    }

    public async Task<Result<UserResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Result.Failure<UserResponse>(
                Error.Validation(new Dictionary<string, string[]> { [nameof(request.ConfirmPassword)] = ["Las contrasenas no coinciden."] }));
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var emailTomado = await _users.FindByEmailAsync(normalizedEmail, cancellationToken) is not null
            || await _customers.FindByEmailAsync(normalizedEmail, cancellationToken) is not null
            || await _requests.FindLatestByEmailAsync(normalizedEmail, cancellationToken) is not null;

        if (emailTomado)
        {
            return Result.Failure<UserResponse>(Error.Validation(new Dictionary<string, string[]>
            {
                [nameof(request.Email)] = ["Ya existe una cuenta registrada con este correo electrónico."]
            }, "Ya existe una cuenta registrada con este correo electrónico. Por favor inicia sesión o utiliza otro correo diferente."));
        }

        var registration = new UserRegistration(
            normalizedEmail,
            request.FullName.Trim(),
            request.Password);

        var created = await _users.RegisterAsync(registration, ApplicationRoles.Customer, cancellationToken);
        if (!created.Succeeded || created.Value is null)
        {
            return Result.Failure<UserResponse>(Error.Validation(
                MapIdentityErrors(created.Errors),
                "No fue posible completar el registro. Revisa los datos ingresados."));
        }

        var document = request.Document.Trim();

        // El documento se valida contra clientes y contra solicitudes previas.
        // Si el documento ya esta tomado se elimina la cuenta recien creada:
        // el registro publico no debe dejar usuarios huerfanos.
        var documentoTomado = await _customers.DocumentExistsAsync(document, null, cancellationToken)
            || await _requests.DocumentExistsAsync(document, cancellationToken);

        if (documentoTomado)
        {
            await _users.DeleteAsync(created.Value.Id, cancellationToken);
            return Result.Failure<UserResponse>(Error.Validation(new Dictionary<string, string[]>
            {
                [nameof(request.Document)] = ["Ese documento ya esta registrado."]
            }));
        }

        await _requests.AddAsync(new CustomerSignupRequest
        {
            UserId = created.Value.Id,
            Document = document,
            FullName = created.Value.FullName,
            Age = request.Age,
            Email = created.Value.Email,
            Phone = request.Phone.Trim(),
            Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
            Status = CustomerRequestStatus.Pending,
            CreatedAt = _clock.UtcNow
        }, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (_mailService is not null)
        {
            try
            {
                await _mailService.SendWelcomeEmailAsync(created.Value.Email, created.Value.FullName, cancellationToken);
            }
            catch
            {
                // Fallo de correo no debe tumbar el registro
            }
        }

        return Result.Success(new UserResponse(
            created.Value.Id,
            created.Value.Email,
            created.Value.FullName,
            [ApplicationRoles.Customer],
            CustomerRequestStatus.Pending.ToString()));
    }

    public async Task<Result<TokenResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        var currentHash = _tokens.HashRefreshToken(request.RefreshToken);
        var stored = await _refreshTokens.FindActiveByHashAsync(currentHash, cancellationToken);
        if (stored is null)
        {
            return Result.Failure<TokenResponse>(Error.Unauthorized("El refresh token es invalido o expiro."));
        }

        var user = await _users.FindByIdAsync(stored.UserId, cancellationToken);
        if (user is null)
        {
            await _refreshTokens.RevokeAsync(currentHash, _clock.UtcNow, null, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Failure<TokenResponse>(Error.Unauthorized("El refresh token es invalido o expiro."));
        }

        var roles = await _users.GetRolesAsync(user.Id, cancellationToken);
        var response = await IssueTokensAsync(user, roles, cancellationToken);
        if (response.IsFailure)
        {
            return response;
        }

        var now = _clock.UtcNow;
        var rotatedHash = _tokens.HashRefreshToken(response.Value!.RefreshToken);

        await _refreshTokens.RevokeAsync(currentHash, now, rotatedHash, cancellationToken);
        await _refreshTokens.StoreAsync(
            new RefreshTokenData(
                Guid.NewGuid().ToString("N"),
                user.Id,
                rotatedHash,
                response.Value.RefreshTokenExpiresAt,
                now,
                null,
                null),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return response;
    }

    public async Task<Result<UserResponse>> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Result.Failure<UserResponse>(Error.Unauthorized("No hay una sesion activa."));
        }

        var user = await _users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<UserResponse>(Error.EntityNotFound("Usuario", userId));
        }

        var roles = await _users.GetRolesAsync(user.Id, cancellationToken);
        var customerStatus = await ResolveCustomerStatusAsync(user.Email, roles, cancellationToken);

        string? phone = null;
        string? address = null;

        var customer = await _customers.FindByEmailAsync(user.Email, cancellationToken);
        if (customer is not null)
        {
            phone = customer.Phone;
            address = customer.Address;
        }
        else
        {
            var request = await _requests.FindLatestByEmailAsync(user.Email, cancellationToken);
            if (request is not null)
            {
                phone = request.Phone;
                address = request.Address;
            }
        }

        return Result.Success(new UserResponse(user.Id, user.Email, user.FullName, roles, customerStatus, user.AvatarUrl, phone, address));
    }

    public async Task<Result<UserResponse>> UpdateProfileAsync(
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Result.Failure<UserResponse>(Error.Unauthorized("No hay una sesión activa."));
        }

        var user = await _users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<UserResponse>(Error.EntityNotFound("Usuario", userId));
        }

        var trimmedName = request.FullName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            return Result.Failure<UserResponse>(Error.Validation(new Dictionary<string, string[]>
            {
                [nameof(request.FullName)] = ["El nombre es obligatorio."]
            }, "El nombre completo es obligatorio."));
        }

        var updateResult = await _users.UpdateProfileAsync(userId, trimmedName, request.AvatarUrl, cancellationToken);
        if (!updateResult.Succeeded || updateResult.Value is null)
        {
            return Result.Failure<UserResponse>(Error.Validation(
                MapIdentityErrors(updateResult.Errors),
                "No fue posible actualizar el perfil."));
        }

        var customer = await _customers.FindByEmailAsync(user.Email, cancellationToken);
        if (customer is not null)
        {
            customer.FullName = trimmedName;
            if (!string.IsNullOrWhiteSpace(request.Phone))
            {
                customer.Phone = request.Phone.Trim();
            }
            if (request.Address is not null)
            {
                customer.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
            }
            customer.UpdatedAt = _clock.UtcNow;
        }

        var signupRequest = await _requests.FindLatestByEmailAsync(user.Email, cancellationToken);
        if (signupRequest is not null)
        {
            signupRequest.FullName = trimmedName;
            if (!string.IsNullOrWhiteSpace(request.Phone))
            {
                signupRequest.Phone = request.Phone.Trim();
            }
            if (request.Address is not null)
            {
                signupRequest.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var roles = await _users.GetRolesAsync(user.Id, cancellationToken);
        var customerStatus = await ResolveCustomerStatusAsync(user.Email, roles, cancellationToken);

        return Result.Success(new UserResponse(
            user.Id,
            user.Email,
            trimmedName,
            roles,
            customerStatus,
            updateResult.Value.AvatarUrl,
            customer?.Phone ?? signupRequest?.Phone ?? request.Phone,
            customer?.Address ?? signupRequest?.Address ?? request.Address));
    }

    public async Task<Result<RequestPasswordResetCodeResponse>> RequestPasswordResetCodeAsync(
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Result.Failure<RequestPasswordResetCodeResponse>(Error.Unauthorized("No hay una sesión activa."));
        }

        var user = await _users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<RequestPasswordResetCodeResponse>(Error.EntityNotFound("Usuario", userId));
        }

        if (_cache is not null && _cache.TryGetValue($"pwd-otp-cooldown:{userId}", out _))
        {
            return Result.Failure<RequestPasswordResetCodeResponse>(Error.Validation(new Dictionary<string, string[]>
            {
                ["Code"] = ["Debes esperar 60 segundos antes de solicitar un nuevo código."]
            }, "Debes esperar 60 segundos antes de solicitar un nuevo código de verificación."));
        }

        var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");

        if (_cache is not null)
        {
            _cache.Set($"pwd-otp:{userId}", code, TimeSpan.FromMinutes(10));
            _cache.Set($"pwd-otp-cooldown:{userId}", true, TimeSpan.FromSeconds(60));
        }

        if (_mailService is not null)
        {
            try
            {
                await _mailService.SendPasswordResetCodeEmailAsync(user.Email, user.FullName, code, 10, cancellationToken);
            }
            catch
            {
                // Fallo de correo no interrumpe el flujo
            }
        }

        return Result.Success(new RequestPasswordResetCodeResponse(
            user.Email,
            "Hemos enviado un código de 6 dígitos a tu correo electrónico.",
            10));
    }

    public async Task<Result> ChangePasswordWithCodeAsync(
        ChangePasswordWithCodeRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Result.Failure(Error.Unauthorized("No hay una sesión activa."));
        }

        if (!string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Result.Failure(Error.Validation(new Dictionary<string, string[]>
            {
                [nameof(request.ConfirmPassword)] = ["Las contraseñas no coinciden."]
            }, "Las contraseñas ingresadas no coinciden."));
        }

        var inputCode = request.Code?.Trim() ?? string.Empty;

        if (_cache is not null)
        {
            if (!_cache.TryGetValue($"pwd-otp:{userId}", out string? cachedCode) || !string.Equals(cachedCode, inputCode, StringComparison.Ordinal))
            {
                return Result.Failure(Error.Validation(new Dictionary<string, string[]>
                {
                    [nameof(request.Code)] = ["El código de verificación es incorrecto o ha expirado."]
                }, "El código de verificación ingresado no es válido o ya expiró (duración máxima 10 minutos)."));
            }
        }

        var resetResult = await _users.ResetPasswordAsync(userId, request.NewPassword, cancellationToken);
        if (!resetResult.Succeeded)
        {
            return Result.Failure(Error.Validation(
                MapIdentityErrors(resetResult.Errors),
                "No fue posible actualizar la contraseña. Revisa que cumpla los requisitos de seguridad."));
        }

        _cache?.Remove($"pwd-otp:{userId}");
        _cache?.Remove($"pwd-otp-cooldown:{userId}");

        return Result.Success();
    }

    public async Task<Result<TokenResponse>> ExternalLoginAsync(
        ExternalUserRegistration registration,
        bool allowCreate = true,
        CancellationToken cancellationToken = default)
    {
        var userResult = await _users.FindOrCreateExternalUserAsync(
            registration,
            ApplicationRoles.Customer,
            allowCreate,
            cancellationToken);

        if (!userResult.Succeeded)
        {
            var firstError = userResult.Errors.FirstOrDefault() ?? "No fue posible autenticar la cuenta externa.";
            return Result.Failure<TokenResponse>(Error.Unauthorized(firstError));
        }

        var (user, _) = userResult.Value;
        var roles = await _users.GetRolesAsync(user.Id, cancellationToken);

        return await IssueTokensAsync(user, roles, cancellationToken);
    }

    public async Task<Result<UserResponse>> CompleteProfileAsync(
        CompleteProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Result.Failure<UserResponse>(Error.Unauthorized("No hay una sesión activa."));
        }

        var user = await _users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<UserResponse>(Error.EntityNotFound("Usuario", userId));
        }

        var document = request.Document.Trim();
        if (string.IsNullOrWhiteSpace(document))
        {
            return Result.Failure<UserResponse>(Error.Validation(new Dictionary<string, string[]>
            {
                [nameof(request.Document)] = ["El número de documento es obligatorio."]
            }, "El número de documento es obligatorio."));
        }

        var documentoTomado = await _customers.DocumentExistsAsync(document, null, cancellationToken)
            || await _requests.DocumentExistsAsync(document, cancellationToken);

        if (documentoTomado)
        {
            return Result.Failure<UserResponse>(Error.Validation(new Dictionary<string, string[]>
            {
                [nameof(request.Document)] = ["Ese documento ya está registrado."]
            }, "El número de documento ingresado ya se encuentra registrado."));
        }

        var fullName = string.IsNullOrWhiteSpace(request.FullName) ? user.FullName : request.FullName.Trim();

        var existingRequest = await _requests.FindLatestByEmailAsync(user.Email, cancellationToken);
        if (existingRequest is not null)
        {
            existingRequest.Document = document;
            existingRequest.FullName = fullName;
            existingRequest.Age = request.Age;
            existingRequest.Phone = request.Phone.Trim();
            existingRequest.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
            existingRequest.Status = CustomerRequestStatus.Pending;
        }
        else
        {
            await _requests.AddAsync(new CustomerSignupRequest
            {
                UserId = user.Id,
                Document = document,
                FullName = fullName,
                Age = request.Age,
                Email = user.Email,
                Phone = request.Phone.Trim(),
                Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim(),
                Status = CustomerRequestStatus.Pending,
                CreatedAt = _clock.UtcNow
            }, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (_mailService is not null)
        {
            try
            {
                await _mailService.SendWelcomeEmailAsync(user.Email, fullName, cancellationToken);
            }
            catch
            {
                // Fallo de correo no debe tumbar el registro
            }
        }

        var roles = await _users.GetRolesAsync(user.Id, cancellationToken);
        return Result.Success(new UserResponse(
            user.Id,
            user.Email,
            fullName,
            roles,
            CustomerRequestStatus.Pending.ToString()));
    }

    public async Task<Result> DeleteAccountAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Result.Failure(Error.Unauthorized("No hay una sesion activa."));
        }

        var user = await _users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(Error.EntityNotFound("Usuario", userId));
        }

        var roles = await _users.GetRolesAsync(user.Id, cancellationToken);
        if (roles.Contains(ApplicationRoles.Administrator))
        {
            return Result.Failure(Error.Forbidden("No se puede eliminar la cuenta principal de administrador."));
        }

        await _refreshTokens.RevokeAllForUserAsync(user.Id, _clock.UtcNow, cancellationToken);

        var customer = await _customers.FindByEmailAsync(user.Email, cancellationToken);
        if (customer is not null)
        {
            await _customers.RemoveSalesByCustomerIdAsync(customer.Id, cancellationToken);
            _customers.Remove(customer);
        }

        var signupRequest = await _requests.FindLatestByEmailAsync(user.Email, cancellationToken);
        if (signupRequest is not null)
        {
            _requests.Remove(signupRequest);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _users.DeleteAsync(user.Id, cancellationToken);

        return Result.Success();
    }

    private async Task<Result<TokenResponse>> IssueTokensAsync(
        UserAccount user,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken)
    {
        var pair = _tokens.CreateTokenPair(user, roles);
        var now = _clock.UtcNow;

        await _refreshTokens.StoreAsync(
            new RefreshTokenData(
                Guid.NewGuid().ToString("N"),
                user.Id,
                _tokens.HashRefreshToken(pair.RefreshToken),
                pair.RefreshTokenExpiresAt,
                now,
                null,
                null),
            cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var customerStatus = await ResolveCustomerStatusAsync(user.Email, roles, cancellationToken);

        return Result.Success(new TokenResponse(
            "Bearer",
            pair.AccessToken,
            pair.AccessTokenExpiresAt,
            pair.RefreshToken,
            pair.RefreshTokenExpiresAt,
            new UserResponse(user.Id, user.Email, user.FullName, roles, customerStatus, user.AvatarUrl)));
    }

    private async Task<string?> ResolveCustomerStatusAsync(
        string email,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken)
    {
        if (roles.Contains(ApplicationRoles.Administrator))
        {
            return "Approved";
        }

        if (roles.Contains(ApplicationRoles.Customer))
        {
            var customer = await _customers.FindByEmailAsync(email, cancellationToken);
            if (customer is not null)
            {
                return "Approved";
            }

            var request = await _requests.FindLatestByEmailAsync(email, cancellationToken);
            return request?.Status.ToString() ?? "RequiresProfile";
        }

        return null;
    }

    private static Dictionary<string, string[]> MapIdentityErrors(IReadOnlyList<string> errors)
    {
        var failures = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var error in errors)
        {
            var key = string.Empty;
            var message = error;

            if (error.Contains("already taken", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("ya está en uso", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("email", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("user name", StringComparison.OrdinalIgnoreCase))
            {
                key = "Email";
                message = "Ya existe una cuenta registrada con este correo electrónico.";
            }

            failures[key] = failures.TryGetValue(key, out var existing)
                ? [.. existing, message]
                : [message];
        }

        return failures;
    }
}
