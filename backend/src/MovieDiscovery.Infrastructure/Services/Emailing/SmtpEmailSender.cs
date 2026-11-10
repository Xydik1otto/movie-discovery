using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MovieDiscovery.Infrastructure.Services.Emailing;

/// <summary>
/// Реальний SMTP через System.Net.Mail.SmtpClient (без нового NuGet).
/// Рекомендація: порт 587 (STARTTLS) — 465 (implicit SSL) SmtpClient не підтримує.
/// Кожен Send — свій SmtpClient (клас не потокобезпечний/повторно не юзається).
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new InvalidOperationException(
                "SMTP_HOST (Email:Host) не задано — вимкніть MAIL_ENABLED або заповніть SMTP-опції.");
        if (string.IsNullOrWhiteSpace(options.From))
            throw new InvalidOperationException("MAIL_FROM (Email:From) не задано.");

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrWhiteSpace(options.User))
            client.Credentials = new NetworkCredential(options.User, options.Password ?? "");

        using var message = new MailMessage(options.From, to)
        {
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
        };
        await client.SendMailAsync(message, ct);
        logger.LogInformation("EMAIL надіслано: {To} | {Subject}", to, subject);
    }
}

/// <summary>Чесний dev-режим (MAIL_ENABLED=false): лист І ЛІНК видні в логах, не тихо.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        logger.LogInformation("EMAIL [SMTP вимкнено] → {To} | {Subject} | {Body}", to, subject, htmlBody);
        return Task.CompletedTask;
    }
}
