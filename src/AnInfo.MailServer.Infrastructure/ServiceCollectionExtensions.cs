using AnInfo.MailServer.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AnInfo.MailServer.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMailServerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpServerOptions>().Bind(configuration.GetSection(SmtpServerOptions.SectionName)).ValidateOnStart();
        services.AddOptions<QueueOptions>().Bind(configuration.GetSection(QueueOptions.SectionName)).ValidateOnStart();
        services.AddOptions<DeliveryOptions>().Bind(configuration.GetSection(DeliveryOptions.SectionName)).ValidateOnStart();
        services.AddOptions<SmtpRelayOptions>().Bind(configuration.GetSection(SmtpRelayOptions.SectionName)).ValidateOnStart();
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required. Supply its password through ConnectionStrings__DefaultConnection.");
        services.AddDbContext<MailDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton<SmtpMessageStore>();
        services.AddSingleton<ConfiguredUserAuthenticator>();
        services.AddScoped<DevelopmentMailDeliveryService>();
        services.AddScoped<SmtpRelayDeliveryService>();
        services.AddScoped<IMailDeliveryService>(provider =>
        {
            var mode = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeliveryOptions>>().Value.Mode;
            return mode.ToUpperInvariant() switch
            {
                "DEVELOPMENT" => provider.GetRequiredService<DevelopmentMailDeliveryService>(),
                "SMTPRELAY" => provider.GetRequiredService<SmtpRelayDeliveryService>(),
                _ => throw new InvalidOperationException("Delivery:Mode must be Development or SmtpRelay.")
            };
        });
        services.AddHostedService<SmtpHostedService>();
        services.AddHostedService<DeliveryWorker>();
        return services;
    }
}
