using AnInfo.MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnInfo.MailServer.Infrastructure;

public sealed class MailDbContext(DbContextOptions<MailDbContext> options) : DbContext(options)
{
    public DbSet<MailMessage> Messages => Set<MailMessage>();
    public DbSet<MailRecipient> Recipients => Set<MailRecipient>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var message = modelBuilder.Entity<MailMessage>();
        message.ToTable("MailMessages").HasKey(x => x.Id);
        message.Property(x => x.MessageId).HasMaxLength(998).IsRequired();
        message.Property(x => x.From).HasMaxLength(320).IsRequired();
        message.Property(x => x.Subject).HasMaxLength(998);
        message.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        message.HasIndex(x => new { x.Status, x.NextAttemptAt });
        message.HasIndex(x => new { x.Status, x.CreatedAt });
        message.HasIndex(x => x.MessageId);
        message.HasMany(x => x.Recipients).WithOne(x => x.MailMessage)
            .HasForeignKey(x => x.MailMessageId).OnDelete(DeleteBehavior.Cascade);
        message.HasMany(x => x.DeliveryAttempts).WithOne(x => x.MailMessage)
            .HasForeignKey(x => x.MailMessageId).OnDelete(DeleteBehavior.Cascade);

        var recipient = modelBuilder.Entity<MailRecipient>();
        recipient.ToTable("MailRecipients").HasKey(x => x.Id);
        recipient.Property(x => x.Address).HasMaxLength(320).IsRequired();
        recipient.Property(x => x.Type).HasConversion<string>().HasMaxLength(10);

        var attempt = modelBuilder.Entity<DeliveryAttempt>();
        attempt.ToTable("DeliveryAttempts").HasKey(x => x.Id);
        attempt.Property(x => x.SmtpResponse).HasMaxLength(2000);
        attempt.Property(x => x.ErrorType).HasMaxLength(200);
        attempt.Property(x => x.ErrorMessage).HasMaxLength(2000);
        attempt.HasIndex(x => new { x.MailMessageId, x.AttemptNumber }).IsUnique();
        attempt.HasIndex(x => x.StartedAt);
    }
}
