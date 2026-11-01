using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Data;

public class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> b)
    {
        b.ToTable("applications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
    }
}

public class WebhookConfiguration : IEntityTypeConfiguration<Webhook>
{
    public void Configure(EntityTypeBuilder<Webhook> b)
    {
        b.ToTable("webhooks");
        b.HasKey(x => x.Id);
        // Lets deliveries reference (id, application_id) so a delivery
        // can never point at a webhook from a different application.
        b.HasAlternateKey(x => new { x.Id, x.ApplicationId });
        b.Property(x => x.Url).HasMaxLength(2048).IsRequired();
        b.Property(x => x.Secret).HasMaxLength(256);

        b.HasOne<Application>()
            .WithMany()
            .HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Fan-out reads "active webhooks of application X"
        b.HasIndex(x => x.ApplicationId)
            .HasDatabaseName("ix_webhooks_application_active")
            .HasFilter("is_active");
    }
}

public class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> b)
    {
        b.ToTable("events");
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.Id, x.ApplicationId });
        b.Property(x => x.Type).HasMaxLength(200).IsRequired();
        b.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();

        b.HasOne<Application>()
            .WithMany()
            .HasForeignKey(x => x.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Fan-out worker: "events not yet fanned out"
        b.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("ix_events_pending_fanout")
            .HasFilter("fan_out_completed_at IS NULL");
    }
}

public class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    public void Configure(EntityTypeBuilder<Delivery> b)
    {
        b.ToTable(
            "deliveries",
            t =>
            {
                t.HasCheckConstraint(
                    "ck_deliveries_status",
                    "status IN ('pending','delivered','failed')"
                );
                t.HasCheckConstraint("ck_deliveries_attempt_count", "attempt_count >= 0");
            }
        );

        b.HasKey(x => x.Id);

        b.Property(x => x.Status)
            .HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<DeliveryStatus>(v, true)
            )
            .HasMaxLength(20);

        b.Property(x => x.LastError).HasMaxLength(2000);

        // Composite FKs: the database enforces that webhook and event
        // belong to the same application as the delivery.
        b.HasOne(x => x.Webhook)
            .WithMany()
            .HasForeignKey(x => new { x.WebhookId, x.ApplicationId })
            .HasPrincipalKey(w => new { w.Id, w.ApplicationId })
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Event)
            .WithMany()
            .HasForeignKey(x => new { x.EventId, x.ApplicationId })
            .HasPrincipalKey(e => new { e.Id, e.ApplicationId })
            .OnDelete(DeleteBehavior.Cascade);

        // No duplicate delivery for (application, webhook, event)
        b.HasIndex(x => new
            {
                x.ApplicationId,
                x.WebhookId,
                x.EventId,
            })
            .IsUnique()
            .HasDatabaseName("ux_deliveries_app_webhook_event");

        // The claim index: only pending rows, ordered by due time
        b.HasIndex(x => x.NextAttemptAt)
            .HasDatabaseName("ix_deliveries_claim")
            .HasFilter("status = 'pending'");
    }
}
