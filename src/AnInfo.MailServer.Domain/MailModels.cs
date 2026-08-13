namespace AnInfo.MailServer.Domain;

public enum MailStatus { Pending, Processing, Sent, Failed }
public enum RecipientType { To, Cc, Bcc }

public sealed class MailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MessageId { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public bool IsHtml { get; set; }
    public byte[] RawMime { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public MailStatus Status { get; set; } = MailStatus.Pending;
    public int RetryCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
    public List<MailRecipient> Recipients { get; set; } = [];
    public List<DeliveryAttempt> DeliveryAttempts { get; set; } = [];
}

public sealed class DeliveryAttempt
{
    public long Id { get; set; }
    public Guid MailMessageId { get; set; }
    public MailMessage MailMessage { get; set; } = null!;
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Success { get; set; }
    public int? SmtpStatusCode { get; set; }
    public string? SmtpResponse { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class MailRecipient
{
    public long Id { get; set; }
    public Guid MailMessageId { get; set; }
    public MailMessage MailMessage { get; set; } = null!;
    public string Address { get; set; } = string.Empty;
    public RecipientType Type { get; set; }
}
