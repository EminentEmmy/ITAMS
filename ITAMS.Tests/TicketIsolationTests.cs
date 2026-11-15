using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ITAMS.Api.Dtos;
using ITAMS.Domain.Entities;
using ITAMS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Tests;

/// <summary>Tickets: the public door (no sign-in) and the signed-in screens must keep every organization apart.</summary>
[Collection("Isolation")]
public class TicketIsolationTests(TestEnvironment env)
{
    private static object ValidReport(string title = "Boarding printer is jammed") => new
    {
        name = "Test Reporter", contact = "ext 123", location = "T2 desk 14", assetReference = "PRN-9",
        category = "Printer", impact = "Blocking", title,
        description = "The boarding pass printer is jammed and will not feed paper.", website = ""
    };

    private static int NumberFrom(string reference) => int.Parse(reference["TKT-".Length..]);

    // ================================================================ data layer
    [Fact]
    public async Task Tickets_are_filtered_by_tenant_and_invisible_without_one()
    {
        await using var alpha = env.Db("alpha");
        await using var nobody = env.Db(null);
        await using var raw = env.Raw();

        var mine = await alpha.Tickets.ToListAsync();
        Assert.NotEmpty(mine);
        Assert.All(mine, t => Assert.Equal("alpha", t.TenantId));
        Assert.Empty(await nobody.Tickets.ToListAsync());
        Assert.Contains("beta", await raw.Tickets.IgnoreQueryFilters().Select(t => t.TenantId).Distinct().ToListAsync());   // control
    }

    [Fact]
    public async Task A_ticket_is_stamped_with_the_current_tenant_whatever_the_caller_says()
    {
        int id;
        await using (var db = env.Db("alpha", Guid.NewGuid().ToString()))
        {
            var t = new Ticket
            {
                Number = 9001, Title = "Stamp probe", Description = "Stamp probe description", ReporterName = "x", TenantId = "beta"
            };
            db.Tickets.Add(t);
            await db.SaveChangesAsync();
            id = t.Id;
        }

        Assert.Equal("alpha", (await env.TicketRowAsync(id)).TenantId);
    }

    // ================================================================ the public door
    [Fact]
    public async Task A_public_report_lands_in_the_organization_that_owns_the_link()
    {
        var alphaBefore = (await env.TicketIdsAsync("alpha")).Count;
        var betaBefore = (await env.TicketIdsAsync("beta")).Count;

        var res = await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, ValidReport("PUBLIC-ALPHA-1"));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var reference = (await res.Content.ReadFromJsonAsync<TicketReferenceDto>(TestHttp.Json))!.Reference;
        Assert.Matches(@"^TKT-\d{6}$", reference);

        Assert.Equal(alphaBefore + 1, (await env.TicketIdsAsync("alpha")).Count);
        Assert.Equal(betaBefore, (await env.TicketIdsAsync("beta")).Count);

