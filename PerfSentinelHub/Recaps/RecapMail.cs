using System.Net.Mail;
using System.Text;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     The message for one subscriber. It attaches the management report and nothing else: the
///     engineering view travels as a link to the Hub, so it stays behind its sign-in.
/// </summary>
public static class RecapMail
{
    public static MailMessage Build(
        RecapReportSummary report, RecapSnapshot tech, string to, MailAddress from, Uri? publicUrl)
    {
        var period = RecapText.Period(
            new RecapPeriodInfo(report.PeriodKind, report.PeriodFrom, report.PeriodTo, false, [], [], null, null));
        var message = new MailMessage(new MailAddress(from.Address, "Perf Sentinel Hub"), new MailAddress(to))
        {
            Subject = $"Perf Sentinel recap: {report.PeriodKind} {period}{(report.Complete ? "" : " (incomplete)")}",
            SubjectEncoding = Encoding.UTF8,
            Body = string.Join('\n', BodyLines(report, period, publicUrl)),
            BodyEncoding = Encoding.UTF8
        };
        if (report.Views.Contains(RecapViewNames.Pilotage))
            message.Attachments.Add(new Attachment(
                new MemoryStream(RecapReportRenderer.Render(RecapViews.Frozen(tech))),
                $"perf-sentinel-recap-{report.PeriodFrom}-pilotage.html", "text/html; charset=utf-8"));
        return message;
    }

    private static IEnumerable<string> BodyLines(RecapReportSummary report, string period, Uri? publicUrl)
    {
        yield return $"Recap \"{report.Subscription}\" for {period}.";
        if (!report.Complete)
            yield return
                "Some days of this period are incomplete, so the comparison with the previous period is not exact.";
        if (report.Views.Contains(RecapViewNames.Pilotage))
            yield return "The management view is attached. Open it in a browser; it needs no network.";
        if (report.Views.Contains(RecapViewNames.Tech))
            yield return RecapWebhook.LinkTo(report, RecapViewNames.Tech, publicUrl) is { } link
                ? $"Engineering view, after signing in to the Hub: {link}"
                : "No link to the engineering view: the Hub has no public address set (Hub:Recaps:PublicUrl).";
        yield return "Figures are frozen at generation. CO2 is an order-of-magnitude estimate.";
    }
}
