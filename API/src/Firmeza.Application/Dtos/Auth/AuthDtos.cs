using System.ComponentModel.DataAnnotations;

namespace Firmeza.Application.Dtos.Auth;

public sealed class LoginRequest
{
    [Display(Name = "Correo")]
    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "Ingresa un correo valido.")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Contrasena")]
    [Required(ErrorMessage = "La contrasena es obligatoria.")]
    public string Password { get; set; } = string.Empty;
}

public sealed class RegisterRequest
{
    [Display(Name = "Documento")]
    [Required(ErrorMessage = "El documento es obligatorio.")]
    [StringLength(30)]
    public string Document { get; set; } = string.Empty;

    [Display(Name = "Nombre completo")]
    [Required(ErrorMessage = "El nombre es obligatorio.")]
    [RegularExpression(@"^[a-zA-ZÁÉÍÓÚÜÑáéíóúüñ' -]+$", ErrorMessage = "El nombre solo puede contener letras y espacios.")]
    [StringLength(120)]
    public string FullName { get; set; } = string.Empty;

    [Display(Name = "Edad")]
    [Range(18, 120, ErrorMessage = "La edad debe estar entre 18 y 120 anios.")]
    public int Age { get; set; }

    [Display(Name = "Correo")]
    [Required(ErrorMessage = "El correo es obligatorio.")]
    [EmailAddress(ErrorMessage = "Ingresa un correo valido.")]
    [StringLength(160)]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "Telefono")]
    [Required(ErrorMessage = "El telefono es obligatorio.")]
    [StringLength(30)]
    public string Phone { get; set; } = string.Empty;

    [Display(Name = "Direccion")]
    [StringLength(240)]
    public string? Address { get; set; }

    [Display(Name = "Contrasena")]
    [Required(ErrorMessage = "La contrasena es obligatoria.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "La contrasena debe tener al menos 8 caracteres.")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "Confirmar contrasena")]
    [Compare(nameof(Password), ErrorMessage = "Las contrasenas no coinciden.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class RefreshTokenRequest
{
    [Display(Name = "Refresh token")]
    [Required(ErrorMessage = "El refresh token es obligatorio.")]
    public string RefreshToken { get; set; } = string.Empty;
}

public sealed class CompleteProfileRequest
{
    [Display(Name = "Documento")]
    [Required(ErrorMessage = "El documento es obligatorio.")]
    public string Document { get; set; } = string.Empty;

    [Display(Name = "Nombre completo")]
    public string? FullName { get; set; }

    [Display(Name = "Edad")]
    [Range(18, 120, ErrorMessage = "Debes tener al menos 18 años.")]
    public int Age { get; set; } = 18;

    [Display(Name = "Teléfono")]
    [Required(ErrorMessage = "El teléfono o celular es obligatorio.")]
    public string Phone { get; set; } = string.Empty;

    [Display(Name = "Dirección")]
    public string? Address { get; set; }
}

public sealed class UpdateProfileRequest
{
    [Display(Name = "Nombre completo")]
    [Required(ErrorMessage = "El nombre es obligatorio.")]
    [StringLength(120, MinimumLength = 3, ErrorMessage = "El nombre debe tener entre 3 y 120 caracteres.")]
    public string FullName { get; set; } = string.Empty;

    [Display(Name = "Teléfono")]
    [StringLength(30)]
    public string? Phone { get; set; }

    [Display(Name = "Dirección")]
    [StringLength(240)]
    public string? Address { get; set; }

    [Display(Name = "Foto de perfil")]
    [StringLength(500)]
    public string? AvatarUrl { get; set; }
}

public sealed record RequestPasswordResetCodeResponse(
    string Email,
    string Message,
    int ExpiresInMinutes);

public sealed class ChangePasswordWithCodeRequest
{
    [Display(Name = "Código de verificación")]
    [Required(ErrorMessage = "El código de verificación es obligatorio.")]
    [StringLength(6, MinimumLength = 6, ErrorMessage = "El código debe tener exactamente 6 dígitos.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "El código solo debe contener números.")]
    public string Code { get; set; } = string.Empty;

    [Display(Name = "Nueva contraseña")]
    [Required(ErrorMessage = "La nueva contraseña es obligatoria.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "La contraseña debe tener al menos 8 caracteres.")]
    public string NewPassword { get; set; } = string.Empty;

    [Display(Name = "Confirmar contraseña")]
    [Compare(nameof(NewPassword), ErrorMessage = "Las contraseñas no coinciden.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed record TokenResponse(
    string TokenType,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserResponse User);

public sealed record UserResponse(
    string Id,
    string Email,
    string FullName,
    IReadOnlyList<string> Roles,
    string? CustomerStatus = null,
    string? AvatarUrl = null,
    string? Phone = null,
    string? Address = null);
