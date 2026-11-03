using System;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

namespace Firmeza.Infrastructure.Mail;

public sealed class SystemSmtpTransport : ISmtpTransport
{
    private readonly MailOptions _options;

    public SystemSmtpTransport(IOptions<MailOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendMailAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = 8000
        };

        if (!string.IsNullOrWhiteSpace(_options.UserName) && !string.IsNullOrWhiteSpace(_options.Password))
        {
            var cleanedPassword = _options.Password.Replace(" ", "").Trim();
            client.Credentials = new NetworkCredential(_options.UserName, cleanedPassword);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        await client.SendMailAsync(message, cts.Token);
    }
}
