using System.Net;
using System.Security.Cryptography;
using System.Text;
using AnInfo.MailServer.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.ComponentModel;

namespace AnInfo.MailServer.Infrastructure;

public sealed class ConfiguredUserAuthenticator(IOptions<SmtpServerOptions> options) : IUserAuthenticator, IUserAuthenticatorFactory
{
    public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken token)
    {
        if (!options.Value.Users.TryGetValue(user, out var expected)) return Task.FromResult(false);
        var left = Encoding.UTF8.GetBytes(expected); var right = Encoding.UTF8.GetBytes(password);
        return Task.FromResult(left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right));
    }
    public IUserAuthenticator CreateInstance(ISessionContext context) => this;
}

public sealed class SmtpHostedService(
    SmtpMessageStore messageStore, ConfiguredUserAuthenticator authenticator,
    IOptions<SmtpServerOptions> options, ILogger<SmtpHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        Validate(settings);
        var address = IPAddress.Parse(settings.Host);
        var builder = new SmtpServerOptionsBuilder().ServerName("AnInfo.MailServer")
            .MaxMessageSize(settings.MaxMessageSizeBytes, MaxMessageSizeHandling.Strict)
            .Endpoint(endpoint => endpoint.Endpoint(new IPEndPoint(address, settings.Port))
                .AuthenticationRequired(settings.RequireAuthentication)
                .AllowUnsecureAuthentication(!settings.EnableStartTls));
        var serviceProvider = new ServiceProvider();
        serviceProvider.Add(messageStore);
        serviceProvider.Add((IUserAuthenticatorFactory)authenticator);
        var server = new global::SmtpServer.SmtpServer(builder.Build(), serviceProvider);
        server.SessionCreated += (_, args) => logger.LogInformation("SMTP connection received; session {SessionId}", args.Context.SessionId);
        server.SessionCompleted += (_, args) => logger.LogInformation("SMTP connection completed; session {SessionId}", args.Context.SessionId);
        server.SessionFaulted += (_, args) => logger.LogWarning(args.Exception, "SMTP connection faulted; session {SessionId}", args.Context.SessionId);
        logger.LogInformation("SMTP server listening on {Host}:{Port}; authentication required: {RequireAuthentication}",
            settings.Host, settings.Port, settings.RequireAuthentication);
        await server.StartAsync(stoppingToken);
    }

    private static void Validate(SmtpServerOptions settings)
    {
        if (!IPAddress.TryParse(settings.Host, out var address))
            throw new InvalidOperationException("SmtpServer:Host must be an IP address to define the bind interface explicitly.");
        if (!IPAddress.IsLoopback(address) && !settings.RequireAuthentication && !settings.AllowInsecurePublicBind)
            throw new InvalidOperationException("Anonymous SMTP on a non-loopback interface is blocked. Enable authentication or explicitly set AllowInsecurePublicBind=true for an isolated development environment.");
        if (!IPAddress.IsLoopback(address) && settings.RequireAuthentication && !settings.EnableStartTls)
            throw new InvalidOperationException("Authenticated SMTP on a non-loopback interface requires STARTTLS; clear-text credentials are blocked.");
        if (settings.RequireAuthentication && settings.Users.Count == 0)
            throw new InvalidOperationException("SMTP authentication is required but no users were configured.");
        if (settings.EnableStartTls)
            throw new InvalidOperationException("STARTTLS configuration is reserved for the next delivery phase; do not expose this version publicly.");
    }
}
