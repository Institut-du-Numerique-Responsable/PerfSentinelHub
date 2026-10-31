using System.Net.Mail;
using System.Text;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Localization;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     The message for one subscriber. It attaches the management report and nothing else: the
///     engineering view travels as a link to the Hub, so it stays behind its sign-in.
/// </summary>
public static class RecapMail
{
    /// <summary>The message in <paramref name="lang" />, the language of the address it goes to.</summary>
    public static MailMessage Build(
        RecapReportSummary report, RecapSnapshot tech, string to, MailAddress from, Uri? publicUrl,
        string lang = HubLang.En)
    {
        var period = RecapText.Period(
            new RecapPeriodInfo(report.PeriodKind, report.PeriodFrom, report.PeriodTo, false, [], [], null, null),
            lang);
        var incomplete = report.Complete ? "" : HubText.Get(lang, "mail.subject.incomplete");
        var message = new MailMessage(new MailAddress(from.Address, "Perf Sentinel Hub"), new MailAddress(to))
        {
            Subject = HubText.Get(lang, "mail.subject." + report.PeriodKind, ("period", period),
                ("incomplete", incomplete)),
            SubjectEncoding = Encoding.UTF8,
            Body = string.Join('\n', BodyLines(report, period, publicUrl, lang)),
            BodyEncoding = Encoding.UTF8
        };
        if (report.Views.Contains(RecapViewNames.Pilotage))
            message.Attachments.Add(new Attachment(
                new MemoryStream(RecapReportRenderer.Render(RecapViews.Frozen(tech), lang)),
                $"perf-sentinel-recap-{report.PeriodFrom}-pilotage.html", "text/html; charset=utf-8"));
        return message;
    }

    private static IEnumerable<string> BodyLines(RecapReportSummary report, string period, Uri? publicUrl, string lang)
    {
        yield return HubText.Get(lang, "mail.intro." + report.PeriodKind, ("subscription", report.Subscription ?? ""),
            ("period", period));
        if (!report.Complete)
            yield return HubText.Get(lang, "mail.incomplete");
        if (report.Views.Contains(RecapViewNames.Pilotage))
            yield return HubText.Get(lang, "mail.attached");
        if (report.Views.Contains(RecapViewNames.Tech))
            yield return RecapWebhook.LinkTo(report, RecapViewNames.Tech, publicUrl, lang) is { } link
                ? HubText.Get(lang, "mail.tech_link", ("link", link))
                : HubText.Get(lang, "mail.no_link");
        yield return HubText.Get(lang, "mail.footer");
    }
}
