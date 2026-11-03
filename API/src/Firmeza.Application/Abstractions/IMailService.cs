using System.Threading;
using System.Threading.Tasks;
using Firmeza.Domain.Entities;

namespace Firmeza.Application.Abstractions;

public interface IMailService
{
    Task SendSaleReceiptEmailAsync(
        string recipientEmail,
        string recipientName,
        Sale sale,
        byte[] pdfReceiptBytes,
        CancellationToken cancellationToken = default);

    Task SendWelcomeEmailAsync(
        string recipientEmail,
        string recipientName,
        CancellationToken cancellationToken = default);

    Task SendAccountApprovedEmailAsync(
        string recipientEmail,
        string recipientName,
        CancellationToken cancellationToken = default);

    Task SendPasswordResetCodeEmailAsync(
        string recipientEmail,
        string recipientName,
        string code,
        int expiresInMinutes = 10,
        CancellationToken cancellationToken = default);
}
