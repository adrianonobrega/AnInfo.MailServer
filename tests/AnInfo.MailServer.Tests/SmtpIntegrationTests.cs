using System.Net;
using System.Net.Sockets;
using AnInfo.MailServer.Application;
using AnInfo.MailServer.Domain;
using AnInfo.MailServer.Infrastructure;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;

namespace AnInfo.MailServer.Tests;

public sealed class SmtpIntegrationTests
{
    [Fact]
    public async Task Client_message_is_persisted_as_pending_with_envelope()
    {
        var adminConnection = Environment.GetEnvironmentVariable("ANINFO_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set ANINFO_TEST_POSTGRES to a PostgreSQL admin connection string (normally the Compose postgres database).");
        var databaseName = $"aninfo_test_{Guid.NewGuid():N}";
        var testBuilder = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName };
        await CreateDatabaseAsync(adminConnection, databaseName);
        var port = GetFreePort();
        var services = new ServiceCollection().AddDbContext<MailDbContext>(o => o.UseNpgsql(testBuilder.ConnectionString));
        var provider = services.BuildServiceProvider();
        try
        {
            await using (var scope = provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<MailDbContext>().Database.MigrateAsync();

            var settings = Options.Create(new SmtpServerOptions { Host = "127.0.0.1", Port = port, MaxRecipients = 10 });
            var store = new SmtpMessageStore(provider.GetRequiredService<IServiceScopeFactory>(), settings, NullLogger<SmtpMessageStore>.Instance);
            var auth = new ConfiguredUserAuthenticator(settings);
            var server = new SmtpHostedService(store, auth, settings, NullLogger<SmtpHostedService>.Instance);
            await server.StartAsync(CancellationToken.None);
            try
            {
                await WaitForPortAsync(port);
                var mime = new MimeMessage();
                mime.From.Add(MailboxAddress.Parse("sender@example.test"));
                mime.To.Add(MailboxAddress.Parse("recipient@example.test"));
                mime.Subject = "Integration test";
                mime.Body = new TextPart("plain") { Text = "Persist me" };
                using var client = new SmtpClient();
                await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
                await client.SendAsync(mime);
                await client.DisconnectAsync(true);

                await using var scope = provider.CreateAsyncScope();
                var saved = await scope.ServiceProvider.GetRequiredService<MailDbContext>().Messages.Include(x => x.Recipients).SingleAsync();
                Assert.Equal("sender@example.test", saved.From);
                Assert.Equal("Integration test", saved.Subject);
                Assert.Equal(MailStatus.Pending, saved.Status);
                Assert.Equal("recipient@example.test", Assert.Single(saved.Recipients).Address);
                Assert.NotEmpty(saved.RawMime);
                Assert.Equal(TimeSpan.Zero, saved.CreatedAt.Offset);
            }
            finally { await server.StopAsync(CancellationToken.None); }
        }
        finally
        {
            await provider.DisposeAsync();
            NpgsqlConnection.ClearAllPools();
            await DropDatabaseAsync(adminConnection, databaseName);
        }
    }

    private static async Task CreateDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (var i = 0; i < 50; i++)
        {
            try { using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port); return; }
            catch (SocketException) { await Task.Delay(20); }
        }
        throw new TimeoutException("SMTP test server did not start.");
    }
}
