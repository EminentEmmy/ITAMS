# Tenant isolation tests

Automated proof that one customer can never see or change another customer's data. The tests start the real app,
talk to a real SQL Server database (LocalDB) built from your real EF migrations, and attack from every direction.

## Where the files go

Put the new `ITAMS.Tests` folder next to your other projects, and overwrite the three changed files.

```
ITAMS/
├── ITAMS.slnx
├── ITAMS.Domain/
├── ITAMS.Infrastructure/
│   └── Data/ApplicationDbContext.cs        <- overwrite (bug fix, see below)
├── ITAMS.Api/
│   ├── Program.cs                          <- overwrite (3 small changes)
│   └── appsettings.json                    <- overwrite (adds "RateLimits")
└── ITAMS.Tests/                            <- NEW folder
    ├── ITAMS.Tests.csproj
    ├── TestEnvironment.cs                  one throwaway database + seeded customers
    ├── TestHttp.cs
    ├── TenantFilterTests.cs
    ├── AssetIsolationTests.cs
    ├── StaffAndAuditIsolationTests.cs
    ├── PlatformBoundaryTests.cs
    └── TokenBoundaryTests.cs
```

## Run them (PowerShell, from the ITAMS root folder)

```powershell
# stop the running app first (Ctrl+C), it locks the build files
dotnet sln add ITAMS.Tests          # if it complains about several solution files: dotnet sln ITAMS.slnx add ITAMS.Tests
dotnet test
```

Each run creates a database called `ITAMS_Test_<random>` in LocalDB and deletes it afterwards.
Not on LocalDB? Set `ITAMS_TEST_SQL` to a server connection string without a database, for example
`$env:ITAMS_TEST_SQL = "Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True"`.

## What is proven (about 50 checks)

| File | What it proves |
|---|---|
| `TenantFilterTests` | Every query returns only the current tenant's rows. With nobody signed in, nothing is visible. New rows are stamped with the real tenant whatever the caller claims. A row cannot be moved to another tenant, and its audit entry stays with the real tenant. The audit log cannot be edited or deleted. |
| `AssetIsolationTests` | Over HTTP: lists, search, read, update, decommission and maintenance on another tenant's asset all answer 404 and change nothing. A tenant id inside the request body is ignored. Duplicate-serial checks do not reveal another customer's serials. Role limits hold (auditor read-only, technician cannot decommission). |
| `StaffAndAuditIsolationTests` | An administrator cannot list, reset, disable or re-role another tenant's staff. New users always land in the caller's tenant. The same email can exist in two tenants without leaking. Admins cannot lock themselves out. The audit trail only ever shows the caller's own tenant. |
| `PlatformBoundaryTests` | Customer admins cannot use platform endpoints. A user with the SuperAdmin role inside a customer tenant is still refused. The owner can look at every tenant but cannot change customer data. The `platform` code cannot be registered. A suspended organization cannot sign in. |
| `TokenBoundaryTests` | Right password under the wrong company code fails. A token with an edited tenant claim, a token signed with the wrong key, and garbage in the cookie are all rejected. A write without the anti-forgery header is refused. |

## Proof that the tests can fail (do this once)

A test suite that cannot fail proves nothing. Try this:

1. In `ITAMS.Infrastructure/Data/ApplicationDbContext.cs`, comment out the line
   `e.HasQueryFilter(a => a.TenantId == CurrentTenantId);` inside `b.Entity<Asset>(...)`.
2. Run `dotnet test`. Several tests must now fail (asset listing, search, update, decommission, platform view).
3. Put the line back and run again: everything passes.

## Keeping it safe as the product grows

Every new endpoint that touches tenant data needs three tests before it ships:

1. **Cross-read**: tenant A asks for tenant B's record, expect 404 and no data.
2. **Cross-write**: tenant A tries to change or delete tenant B's record, expect 404 and the record unchanged (check with `env.Raw()` and `IgnoreQueryFilters()`).
3. **Body tenant**: send `"tenantId":"beta"` in the body, expect it ignored.

Also: when you add a new entity with a `TenantId`, give it a `HasQueryFilter`, and add it to `Every_query_returns_only_rows_of_the_current_tenant`.

## A bug these tests exposed (already fixed in the files above)

If code tried to move a row to another tenant, the database update was correctly blocked, but the audit entry
for that change would have been written under the *other* tenant. `ApplicationDbContext.StampTenant` now resets
the in-memory value as well, and a test locks the behaviour in.

## Notes

- I could not run `dotnet test` in my environment (no .NET or SQL Server there). The tests are written against your actual code, but run them and send me any failure output: a failure is either a mistake in a test or a real hole, and both are worth fixing.
- Optional CI (GitHub Actions) is easy to add later, using a SQL Server service container and the `ITAMS_TEST_SQL` variable.