        await using var raw = env.Raw();
        var ticket = await raw.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Title == "PUBLIC-ALPHA-1");
        Assert.Equal("alpha", ticket.TenantId);
        Assert.Equal(TicketSource.PublicLink, ticket.Source);
        Assert.Null(ticket.CreatedByUserId);            // no identity: the reporter's name is only text
        Assert.Equal(TicketPriority.High, ticket.Priority);   // "Blocking" starts at High
    }

    [Fact]
    public async Task A_tenant_id_in_the_public_body_is_ignored()
    {
        using var client = env.NewClient(handleCookies: false);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/public/report")
        {
            Content = new StringContent(
                """{"name":"Sneaky","category":"Other","impact":"Minor","title":"PUBLIC-BODY-TENANT","description":"Trying to pick another tenant.","tenantId":"beta"}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Report-Token", TestEnvironment.AlphaReportToken);

        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

        await using var raw = env.Raw();
        Assert.Equal("alpha", (await raw.Tickets.IgnoreQueryFilters().SingleAsync(t => t.Title == "PUBLIC-BODY-TENANT")).TenantId);
    }

    [Fact]
    public async Task The_public_link_reveals_nothing_but_the_organizations_name()
    {
        var res = await env.PublicAsync(HttpMethod.Get, TestEnvironment.AlphaReportToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "organization" }, doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Alpha Airlines", doc.RootElement.GetProperty("organization").GetString());
    }

    [Fact]
    public async Task Unknown_missing_and_malformed_tokens_all_get_the_same_404()
    {
        foreach (var token in new string?[] { null, "", "not-a-real-token-0123456789abcdef", new string('x', 200) })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await env.PublicAsync(HttpMethod.Get, token)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await env.PublicAsync(HttpMethod.Post, token, ValidReport())).StatusCode);
        }
    }

    [Fact]
    public async Task A_switched_off_or_suspended_organizations_link_stops_working()
    {
        await env.CreateTenantWithAdminAsync("suspendedco", "admin@suspendedco.test", "suspended-token-0123456789abcdef", active: false);

        Assert.Equal(HttpStatusCode.NotFound, (await env.PublicAsync(HttpMethod.Get, "suspended-token-0123456789abcdef")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await env.PublicAsync(HttpMethod.Post, "suspended-token-0123456789abcdef", ValidReport())).StatusCode);
    }

    [Fact]
    public async Task Replacing_the_link_kills_the_old_one_and_turning_it_off_kills_them_all()
    {
        const string oldToken = "rotation-token-0123456789abcdef0";
        await env.CreateTenantWithAdminAsync("rotateco", "admin@rotateco.test", oldToken);
        var admin = await env.SignInAsync("rotateco", "admin@rotateco.test");
        Assert.Equal(HttpStatusCode.OK, (await env.PublicAsync(HttpMethod.Get, oldToken)).StatusCode);

        var replaced = await (await admin.PostAsync("/api/settings/report-link", null)).Content.ReadFromJsonAsync<ReportLinkDto>(TestHttp.Json);
        Assert.True(replaced!.Enabled);
        Assert.NotEqual(oldToken, replaced.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await env.PublicAsync(HttpMethod.Get, oldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await env.PublicAsync(HttpMethod.Get, replaced.Token)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/settings/report-link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await env.PublicAsync(HttpMethod.Get, replaced.Token)).StatusCode);
    }

    [Fact]
    public async Task Only_administrators_control_the_public_link()
    {
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        var requester = await env.SignInAsync("alpha", "requester@alpha.test");

        foreach (var client in new[] { tech, requester })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/settings/report-link")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/settings/report-link", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync("/api/settings/report-link")).StatusCode);
        }
    }

    [Fact]
    public async Task A_filled_honeypot_is_answered_with_success_but_nothing_is_saved()
    {
        var body = new
        {
            name = "Bot", category = "Other", impact = "Minor", title = "HONEYPOT-PROBE",
            description = "Automated spam submission text.", website = "http://spam.example"
        };

        var res = await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, body);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        await using var raw = env.Raw();
        Assert.False(await raw.Tickets.IgnoreQueryFilters().AnyAsync(t => t.Title == "HONEYPOT-PROBE"));
    }

    [Fact]
    public async Task Invalid_public_reports_are_rejected()
    {
        var tooShort = new { name = "A", category = "Other", impact = "Minor", title = "x", description = "short", website = "" };

        Assert.Equal(HttpStatusCode.BadRequest, (await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, tooShort)).StatusCode);
    }

    [Fact]
    public async Task The_public_link_cannot_be_used_to_read_or_manage_tickets()
    {
        using var client = env.NewClient(handleCookies: false);
        var someId = (await env.TicketIdsAsync("alpha")).First();

        foreach (var url in new[] { "/api/tickets", $"/api/tickets/{someId}", "/api/tickets/assignees", "/api/settings/report-link" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Report-Token", TestEnvironment.AlphaReportToken);   // the token opens the report door only
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task Ticket_numbers_count_separately_in_each_organization()
    {
        var alphaBefore = await env.MaxTicketNumberAsync("alpha");
        var betaBefore = await env.MaxTicketNumberAsync("beta");

        var a1 = await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, ValidReport("NUMBERS-A1"));
        var a2 = await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, ValidReport("NUMBERS-A2"));
        var b1 = await env.PublicAsync(HttpMethod.Post, TestEnvironment.BetaReportToken, ValidReport("NUMBERS-B1"));

        Assert.Equal(alphaBefore + 1, NumberFrom((await a1.Content.ReadFromJsonAsync<TicketReferenceDto>(TestHttp.Json))!.Reference));
        Assert.Equal(alphaBefore + 2, NumberFrom((await a2.Content.ReadFromJsonAsync<TicketReferenceDto>(TestHttp.Json))!.Reference));
        Assert.Equal(betaBefore + 1, NumberFrom((await b1.Content.ReadFromJsonAsync<TicketReferenceDto>(TestHttp.Json))!.Reference));
    }

    // ================================================================ signed-in staff
    [Theory]
    [InlineData("alpha")]
    [InlineData("beta")]
    public async Task The_ticket_queue_contains_only_the_callers_organization(string tenant)
    {
        var admin = await env.SignInAsync(tenant, $"admin@{tenant}.test");

        var page = await admin.GetJsonAsync<PagedResult<TicketDto>>("/api/tickets?pageSize=100");

        Assert.Equal((await env.TicketIdsAsync(tenant)).Order(), page.Items.Select(t => t.Id).Order());
    }

    [Fact]
    public async Task Another_organizations_ticket_is_not_found_and_cannot_be_changed()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var betaTicket = (await env.TicketIdsAsync("beta")).First();
        var before = await env.TicketRowAsync(betaTicket);

        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/api/tickets/{betaTicket}")).StatusCode);

        var res = await alpha.PutJsonAsync($"/api/tickets/{betaTicket}", new
        {
            status = "Closed", priority = "Low", assignedToUserId = (Guid?)null, assetSerial = (string?)null, resolutionNotes = "hijacked"
        });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var after = await env.TicketRowAsync(betaTicket);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Priority, after.Priority);
        Assert.Equal(before.ResolutionNotes, after.ResolutionNotes);
    }

    [Fact]
    public async Task A_ticket_raised_by_staff_records_the_creator_and_ignores_a_tenant_in_the_body()
    {
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        var techId = await env.UserIdAsync("alpha", "tech@alpha.test");

        var res = await tech.PostRawJsonAsync("/api/tickets",
            """{"title":"STAFF-BODY-TENANT","description":"Trying to pick another tenant.","category":"Other","impact":"Minor","tenantId":"beta"}""");

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<TicketDto>(TestHttp.Json))!;
        var row = await env.TicketRowAsync(dto.Id);
        Assert.Equal("alpha", row.TenantId);
        Assert.Equal(techId, row.CreatedByUserId);
        Assert.Equal(TicketSource.StaffAccount, row.Source);
    }

    [Fact]
    public async Task A_requester_sees_only_their_own_tickets()
    {
        var requester = await env.SignInAsync("alpha", "requester@alpha.test");
        var requesterId = await env.UserIdAsync("alpha", "requester@alpha.test");

        var created = await requester.PostJsonAsync("/api/tickets",
            new { title = "REQUESTER-OWN-1", description = "A fault that I am reporting myself.", category = "Hardware", impact = "Minor" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var page = await requester.GetJsonAsync<PagedResult<TicketDto>>("/api/tickets?pageSize=100");
        await using var raw = env.Raw();
        var expected = await raw.Tickets.IgnoreQueryFilters().Where(t => t.CreatedByUserId == requesterId).Select(t => t.Id).ToListAsync();

        Assert.NotEmpty(page.Items);
        Assert.Equal(expected.Order(), page.Items.Select(t => t.Id).Order());

        // other people's tickets in the SAME organization are not found either
        var othersTicket = (await env.TicketIdsAsync("alpha")).Except(expected).First();
        Assert.Equal(HttpStatusCode.NotFound, (await requester.GetAsync($"/api/tickets/{othersTicket}")).StatusCode);
    }

    [Fact]
    public async Task A_requester_cannot_triage_tickets_or_open_assets_staff_or_the_audit_trail()
    {
        var requester = await env.SignInAsync("alpha", "requester@alpha.test");
        var ticketId = (await env.TicketIdsAsync("alpha")).First();

        Assert.Equal(HttpStatusCode.Forbidden, (await requester.PutJsonAsync($"/api/tickets/{ticketId}",
            new { status = "Closed", priority = "Low", resolutionNotes = "closing it" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/tickets/assignees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task An_auditor_can_read_the_queue_but_not_change_it()
    {
        var auditor = await env.SignInAsync("alpha", "audit@alpha.test");
        var ticketId = (await env.TicketIdsAsync("alpha")).First();

        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PutJsonAsync($"/api/tickets/{ticketId}",
            new { status = "Closed", priority = "Low", resolutionNotes = "no" })).StatusCode);
    }

    [Fact]
    public async Task A_ticket_cannot_be_assigned_to_someone_in_another_organization()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var ticket = await CreateStaffTicketAsync(alpha, "ASSIGN-CROSS-TENANT");
        var betaTech = await env.UserIdAsync("beta", "tech@beta.test");

        var res = await alpha.PutJsonAsync($"/api/tickets/{ticket.Id}", new
        {
            status = "InProgress", priority = "Normal", assignedToUserId = betaTech, assetSerial = (string?)null, resolutionNotes = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Null((await env.TicketRowAsync(ticket.Id)).AssignedToUserId);
    }

    [Fact]
    public async Task A_ticket_cannot_be_linked_to_an_asset_of_another_organization()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var ticket = await CreateStaffTicketAsync(alpha, "LINK-CROSS-TENANT");

        var res = await alpha.PutJsonAsync($"/api/tickets/{ticket.Id}", new
        {
            status = "InProgress", priority = "Normal", assignedToUserId = (Guid?)null, assetSerial = "BETA-1", resolutionNotes = (string?)null
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Null((await env.TicketRowAsync(ticket.Id)).AssetId);
    }

    [Fact]
    public async Task Resolving_needs_a_note_and_logs_the_work_on_the_linked_asset_exactly_once()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var ticket = await CreateStaffTicketAsync(alpha, "RESOLVE-LOGS-MAINTENANCE");
        var assetId = await env.AssetIdAsync("alpha", "ALPHA-1");

        object Update(string? notes) => new
        {
            status = "Resolved", priority = "Normal", assignedToUserId = (Guid?)null, assetSerial = "ALPHA-1", resolutionNotes = notes
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await alpha.PutJsonAsync($"/api/tickets/{ticket.Id}", Update(null))).StatusCode);

        await using var raw = env.Raw();
        var before = await raw.MaintenanceLogs.IgnoreQueryFilters().CountAsync(m => m.AssetId == assetId);

        Assert.Equal(HttpStatusCode.OK, (await alpha.PutJsonAsync($"/api/tickets/{ticket.Id}", Update("Replaced the feed roller."))).StatusCode);
        var afterFirst = await raw.MaintenanceLogs.IgnoreQueryFilters().Where(m => m.AssetId == assetId).ToListAsync();
        Assert.Equal(before + 1, afterFirst.Count);
        Assert.Contains(afterFirst, m => m.Description.Contains(ticket.Reference) && m.Description.Contains("Replaced the feed roller."));
        Assert.Equal(TicketStatus.Resolved, (await env.TicketRowAsync(ticket.Id)).Status);

        // saving again must not duplicate the maintenance entry
        Assert.Equal(HttpStatusCode.OK, (await alpha.PutJsonAsync($"/api/tickets/{ticket.Id}", Update("Replaced the feed roller (confirmed)."))).StatusCode);
        Assert.Equal(before + 1, await raw.MaintenanceLogs.IgnoreQueryFilters().CountAsync(m => m.AssetId == assetId));
    }

    [Fact]
    public async Task Ticket_changes_are_audited_under_the_organization_that_made_them()
    {
        var alpha = await env.SignInAsync("alpha", "admin@alpha.test");
        var ticket = await CreateStaffTicketAsync(alpha, "AUDITED-TICKET");

        await using var alphaDb = env.Db("alpha");
        await using var betaDb = env.Db("beta");
        var key = ticket.Id.ToString();

        Assert.Contains(await alphaDb.SecurityAuditLogs.ToListAsync(), l => l.EntityName == nameof(Ticket) && l.EntityId == key);
        Assert.DoesNotContain(await betaDb.SecurityAuditLogs.ToListAsync(), l => l.EntityName == nameof(Ticket) && l.EntityId == key);
    }

    // ---------------------------------------------------------------- helper
    private static async Task<TicketDto> CreateStaffTicketAsync(HttpClient client, string title)
    {
        var res = await client.PostJsonAsync("/api/tickets",
            new { title, description = "Created by an automated test.", category = "Hardware", impact = "Degraded" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<TicketDto>(TestHttp.Json))!;
    }
}
