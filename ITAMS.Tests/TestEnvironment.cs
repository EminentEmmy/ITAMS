using System.Collections.Concurrent;
using System.Net.Http.Json;
using ITAMS.Domain.Abstractions;
using ITAMS.Domain.Common;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using ITAMS.Infrastructure.Data;
using ITAMS.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Tests;

/// <summary>Stands in for the signed-in user when a test talks to the DbContext directly.</summary>
public sealed class FakeCurrentUser(string? tenantId, string? userId = null) : ICurrentUserService
{
    public string? TenantId { get; } = tenantId;
    public string? UserId { get; } = userId;
    public string? FullName => "Test User";
    public string? IpAddress => "127.0.0.1";
    public string? UserAgent => "ITAMS.Tests";
    public bool IsAuthenticated => TenantId is not null;
}

public sealed class ItamsFactory : WebApplicationFactory<Program>
{
    // "Testing" skips the Development-only auto-migrate and seeding: the tests build their own data.
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
}

[CollectionDefinition("Isolation")]
public class IsolationCollection : ICollectionFixture<TestEnvironment> { }

/// <summary>
/// One throwaway SQL Server database per test run, created from the real EF migrations and filled with
/// three tenants: "alpha" and "beta" (two competing customers) and "platform" (the owner).
/// Set ITAMS_TEST_SQL to a server connection string (without a database) to use something other than LocalDB.
/// </summary>
public sealed class TestEnvironment : IAsyncLifetime
{
    public const string Password = "Test-Password-123!";

    private readonly string _dbName = $"ITAMS_Test_{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, Lazy<Task<HttpClient>>> _clients = new();

    public ItamsFactory Factory { get; private set; } = default!;
    private DbContextOptions<ApplicationDbContext> _options = default!;

