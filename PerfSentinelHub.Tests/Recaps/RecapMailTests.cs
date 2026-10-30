using System.Net.Mail;
using System.Text;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

/// <summary>
///     The message a subscriber receives: the management report as the one attachment, the engineering
///     view only as a link to the Hub, and nothing from the engineering snapshot in the text.
/// </summary>
public sealed class RecapMailTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";
    private static readonly MailAddress Relay = new("hub@example.test");

    [Fact]
    public void The_attachment_is_the_management_report_only()
    {
        using var mail = Build(new Uri("https://hub.example/"));

        var attachment = Assert.Single(mail.Attachments);
        Assert.Equal("perf-sentinel-recap-2026-10-19-pilotage.html", attachment.Name);
        Assert.Equal("text/html", attachment.ContentType.MediaType);
        Assert.Equal("utf-8", attachment.ContentType.CharSet);
        var html = new StreamReader(attachment.ContentStream, Encoding.UTF8).ReadToEnd();
        Assert.Contains("Label OK", html, StringComparison.Ordinal);
        var everything = mail.Subject + mail.Body + html;
        foreach (var value in RecapSnapshots.Values)
            Assert.DoesNotContain(value, everything, StringComparison.Ordinal);
        foreach (var key in new[]
                 {
                     "signature", "endpoint", "template", "suggestion", "suggested_fix", "code_location",
                     "confidence", "events"
                 })
            Assert.DoesNotContain($"\"{key}\"", everything, StringComparison.Ordinal);
        Assert.Equal("Perf Sentinel Hub", mail.From!.DisplayName);
        Assert.Equal("hub@example.test", mail.From.Address);
        Assert.Equal("pm@example.test", Assert.Single(mail.To).Address);
    }

    [Fact]
    public void The_attachment_names_no_endpoint_of_an_unlabelled_row()
    {
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Priorities = [RecapSnapshots.Priority(1, null)],
            Movements = new RecapMovements([RecapSnapshots.Move(null)], [], 0, [], [], [], [])
        };
        using var mail = Build(null, snapshot: snapshot);

        var html = new StreamReader(Assert.Single(mail.Attachments).ContentStream, Encoding.UTF8).ReadToEnd();

        Assert.Contains("An unnamed step in checkout-api", html, StringComparison.Ordinal);
        Assert.DoesNotContain(RecapSnapshots.Endpoint, html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_view_is_a_link_when_the_hub_has_a_public_url()
    {
        using var mail = Build(new Uri("https://hub.example/"));

        Assert.Equal("Perf Sentinel recap: week 2026-10-19 to 2026-10-25", mail.Subject);
        Assert.Equal(
            [
                "Recap \"weekly-shop\" for 2026-10-19 to 2026-10-25.",
                "The management view is attached. Open it in a browser; it needs no network.",
                $"Engineering view, after signing in to the Hub: https://hub.example/recaps/{Id}/tech.html",
                "Figures are frozen at generation. CO2 is an order-of-magnitude estimate."
            ],
            Lines(mail));
    }

    [Fact]
    public void Without_a_public_url_the_mail_says_why_there_is_no_link()
    {
        using var mail = Build(null);

        Assert.Contains(
            "No link to the engineering view: the Hub has no public address set (Hub:Recaps:PublicUrl).",
            Lines(mail));
        Assert.DoesNotContain("tech.html", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tech_only_subscription_attaches_nothing()
    {
        using var mail = Build(new Uri("https://hub.example/"), [RecapViewNames.Tech]);

        Assert.Empty(mail.Attachments);
        Assert.DoesNotContain("attached", mail.Body, StringComparison.Ordinal);
        Assert.Contains($"https://hub.example/recaps/{Id}/tech.html", mail.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_management_only_subscription_has_no_engineering_line()
    {
        using var mail = Build(null, [RecapViewNames.Pilotage]);

        Assert.Single(mail.Attachments);
        Assert.DoesNotContain("engineering", mail.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_incomplete_period_says_so_in_the_subject_and_the_body()
    {
        using var mail = Build(null, complete: false);

        Assert.Equal("Perf Sentinel recap: week 2026-10-19 to 2026-10-25 (incomplete)", mail.Subject);
        Assert.Equal(
            "Some days of this period are incomplete, so the comparison with the previous period is not exact.",
            Lines(mail)[1]);
    }

    private static string[] Lines(MailMessage mail)
    {
        return mail.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static MailMessage Build(
        Uri? publicUrl, string[]? views = null, bool complete = true, RecapSnapshot? snapshot = null)
    {
        var summary = new RecapReportSummary(
            Id, "weekly-shop", "week", "2026-10-19", "2026-10-25",
            views ?? [RecapViewNames.Pilotage, RecapViewNames.Tech], complete, false, null, 10_000);
        return RecapMail.Build(summary, snapshot ?? RecapSnapshots.Distinctive(), "pm@example.test", Relay, publicUrl);
    }
}
