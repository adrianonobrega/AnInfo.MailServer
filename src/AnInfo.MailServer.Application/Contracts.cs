using AnInfo.MailServer.Domain;

namespace AnInfo.MailServer.Application;

public interface IMailDeliveryService
{
    Task DeliverAsync(MailMessage message, CancellationToken cancellationToken);
}

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";
    public string Mode { get; set; } = "Development";
}

public sealed class SmtpRelayOptions
{
    public const string SectionName = "SmtpRelay";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseStartTls { get; set; } = true;
    public bool UseSsl { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "AnInfo Mail Server";
    public bool PreserveOriginalFrom { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class MailDeliveryException(
    string message, bool isPermanent, int? smtpStatusCode = null,
    string? smtpResponse = null, Exception? innerException = null) : Exception(message, innerException)
{
    public bool IsPermanent { get; } = isPermanent;
    public int? SmtpStatusCode { get; } = smtpStatusCode;
    public string? SmtpResponse { get; } = smtpResponse;
}

public sealed class QueueOptions
{
    public const string SectionName = "Queue";
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxRetries { get; set; } = 3;
    public int[] RetryDelaysSeconds { get; set; } = [5, 30, 120];
    public int BatchSize { get; set; } = 10;
}

public sealed class SmtpServerOptions
{
    public const string SectionName = "SmtpServer";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2525;
    public bool RequireAuthentication { get; set; }
    public bool AllowInsecurePublicBind { get; set; }
    public int MaxMessageSizeBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxRecipients { get; set; } = 100;
    public bool EnableStartTls { get; set; }
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public Dictionary<string, string> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
