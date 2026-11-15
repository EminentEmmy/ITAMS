using System.Net;
using System.Net.Http.Json;
using ITAMS.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ITAMS.Tests;

/// <summary>The bell must only ever tell a person about their own organization, and only what their role should see.</summary>
[Collection("Isolation")]
public class NotificationTests(TestEnvironment env)
{
    private static object Report(string title) => new
    {
        name = "Bell Tester", category = "Hardware", impact = "Degraded", title,
        description = "A fault reported to exercise the notification bell.", website = ""
    };

    [Fact]
    public async Task The_bell_requires_signing_in()
    {
        using var anonymous = env.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/notifications/read", null)).StatusCode);
    }

    [Fact]
    public async Task A_handler_hears_about_new_tickets_of_their_own_organization_only()
    {
        await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, Report("BELL-ALPHA-ONLY"));
        await env.PublicAsync(HttpMethod.Post, TestEnvironment.BetaReportToken, Report("BELL-BETA-ONLY"));

        var alphaTech = await env.SignInAsync("alpha", "tech@alpha.test");
        var bell = await alphaTech.GetJsonAsync<NotificationsDto>("/api/notifications");

        Assert.Contains(bell.Items, i => i.Text.Contains("BELL-ALPHA-ONLY") && i.Unread);
        Assert.DoesNotContain(bell.Items, i => i.Text.Contains("BELL-BETA-ONLY"));

        // every ticket the bell mentions really belongs to alpha
        var betaIds = await env.TicketIdsAsync("beta");
        Assert.DoesNotContain(bell.Items, i => betaIds.Contains(i.TicketId));
    }

    [Fact]
    public async Task Marking_all_read_clears_the_count_and_a_later_ticket_makes_it_unread_again()
    {
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, Report("BELL-BEFORE-READ"));
        Assert.True((await tech.GetJsonAsync<NotificationsDto>("/api/notifications")).Unread >= 1);

        Assert.Equal(HttpStatusCode.NoContent, (await tech.PostAsync("/api/notifications/read", null)).StatusCode);

        var after = await tech.GetJsonAsync<NotificationsDto>("/api/notifications");
        Assert.Equal(0, after.Unread);
        Assert.Contains(after.Items, i => i.Text.Contains("BELL-BEFORE-READ") && !i.Unread);   // still listed, now read

        await Task.Delay(50);   // make sure the next ticket is clearly newer than the read time
        await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, Report("BELL-AFTER-READ"));

        var again = await tech.GetJsonAsync<NotificationsDto>("/api/notifications");
        Assert.Equal(1, again.Unread);
        Assert.Contains(again.Items, i => i.Text.Contains("BELL-AFTER-READ") && i.Unread);
    }

    [Fact]
    public async Task Reading_is_per_person_not_per_organization()
    {
        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, Report("BELL-PER-PERSON"));

        await admin.PostAsync("/api/notifications/read", null);

        Assert.Equal(0, (await admin.GetJsonAsync<NotificationsDto>("/api/notifications")).Unread);
        Assert.Contains((await tech.GetJsonAsync<NotificationsDto>("/api/notifications")).Items,
            i => i.Text.Contains("BELL-PER-PERSON") && i.Unread);   // the technician has not read it
    }

    [Fact]
    public async Task A_technician_is_told_when_a_ticket_is_assigned_to_them()
    {
        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        var tech = await env.SignInAsync("alpha", "tech@alpha.test");
        var techId = await env.UserIdAsync("alpha", "tech@alpha.test");
        await tech.PostAsync("/api/notifications/read", null);
        await Task.Delay(50);

        var ticket = (await (await admin.PostJsonAsync("/api/tickets",
            new { title = "BELL-ASSIGNED", description = "Assigned to the technician by the admin.", category = "Hardware", impact = "Minor" }))
            .Content.ReadFromJsonAsync<TicketDto>(TestHttp.Json))!;
        var res = await admin.PutJsonAsync($"/api/tickets/{ticket.Id}", new
        {
            status = "InProgress", priority = "Normal", assignedToUserId = techId, assetSerial = (string?)null, resolutionNotes = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var bell = await tech.GetJsonAsync<NotificationsDto>("/api/notifications");
        Assert.Contains(bell.Items, i => i.TicketId == ticket.Id && i.Kind == "Assigned" && i.Unread);
    }

    [Fact]
    public async Task A_requester_is_told_about_their_own_tickets_and_nobody_elses()
    {
        var requester = await env.SignInAsync("alpha", "requester@alpha.test");
        var admin = await env.SignInAsync("alpha", "admin@alpha.test");
        var requesterId = await env.UserIdAsync("alpha", "requester@alpha.test");

        var mine = (await (await requester.PostJsonAsync("/api/tickets",
            new { title = "BELL-REQUESTER-OWN", description = "A fault that the requester reported.", category = "Hardware", impact = "Minor" }))
            .Content.ReadFromJsonAsync<TicketDto>(TestHttp.Json))!;
        await admin.PutJsonAsync($"/api/tickets/{mine.Id}", new
        {
            status = "Resolved", priority = "Normal", assignedToUserId = (Guid?)null, assetSerial = (string?)null, resolutionNotes = "Fixed it."
        });

        var bell = await requester.GetJsonAsync<NotificationsDto>("/api/notifications");

        Assert.Contains(bell.Items, i => i.TicketId == mine.Id && i.Title.Contains("resolved"));

        await using var raw = env.Raw();
        var ownIds = await raw.Tickets.IgnoreQueryFilters().Where(t => t.CreatedByUserId == requesterId).Select(t => t.Id).ToListAsync();
        Assert.All(bell.Items, i => Assert.Contains(i.TicketId, ownIds));   // never anyone else's ticket
    }

    [Fact]
    public async Task An_auditor_has_a_bell_but_no_ticket_notifications()
    {
        await env.PublicAsync(HttpMethod.Post, TestEnvironment.AlphaReportToken, Report("BELL-AUDITOR-QUIET"));
        var auditor = await env.SignInAsync("alpha", "audit@alpha.test");

        var bell = await auditor.GetJsonAsync<NotificationsDto>("/api/notifications");

        Assert.Empty(bell.Items);
        Assert.Equal(0, bell.Unread);
    }
}
