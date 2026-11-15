using ITAMS.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ITAMS.Infrastructure.Data.Configurations;

public class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> e)
    {
        e.Property(t => t.TenantId).HasMaxLength(50).IsRequired();
        e.Property(t => t.Title).HasMaxLength(150).IsRequired();
        e.Property(t => t.Description).HasMaxLength(2000).IsRequired();
        e.Property(t => t.Category).HasConversion<string>().HasMaxLength(30);
        e.Property(t => t.Impact).HasConversion<string>().HasMaxLength(30);
        e.Property(t => t.Priority).HasConversion<string>().HasMaxLength(30);
        e.Property(t => t.Status).HasConversion<string>().HasMaxLength(30);
        e.Property(t => t.Source).HasConversion<string>().HasMaxLength(30);
        e.Property(t => t.ReporterName).HasMaxLength(120).IsRequired();
        e.Property(t => t.ReporterContact).HasMaxLength(120);
        e.Property(t => t.Location).HasMaxLength(200);
        e.Property(t => t.AssetReference).HasMaxLength(100);
        e.Property(t => t.AssignedToName).HasMaxLength(200);
        e.Property(t => t.ResolutionNotes).HasMaxLength(2000);

        e.HasIndex(t => new { t.TenantId, t.Number }).IsUnique();   // numbers are per organization
        e.HasIndex(t => new { t.TenantId, t.Status });
        e.HasIndex(t => new { t.TenantId, t.CreatedByUserId });

        e.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId);
        // The tenant query filter is added in ApplicationDbContext (it needs the context's CurrentTenantId).
    }
}

/// <summary>The extra Tenant columns for the public report link.</summary>
public class TenantReportLinkConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> e)
    {
        e.Property(t => t.PublicReportToken).HasMaxLength(64);
        e.HasIndex(t => t.PublicReportToken).IsUnique().HasFilter("[PublicReportToken] IS NOT NULL");
    }
}
