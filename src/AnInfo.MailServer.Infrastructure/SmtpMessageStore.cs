using System.Buffers;
using AnInfo.MailServer.Application;
using AnInfo.MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace AnInfo.MailServer.Infrastructure;

public sealed class SmtpMessageStore(
    IServiceScopeFactory scopeFactory,
    IOptions<SmtpServerOptions> options,
    ILogger<SmtpMessageStore> logger) : MessageStore
{
    public override async Task<SmtpResponse> SaveAsync(
        ISessionContext context, IMessageTransaction transaction,
        ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Length > options.Value.MaxMessageSizeBytes)
            return SmtpResponse.SizeLimitExceeded;
        if (transaction.To.Count > options.Value.MaxRecipients)
            return SmtpResponse.TransactionFailed;

        try
        {
            await using var stream = new MemoryStream(buffer.ToArray(), writable: false);
            var mime = await MimeMessage.LoadAsync(stream, cancellationToken);
            var raw = buffer.ToArray();
            var envelopeRecipients = transaction.To.Select(FormatAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var cc = mime.Cc.Mailboxes.Select(x => x.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bcc = mime.Bcc.Mailboxes.Select(x => x.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entity = new MailMessage
            {
                MessageId = string.IsNullOrWhiteSpace(mime.MessageId) ? MimeUtils.GenerateMessageId() : mime.MessageId,
                From = FormatAddress(transaction.From), Subject = mime.Subject,
                Body = mime.HtmlBody ?? mime.TextBody, IsHtml = mime.HtmlBody is not null, RawMime = raw
            };
            foreach (var address in envelopeRecipients)
                entity.Recipients.Add(new MailRecipient { Address = address,
                    Type = bcc.Contains(address) ? RecipientType.Bcc : cc.Contains(address) ? RecipientType.Cc : RecipientType.To });

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            db.Messages.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Message {MessageId} from {From} to {Recipients} persisted as {MailId}",
                entity.MessageId, entity.From, string.Join(",", envelopeRecipients), entity.Id);
            return SmtpResponse.Ok;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMTP message could not be validated or persisted");
            return SmtpResponse.TransactionFailed;
        }
    }

    private static string FormatAddress(IMailbox mailbox) => $"{mailbox.User}@{mailbox.Host}";
}
