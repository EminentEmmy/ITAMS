# ITAMS development log

IT Asset Management System for airport and cargo operations. Multi-tenant: each customer (airline or ground handler)
sees only its own data. Stack: .NET 10, ASP.NET Core, EF Core, SQL Server, plain HTML/CSS/JavaScript front end.

## Solution layout

```
ITAMS.Domain/           entities, enums, interfaces (no packages)
ITAMS.Infrastructure/   ApplicationDbContext (tenant filter, stamping, audit), password hasher, seeder, migrations
ITAMS.Api/              controllers, auth, middleware, wwwroot (login, dashboard, platform console)
ITAMS.Tests/            tenant isolation tests (xUnit, real SQL Server LocalDB)
docs/                   this log
```

## Run locally

```powershell
dotnet run --project ITAMS.Api --launch-profile https      # then open https://localhost:7180/login.html
dotnet test                                                # stop the app first
```

Secrets live in `dotnet user-secrets` (never in git): `Jwt:Key`, `Seed:AdminPassword`, `Owner:Email`, `Owner:FullName`, `Owner:Password`.
Sign-in needs a company code: `platform` for the owner, `nahco` for the seeded dev organization.

## Security model (summary)

- Sign-in sets two HttpOnly, Secure, SameSite=Strict cookies: a 15 minute access JWT and a 7 day rotating refresh token.
- The JWT carries `sub` (user), `tid` (tenant), `role`, and `mcp` (must change password).
- `ApplicationDbContext` filters every tenant table by `tid`, stamps `TenantId` on every insert, refuses to change it on update,
  and writes an automatic audit entry for every asset or maintenance change. The audit log is append-only.
- Passwords: PBKDF2-SHA256, 600k iterations. 5 failed attempts lock an account for 15 minutes.
- Temporary passwords (new or reset accounts) are generated, shown once, and must be changed at first sign-in.
- CSRF defence: SameSite=Strict plus a required `X-ITAMS-Request` header on every write.
- Strict CSP: no inline scripts or styles, no `innerHTML`.
- Platform endpoints are the only code that bypasses tenant isolation; they require role SuperAdmin **and** membership of the `platform` tenant,
  and are read-only for customer data.

## Milestones

### 1. Scaffold
Domain entities, DbContext, JWT-in-cookie auth with refresh rotation, RBAC (TenantAdmin, Technician, Auditor), asset and maintenance
APIs, rate limiting, security headers, login page and asset dashboard.

### 2. Platform console (owner account)
- Owner is a SuperAdmin in the reserved `platform` tenant, created once from `Owner:*` secrets with a temporary password.
- Console at `/platform.html`: organizations list with live figures, create organization with its first administrator,
  suspend or reactivate, per-organization Users, Assets (read-only) and Activity tabs, add users, reset passwords, disable or enable users.
- Every platform action is audited under the affected organization, attributed to the owner. Customers see it as "ITAMS platform team".
- New column `Users.MustChangePassword` (migration `AddPlatformOwner`).

### 3. Sign-in flow
- After choosing a new password the user is signed out and returned to the sign-in page ("Password updated").
- Pages stay hidden until the script has verified the session. Static files are revalidated on every load (`Cache-Control: no-cache`).

### 4. Staff and audit screens (customer side)
- Tabs by role: Assets (all), Staff (TenantAdmin), Audit trail (TenantAdmin, Auditor).
- Staff: add person, change role, reset password, disable or enable. Admins cannot disable, re-role or reset themselves.
- Audit trail: names instead of ids, category filter, suspicious events marked.
- "Change password" link for every user.

### 5. Tenant isolation tests
- 54 tests against the real app and a throwaway LocalDB database built from the real migrations. Two seeded customers (alpha, beta) plus the platform tenant.
- Covers the data layer, assets, staff, audit, the platform boundary and token tampering.
- Rate limits became configurable (`RateLimits:LoginPerMinute`, `RateLimits:ApiPerMinute`) so tests can relax them and operations can tune them.

## Findings and fixes

| When | Finding | Fix |
|---|---|---|
| Review before first test run | Attempting to move a row to another tenant was blocked in the UPDATE, but the audit entry was written under the other tenant | `StampTenant` now resets the in-memory `TenantId` to its original value as well; test `A_rows_tenant_cannot_be_changed_by_an_update_and_its_audit_entry_stays_with_the_real_tenant` |
| First test run: 53 of 54 passed | `The_staff_list_contains_only_the_administrators_own_people` assumed every `@beta.test` address belongs to Beta, but another test legitimately creates an Alpha account with `tech@beta.test` | Test mistake, not a leak. The assertion now compares ids against Beta's real users instead of email text |

Result after applying the test fix: ______ (fill in the `dotnet test` summary line, for example "total: 54, failed: 0, succeeded: 54").

## Known gaps / next steps

1. Two-factor sign-in for the owner account (it can see every customer).
2. Suspending an organization blocks new sign-ins immediately; people already signed in keep working for up to 15 minutes.
3. Role changes take effect at the user's next sign-in.
4. Audit trail has category filters only: no date range, search or export yet.
5. Forwarded-headers configuration is needed behind a proxy, otherwise the audit "From" column shows the proxy address (`::1` locally).
6. Production hardening: separate database login for migrations (the app login should be read/write only), Key Vault for `Jwt:Key`,
   wrap the audit double-save in one transaction, CI to run `dotnet test` on every push.
7. Bulk asset import from Excel/CSV.
8. Support impersonation for the owner (time-limited and loudly audited), only if needed.

## How to keep this log useful

Add an entry after each working session: what changed, why, which files, and the `dotnet test` result.
