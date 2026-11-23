using System.Globalization;
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
        var period = lang == HubLang.Fr && report.PeriodKind == "month"
            ? RecapText.FrenchMonth(report.PeriodFrom)
            : RecapText.Period(
                new RecapPeriodInfo(report.PeriodKind, report.PeriodFrom, report.PeriodTo, false, [], [], null, null),
                lang);
        var incomplete = report.Complete ? "" : HubText.Get(lang, "mail.subject.incomplete");
        var message = new MailMessage(new MailAddress(from.Address, "Perf Sentinel Hub"), new MailAddress(to))
        {
            Subject = HubText.Get(lang, "mail.subject." + report.PeriodKind, ("period", period),
                ("incomplete", incomplete)),
            SubjectEncoding = Encoding.UTF8,
            Body = string.Join('\n', BodyLines(report, tech, period, publicUrl, lang)),
            BodyEncoding = Encoding.UTF8
        };
        if (report.Views.Contains(RecapViewNames.Pilotage))
            message.Attachments.Add(new Attachment(
                new MemoryStream(RecapReportRenderer.Render(RecapViews.Frozen(tech), lang)),
                $"perf-sentinel-recap-{report.PeriodFrom}-pilotage.html", "text/html; charset=utf-8"));
        return message;
    }

    private static IEnumerable<string> BodyLines(
        RecapReportSummary report, RecapSnapshot tech, string period, Uri? publicUrl, string lang)
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
        foreach (var line in IncidentLines(report, tech, publicUrl, lang))
            yield return line;
        yield return HubText.Get(lang, "mail.footer");
    }

    // The management cut, whatever the views: a mail travels, so it names no endpoint or signature.
    private static IEnumerable<string> IncidentLines(
        RecapReportSummary report, RecapSnapshot tech, Uri? publicUrl, string lang)
    {
        if (RecapViews.Frozen(tech).Incidents is not { } incidents ||
            (incidents.Count == 0 && incidents.Suspects.Count == 0))
            yield break;
        var kinds = RecapText.Kinds(incidents.ByKind, lang);
        yield return HubText.Get(lang, "mail.incidents",
            ("count", RecapText.Number(incidents.Count, lang)),
            ("previous", RecapText.Previous(tech.Previous, RecapText.Number(incidents.PreviousCount, lang), lang)),
            ("kinds", kinds.Length > 0 ? ", " + kinds : ""));
        if (incidents.Suspects.Count > 0)
            yield return HubText.Get(lang, "mail.suspects");
        foreach (var suspect in incidents.Suspects)
            yield return HubText.Get(lang, "mail.suspect",
                ("rank", suspect.Rank.ToString(CultureInfo.InvariantCulture)),
                ("type", RecapText.Type(suspect.FindingType, lang)), ("service", suspect.Service),
                ("why", RecapText.SuspectWhy(suspect, lang)));
        if (incidents.MoreSuspects > 0)
            yield return HubText.Get(lang, "recap.more.report." + HubLang.Plural(lang, incidents.MoreSuspects),
                ("count", incidents.MoreSuspects.ToString(CultureInfo.InvariantCulture)));
        // The Incidents screen sits behind the sign-in, like the engineering view.
        if (report.Views.Contains(RecapViewNames.Tech) && publicUrl is not null)
            yield return HubText.Get(lang, "mail.incidents_link", ("link", new Uri(publicUrl, "/#/incidents").ToString()));
    }
}
