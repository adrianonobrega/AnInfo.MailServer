using AnInfo.MailServer.Application;
using AnInfo.MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;

namespace AnInfo.MailServer.Infrastructure;

public sealed class DevelopmentMailDeliveryService(ILogger<DevelopmentMailDeliveryService> logger) : IMailDeliveryService
{
    public Task DeliverAsync(MailMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Development delivery: message {MessageId} would be delivered to {RecipientCount} recipients",
            message.MessageId, message.Recipients.Count);
        return Task.CompletedTask;
    }
}

public sealed class DeliveryWorker(IServiceScopeFactory scopeFactory, IOptions<QueueOptions> options,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Queue delivery worker started");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds)));
        do { await ProcessBatchOnceAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ProcessBatchOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var delivery = scope.ServiceProvider.GetRequiredService<IMailDeliveryService>();
        var now = DateTimeOffset.UtcNow;
        // Claim rows atomically. Concurrent workers skip locks held by another instance and cannot claim duplicates.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var messages = await db.Messages
            .FromSqlInterpolated($$"""
                SELECT * FROM "MailMessages"
                WHERE "Status" = 'Pending'
                  AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {{now}})
                ORDER BY "CreatedAt"
                FOR UPDATE SKIP LOCKED
                LIMIT {{options.Value.BatchSize}}
                """)
            .Include(x => x.Recipients).ToListAsync(ct);
        foreach (var message in messages)
        {
            message.Status = MailStatus.Processing;
            message.LastAttemptAt = now;
            message.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        foreach (var message in messages)
        {
            logger.LogInformation("Processing message {MessageId}", message.MessageId);
            var attempt = new DeliveryAttempt
            {
                MailMessageId = message.Id,
                AttemptNumber = message.RetryCount + 1,
                StartedAt = DateTimeOffset.UtcNow
            };
            db.DeliveryAttempts.Add(attempt);
            await db.SaveChangesAsync(ct);
            try
            {
                logger.LogInformation("Delivery attempt {Attempt} for {MessageId}", message.RetryCount + 1, message.MessageId);
                await delivery.DeliverAsync(message, ct);
                message.Status = MailStatus.Sent; message.SentAt = DateTimeOffset.UtcNow; message.LastError = null;
                attempt.Success = true;
                attempt.FinishedAt = DateTimeOffset.UtcNow;
                logger.LogInformation("Message {MessageId} marked as Sent", message.MessageId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                message.Status = MailStatus.Pending;
                message.UpdatedAt = DateTimeOffset.UtcNow;
                attempt.FinishedAt = DateTimeOffset.UtcNow;
                attempt.ErrorType = nameof(OperationCanceledException);
                attempt.ErrorMessage = "Delivery was cancelled during shutdown.";
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                var deliveryError = ex as MailDeliveryException;
                message.LastError = Limit(ex.Message);
                attempt.Success = false;
                attempt.FinishedAt = DateTimeOffset.UtcNow;
                attempt.SmtpStatusCode = deliveryError?.SmtpStatusCode;
                attempt.SmtpResponse = Limit(deliveryError?.SmtpResponse);
                attempt.ErrorType = ex.GetType().Name;
                attempt.ErrorMessage = Limit(ex.Message);
                if (deliveryError?.IsPermanent == true || message.RetryCount >= options.Value.MaxRetries)
                {
                    message.Status = MailStatus.Failed;
                    logger.LogError(ex, "Message {MessageId} permanently failed", message.MessageId);
                }
                else
                {
                    message.Status = MailStatus.Pending;
                    var delays = options.Value.RetryDelaysSeconds;
                    var delay = delays.Length == 0 ? 30 : delays[Math.Min(message.RetryCount - 1, delays.Length - 1)];
                    message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delay);
                    logger.LogWarning(ex, "Message {MessageId} scheduled for retry at {NextAttempt}", message.MessageId, message.NextAttemptAt);
                }
            }
            message.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string? Limit(string? value) => value is null || value.Length <= 2000 ? value : value[..2000];
}
