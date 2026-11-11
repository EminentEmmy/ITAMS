using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Common;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Infrastructure.Data;

/// <summary>DEVELOPMENT ONLY. Creates one tenant ("nahco") and one user per role.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db, IPasswordHasher hasher, string password, CancellationToken ct = default)
    {
        if (await db.Tenants.AnyAsync(t => t.Id == "nahco", ct)) return;

        db.Tenants.Add(new Tenant { Id = "nahco", Name = "NAHCO (dev seed)" });

        db.Users.AddRange(
            NewUser(hasher, password, "admin@nahco.local", "Dev Tenant Admin", UserRole.TenantAdmin),
            NewUser(hasher, password, "tech@nahco.local", "Dev Technician", UserRole.Technician),
            NewUser(hasher, password, "audit@nahco.local", "Dev Auditor", UserRole.Auditor));

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates the platform owner (SuperAdmin) once, if none exists. The password is temporary:
    /// the owner must choose a new one at first sign-in, after which the secret you supplied is useless.
    /// </summary>
    public static async Task SeedPlatformOwnerAsync(ApplicationDbContext db, IPasswordHasher hasher,
        string email, string fullName, string password, CancellationToken ct = default)
    {
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Role == UserRole.SuperAdmin, ct)) return;

        if (!await db.Tenants.AnyAsync(t => t.Id == PlatformConstants.TenantId, ct))
            db.Tenants.Add(new Tenant { Id = PlatformConstants.TenantId, Name = "ITAMS Platform" });

        db.Users.Add(new AppUser
        {
            TenantId = PlatformConstants.TenantId,
            Email = email.Trim().ToLowerInvariant(),
            FullName = fullName.Trim(),
            Role = UserRole.SuperAdmin,
            PasswordHash = hasher.Hash(password),
            MustChangePassword = true
        });

        await db.SaveChangesAsync(ct);
    }

    private static AppUser NewUser(IPasswordHasher hasher, string password, string email, string name, UserRole role) => new()
    {
        TenantId = "nahco",
        Email = email,
        FullName = name,
        Role = role,
        PasswordHash = hasher.Hash(password)
    };
}
