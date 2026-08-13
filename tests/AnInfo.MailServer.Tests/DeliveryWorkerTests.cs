using AnInfo.MailServer.Application;
using AnInfo.MailServer.Domain;
using AnInfo.MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AnInfo.MailServer.Tests;

public sealed class DeliveryWorkerTests
{
    public static TheoryData<string, bool, bool, MailStatus, int, int?> Cases => new()
    {
        { "Success", false, false, MailStatus.Sent, 0, null },
        { "Temporary", true, false, MailStatus.Pending, 1, 450 },
        { "Permanent", true, true, MailStatus.Failed, 1, 550 },
        { "Authentication", true, true, MailStatus.Failed, 1, 535 }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Worker_records_attempt_and_applies_failure_policy(
        string scenario, bool throws, bool permanent, MailStatus expectedStatus, int expectedRetries, int? statusCode)
    {
        var admin = Environment.GetEnvironmentVariable("ANINFO_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set ANINFO_TEST_POSTGRES before running PostgreSQL integration tests.");
        var database = $"aninfo_worker_{Guid.NewGuid():N}";
        var testConnection = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
        await ExecuteAdminAsync(admin, $"CREATE DATABASE \"{database}\"");
        var services = new ServiceCollection();
        services.AddDbContext<MailDbContext>(o => o.UseNpgsql(testConnection));
        services.AddSingleton<IMailDeliveryService>(new FakeDeliveryService(throws, permanent, statusCode));
        var provider = services.BuildServiceProvider();
        try
        {
            Guid id;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
                await db.Database.MigrateAsync();
                var message = new MailMessage { MessageId = $"{scenario}@test", From = "sender@example.test", RawMime = [1] };
                db.Messages.Add(message); await db.SaveChangesAsync(); id = message.Id;
            }

            var worker = new DeliveryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new QueueOptions { MaxRetries = 3, RetryDelaysSeconds = [1] }), NullLogger<DeliveryWorker>.Instance);
            await worker.ProcessBatchOnceAsync(CancellationToken.None);

            await using var verificationScope = provider.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<MailDbContext>();
            var saved = await verificationDb.Messages.SingleAsync(x => x.Id == id);
            var attempt = await verificationDb.DeliveryAttempts.SingleAsync(x => x.MailMessageId == id);
            Assert.Equal(expectedStatus, saved.Status);
            Assert.Equal(expectedRetries, saved.RetryCount);
            Assert.Equal(!throws, attempt.Success);
            Assert.Equal(statusCode, attempt.SmtpStatusCode);
            Assert.NotNull(attempt.FinishedAt);
            Assert.Equal(throws && !permanent, saved.NextAttemptAt is not null);
        }
        finally
        {
            await provider.DisposeAsync(); NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeDeliveryService(bool throws, bool permanent, int? statusCode) : IMailDeliveryService
    {
        public Task DeliverAsync(MailMessage message, CancellationToken cancellationToken) => throws
            ? Task.FromException(new MailDeliveryException("Synthetic delivery failure.", permanent, statusCode, "Synthetic SMTP response"))
            : Task.CompletedTask;
    }
}
