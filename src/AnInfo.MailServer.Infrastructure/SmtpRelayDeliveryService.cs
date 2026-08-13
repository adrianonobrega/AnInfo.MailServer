using AnInfo.MailServer.Application;
using AnInfo.MailServer.Domain;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AnInfo.MailServer.Infrastructure;

public sealed class SmtpRelayDeliveryService(
    IOptions<SmtpRelayOptions> options,
    ILogger<SmtpRelayDeliveryService> logger) : IMailDeliveryService
{
    public async Task DeliverAsync(MailMessage storedMessage, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        Validate(settings);
        MimeMessage mime;
        try
        {
            await using var stream = new MemoryStream(storedMessage.RawMime, writable: false);
            mime = await MimeMessage.LoadAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MailDeliveryException("The persisted MIME message is invalid.", true, innerException: ex);
        }

        MailboxAddress envelopeSender;
        List<MailboxAddress> recipients;
        try
        {
            var configuredSender = new MailboxAddress(settings.FromName, settings.FromAddress);
            var originalFrom = mime.From.Mailboxes.FirstOrDefault();
            if (!settings.PreserveOriginalFrom)
            {
                if (originalFrom is not null && !mime.ReplyTo.Mailboxes.Any()) mime.ReplyTo.Add(originalFrom);
                mime.From.Clear();
                mime.From.Add(configuredSender);
                envelopeSender = configuredSender;
            }
            else
            {
                envelopeSender = MailboxAddress.Parse(storedMessage.From);
            }

            recipients = storedMessage.Recipients.Select(x => MailboxAddress.Parse(x.Address))
                .DistinctBy(x => x.Address, StringComparer.OrdinalIgnoreCase).ToList();
            if (recipients.Count == 0) throw new FormatException("No envelope recipients were persisted.");
        }
        catch (Exception ex)
        {
            throw new MailDeliveryException("Sender or recipient address is invalid.", true, innerException: ex);
        }

        using var client = new SmtpClient { Timeout = checked(settings.TimeoutSeconds * 1000) };
        var security = settings.UseSsl ? SecureSocketOptions.SslOnConnect
            : settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        try
        {
            logger.LogInformation("Connecting to SMTP relay {Host}:{Port}", settings.Host, settings.Port);
            await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);
            logger.LogInformation("SMTP connection established");
            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
                logger.LogInformation("SMTP authentication succeeded");
            }

            logger.LogInformation("Sending message {MessageId}", storedMessage.MessageId);
            var response = await client.SendAsync(mime, envelopeSender, recipients, cancellationToken);
            logger.LogInformation("SMTP relay accepted message {MessageId}", storedMessage.MessageId);
            await client.DisconnectAsync(true, cancellationToken);
            _ = response;
        }
        catch (SmtpCommandException ex)
        {
            var status = (int)ex.StatusCode;
            var permanent = status >= 500;
            logger.Log(permanent ? LogLevel.Error : LogLevel.Warning, ex,
                permanent ? "SMTP permanent failure for {MessageId}" : "SMTP temporary failure for {MessageId}", storedMessage.MessageId);
            throw new MailDeliveryException(
                permanent ? "SMTP relay rejected the message permanently." : "SMTP relay reported a temporary failure.",
                permanent, status, Limit(ex.Message), ex);
        }
        catch (MailKit.Security.AuthenticationException ex)
        {
            logger.LogError("SMTP authentication failed");
            throw new MailDeliveryException("SMTP authentication failed.", true, 535, innerException: ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SMTP relay connection or protocol failure for {MessageId}", storedMessage.MessageId);
            throw new MailDeliveryException("SMTP relay connection or protocol failure.", false, innerException: ex);
        }
        finally
        {
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(false, CancellationToken.None); }
                catch { /* Preserve the delivery result; never log credentials or mask the primary exception. */ }
            }
        }
    }

    private static void Validate(SmtpRelayOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host)) throw new MailDeliveryException("SmtpRelay:Host is required in SmtpRelay mode.", true);
        if (settings.Port is < 1 or > 65535) throw new MailDeliveryException("SmtpRelay:Port is invalid.", true);
        if (settings.UseStartTls && settings.UseSsl) throw new MailDeliveryException("Choose STARTTLS or implicit TLS, not both.", true);
        if (string.IsNullOrWhiteSpace(settings.FromAddress)) throw new MailDeliveryException("SmtpRelay:FromAddress is required.", true);
        if (!string.IsNullOrWhiteSpace(settings.Username) && string.IsNullOrEmpty(settings.Password))
            throw new MailDeliveryException("SmtpRelay:Password is required when Username is configured.", true);
        if (settings.TimeoutSeconds is < 1 or > 300) throw new MailDeliveryException("SmtpRelay:TimeoutSeconds must be between 1 and 300.", true);
    }

    private static string Limit(string value) => value.Length <= 2000 ? value : value[..2000];
}