    // ------------------------------------------------------------------ lifetime
    public async Task InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("ITAMS_TEST_SQL")
            ?? @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";
        var connection = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = _dbName }.ConnectionString;

        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection).Options;

        await using (var db = Db(null))
            await db.Database.MigrateAsync();     // also proves the migrations match the model

        await SeedAsync();

        // Environment variables are read by the app the moment it starts, so set them before the first client is created.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connection);
        Environment.SetEnvironmentVariable("Jwt__Key", "test-only-signing-key-that-is-longer-than-32-bytes!!");
        Environment.SetEnvironmentVariable("RateLimits__LoginPerMinute", "100000");
        Environment.SetEnvironmentVariable("RateLimits__ApiPerMinute", "100000");

        Factory = new ItamsFactory();
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients.Values)
            if (client.IsValueCreated) (await client.Value).Dispose();

        Factory?.Dispose();
        await using var db = Db(null);
        await db.Database.EnsureDeletedAsync();
    }

    // ------------------------------------------------------------------ database access
    /// <summary>A DbContext acting as a signed-in member of <paramref name="tenantId"/> (null = nobody signed in).</summary>
    public ApplicationDbContext Db(string? tenantId, string? userId = null) =>
        new(_options, new FakeCurrentUser(tenantId, userId));

    /// <summary>For ground truth: pair with IgnoreQueryFilters() to see every tenant's rows.</summary>
    public ApplicationDbContext Raw() => Db(null);

    public async Task<int> AssetIdAsync(string tenant, string serial)
    {
        await using var db = Raw();
        return await db.Assets.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenant && a.SerialNumber == serial).Select(a => a.Id).SingleAsync();
    }

    public async Task<List<int>> AssetIdsAsync(string tenant)
    {
        await using var db = Raw();
        return await db.Assets.IgnoreQueryFilters().Where(a => a.TenantId == tenant).Select(a => a.Id).ToListAsync();
    }

    public async Task<Asset> AssetRowAsync(int id)
    {
        await using var db = Raw();
        return await db.Assets.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == id);
    }

    public async Task<Guid> UserIdAsync(string tenant, string email)
    {
        await using var db = Raw();
        return await db.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenant && u.Email == email).Select(u => u.Id).SingleAsync();
    }

    public async Task<AppUser> UserRowAsync(Guid id)
    {
        await using var db = Raw();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == id);
    }

    public async Task<List<long>> AuditIdsAsync(string tenant)
    {
        await using var db = Raw();
        return await db.SecurityAuditLogs.IgnoreQueryFilters().Where(l => l.TenantId == tenant).Select(l => l.Id).ToListAsync();
    }

    public async Task CreateTenantWithAdminAsync(string tenantId, string email)
    {
        await using var db = Raw();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = tenantId });
        db.Users.Add(NewUser(tenantId, email, UserRole.TenantAdmin, new Pbkdf2PasswordHasher().Hash(Password)));
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ HTTP access
    public HttpClient NewClient(bool handleCookies = true, bool csrfHeader = true)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),   // the auth cookies are Secure
            HandleCookies = handleCookies,
            AllowAutoRedirect = false
        });
        if (csrfHeader) client.DefaultRequestHeaders.Add("X-ITAMS-Request", "1");   // what the real front end always sends
        return client;
    }

    /// <summary>A client that is signed in as the user (cached, so each user logs in once per test run).</summary>
    public Task<HttpClient> SignInAsync(string tenant, string email) =>
        _clients.GetOrAdd($"{tenant}|{email}", _ => new Lazy<Task<HttpClient>>(() => LoginAsync(tenant, email))).Value;

    private async Task<HttpClient> LoginAsync(string tenant, string email)
    {
        var client = NewClient();
        var res = await TrySignInAsync(client, tenant, email, Password);
        Assert.True(res.IsSuccessStatusCode, $"Could not sign in {email} to {tenant}: {(int)res.StatusCode}");
        return client;
    }

    public static Task<HttpResponseMessage> TrySignInAsync(HttpClient client, string tenant, string email, string password)
    {
        return client.PostAsJsonAsync("/api/auth/login", new { tenantId = tenant, email, password });
    }

    // ------------------------------------------------------------------ seed data
    private async Task SeedAsync()
    {
        var hash = new Pbkdf2PasswordHasher().Hash(Password);   // hashed once, shared, to keep set-up fast

        await using (var db = Db(null))
        {
            db.Tenants.AddRange(
                new Tenant { Id = "alpha", Name = "Alpha Airlines" },
                new Tenant { Id = "beta", Name = "Beta Cargo" },
                new Tenant { Id = PlatformConstants.TenantId, Name = "ITAMS Platform" });

            db.Users.AddRange(
                NewUser("alpha", "admin@alpha.test", UserRole.TenantAdmin, hash),
                NewUser("alpha", "tech@alpha.test", UserRole.Technician, hash),
                NewUser("alpha", "audit@alpha.test", UserRole.Auditor, hash),
                // A user whose ROLE says SuperAdmin but who lives in a customer tenant: must NOT be a platform owner.
                NewUser("alpha", "super@alpha.test", UserRole.SuperAdmin, hash),
                NewUser("beta", "admin@beta.test", UserRole.TenantAdmin, hash),
                NewUser("beta", "tech@beta.test", UserRole.Technician, hash),
                NewUser(PlatformConstants.TenantId, "owner@platform.test", UserRole.SuperAdmin, hash));

            await db.SaveChangesAsync();
        }

        // SHARED-001 exists in BOTH tenants on purpose: serial numbers only have to be unique per customer.
        await SeedTenantDataAsync("alpha", "admin@alpha.test", "ALPHA-1", "ALPHA-2", "ALPHA-ONLY-1", "SHARED-001");
        await SeedTenantDataAsync("beta", "admin@beta.test", "BETA-1", "BETA-2", "SHARED-001");
    }

    private async Task SeedTenantDataAsync(string tenant, string adminEmail, params string[] serials)
    {
        var adminId = await UserIdAsync(tenant, adminEmail);

        // Inserted as that tenant: TenantId is stamped by the DbContext, exactly as in production.
        await using var db = Db(tenant, adminId.ToString());
        foreach (var serial in serials)
            db.Assets.Add(new Asset
            {
                SerialNumber = serial, Type = AssetType.SystemUnit, Model = "Seed model",
                Location = $"{tenant} desk", Status = AssetStatus.Deployed
            });
        await db.SaveChangesAsync();

        var first = await db.Assets.OrderBy(a => a.Id).FirstAsync();
        db.MaintenanceLogs.Add(new MaintenanceLog
        {
            AssetId = first.Id, TechnicianId = adminId, TechnicianName = "Seed Admin",
            Description = $"{tenant} seed maintenance"
        });
        await db.SaveChangesAsync();
    }

    private static AppUser NewUser(string tenant, string email, UserRole role, string hash) => new()
    {
        TenantId = tenant, Email = email, FullName = email, Role = role, PasswordHash = hash, MustChangePassword = false
    };
}
