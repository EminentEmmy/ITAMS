using System.Text.Json;
using System.Text.Json.Serialization;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Common;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ITAMS.Infrastructure.Data;

public class ApplicationDbContext : DbContext
{
    private static readonly JsonSerializerOptions AuditJson = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ICurrentUserService _currentUser;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ICurrentUserService currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<MaintenanceLog> MaintenanceLogs => Set<MaintenanceLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SecurityAuditLog> SecurityAuditLogs => Set<SecurityAuditLog>();

    // Read by EF on every query (per DbContext instance = per request).
    // If there is no authenticated user this is null, so every filtered query returns nothing (fail closed).
    private string? CurrentTenantId => _currentUser.TenantId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(50);
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.TenantId).HasMaxLength(50).IsRequired();
            e.Property(u => u.Email).HasMaxLength(200).IsRequired();
            e.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId);
            e.HasQueryFilter(u => u.TenantId == CurrentTenantId);
        });

        b.Entity<Asset>(e =>
        {
            e.Property(a => a.TenantId).HasMaxLength(50).IsRequired();
            e.Property(a => a.SerialNumber).HasMaxLength(100).IsRequired();
            e.Property(a => a.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(a => a.Model).HasMaxLength(100);
            e.Property(a => a.Location).HasMaxLength(200);
            e.Property(a => a.Notes).HasMaxLength(1000);
            e.HasIndex(a => new { a.TenantId, a.SerialNumber }).IsUnique();
            e.HasIndex(a => new { a.TenantId, a.Status });
            e.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId);
            e.HasQueryFilter(a => a.TenantId == CurrentTenantId);
        });

        b.Entity<MaintenanceLog>(e =>
        {
            e.Property(m => m.TenantId).HasMaxLength(50).IsRequired();
            e.Property(m => m.TechnicianName).HasMaxLength(200).IsRequired();
            e.Property(m => m.Description).HasMaxLength(2000).IsRequired();
            e.Property(m => m.PartReplaced).HasMaxLength(200);
            e.HasIndex(m => new { m.TenantId, m.AssetId });
            e.HasOne(m => m.Asset).WithMany(a => a.MaintenanceLogs).HasForeignKey(m => m.AssetId);
            e.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId);
            e.HasQueryFilter(m => m.TenantId == CurrentTenantId);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.TenantId).HasMaxLength(50).IsRequired();
            e.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(t => t.CreatedByIp).HasMaxLength(45);
            e.Property(t => t.UserAgent).HasMaxLength(255);
            e.Property(t => t.ReplacedByTokenHash).HasMaxLength(128);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasQueryFilter(t => t.TenantId == CurrentTenantId);
        });

        b.Entity<SecurityAuditLog>(e =>
        {
            // No FK to Tenant on purpose: failed logins for unknown tenants are still recorded.
            e.Property(l => l.TenantId).HasMaxLength(50).IsRequired();
            e.Property(l => l.UserId).HasMaxLength(100).IsRequired();
            e.Property(l => l.Action).HasMaxLength(100).IsRequired();
            e.Property(l => l.EntityName).HasMaxLength(100).IsRequired();
            e.Property(l => l.EntityId).HasMaxLength(50).IsRequired();
            e.Property(l => l.IpAddress).HasMaxLength(45).IsRequired();
            e.Property(l => l.UserAgent).HasMaxLength(255);
            e.HasIndex(l => new { l.TenantId, l.TimestampUtc });
            e.HasQueryFilter(l => l.TenantId == CurrentTenantId);
        });

        // No cascading deletes anywhere: this is an audit-heavy system.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }

    // ---------------------------------------------------------------------
    // Save pipeline: tenant stamping + automatic audit trail
    // ---------------------------------------------------------------------

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new NotSupportedException("Use SaveChangesAsync so tenant stamping and auditing always run.");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAuditLogIsAppendOnly();
        StampTenant();
        var pending = CaptureAudit();

        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        if (pending.Count > 0)
        {
            // Second save so newly generated Ids exist. Hardening idea for later: wrap both in one transaction.
            foreach (var p in pending) SecurityAuditLogs.Add(BuildLog(p));
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        return result;
    }

    private void StampTenant()
    {
        var tenantId = _currentUser.TenantId;

        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                // Always overwrite: a client can never choose which tenant a row belongs to.
                entry.Entity.TenantId = tenantId
                    ?? throw new InvalidOperationException("Cannot create tenant data without an authenticated tenant.");
            }
            else if (entry.State == EntityState.Modified)
            {
                // Undo any attempt to move the row to another tenant, in memory as well as in the UPDATE,
                // so the audit entry that follows is also recorded under the row's real tenant.
                var tenant = entry.Property(e => e.TenantId);
                tenant.CurrentValue = tenant.OriginalValue;
                tenant.IsModified = false;
            }
        }
    }

    private void GuardAuditLogIsAppendOnly()
    {
        if (ChangeTracker.Entries<SecurityAuditLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("SecurityAuditLogs is append-only.");
    }

    private sealed record PendingAudit(EntityEntry<TenantEntity> Entry, string Action, string? Old, string? New);

    private List<PendingAudit> CaptureAudit()
    {
        var list = new List<PendingAudit>();

        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var name = entry.Entity.GetType().Name.ToUpperInvariant();

            switch (entry.State)
            {
                case EntityState.Added:
                {
                    var action = entry.Entity is MaintenanceLog ? "MAINTENANCE_LOGGED" : $"{name}_CREATED";
                    var values = entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue);
                    list.Add(new PendingAudit(entry, action, null, JsonSerializer.Serialize(values, AuditJson)));
                    break;
                }
                case EntityState.Deleted:
                {
                    var values = entry.Properties.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue);
                    list.Add(new PendingAudit(entry, $"{name}_DELETED", JsonSerializer.Serialize(values, AuditJson), null));
                    break;
                }
                default: // Modified
                {
                    var changed = entry.Properties
                        .Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
                        .ToList();
                    if (changed.Count == 0) break;

                    var action = $"{name}_UPDATED";
                    if (entry.Entity is Asset asset && changed.Any(p => p.Metadata.Name == nameof(Asset.Status)))
                        action = asset.Status == AssetStatus.Decommissioned ? "ASSET_DECOMMISSIONED" : "ASSET_STATUS_CHANGED";

                    var oldValues = changed.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue);
                    var newValues = changed.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue);
                    list.Add(new PendingAudit(entry, action,
                        JsonSerializer.Serialize(oldValues, AuditJson),
                        JsonSerializer.Serialize(newValues, AuditJson)));
                    break;
                }
            }
        }

        return list;
    }

    private SecurityAuditLog BuildLog(PendingAudit p) => new()
    {
        TenantId = p.Entry.Entity.TenantId,
        UserId = _currentUser.UserId ?? "system",
        Action = p.Action,
        EntityName = p.Entry.Entity.GetType().Name,
        EntityId = p.Entry.Entity.Id.ToString(),
        OldValues = p.Old,
        NewValues = p.New,
        IpAddress = Truncate(_currentUser.IpAddress ?? "unknown", 45)!,
        UserAgent = Truncate(_currentUser.UserAgent, 255)
    };

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
