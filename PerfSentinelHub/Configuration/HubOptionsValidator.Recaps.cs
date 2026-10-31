using System.Net;
using System.Net.Mail;
using PerfSentinelHub.Localization;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Configuration;

public sealed partial class HubOptionsValidator
{
    private const int MaxSubscriptions = 100;
    private const int MaxEmails = 50;

    // MovementThreshold is checked with the other numeric settings, in ValidateHubSettings.
    private static void ValidateRecaps(HubOptions options, List<string> errors)
    {
        var recaps = options.Recaps;
        if (recaps.Retention < TimeSpan.FromDays(32))
            errors.Add("Hub:Recaps:Retention must be at least 32 days: a shorter one purges a month's report "
                       + "while it is still the last closed month, and the next pass would generate and send it again.");
        if (recaps.MaxDelay <= TimeSpan.Zero || recaps.MaxDelay > TimeSpan.FromDays(7))
            errors.Add("Hub:Recaps:MaxDelay must be above zero and at most 7 days.");
        ValidatePublicUrl("Hub:Recaps:PublicUrl", recaps.PublicUrl, errors);
        var names = ValidateRecapWebhooks(recaps.Webhooks, errors);
        ValidateRecapSmtp(recaps.Smtp, errors);

        if (recaps.Subscriptions.Count > MaxSubscriptions)
            errors.Add($"Hub:Recaps:Subscriptions holds {MaxSubscriptions} subscriptions at most.");
        var subscriptionNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < recaps.Subscriptions.Count; i++)
            ValidateSubscription(recaps.Subscriptions[i], $"Hub:Recaps:Subscriptions:{i}", subscriptionNames, names,
                options, errors);
    }

    private static HashSet<string> ValidateRecapWebhooks(IReadOnlyList<WebhookOptions> webhooks, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < webhooks.Count; i++)
        {
            var path = $"Hub:Recaps:Webhooks:{i}";
            ValidateWebhook(webhooks[i], path, names, errors);
            if (webhooks[i].IncludeShareLink)
                errors.Add($"{path}:IncludeShareLink applies to disclosures only.");
        }

        return names;
    }

    private static void ValidateRecapSmtp(RecapSmtpOptions smtp, List<string> errors)
    {
        if (smtp.Host is not { } host)
            return;
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            errors.Add("Hub:Recaps:Smtp:Host must be a host name or an IP address.");
        if (smtp.Port is < 1 or > 65535 or 465)
            errors.Add("Hub:Recaps:Smtp:Port must be between 1 and 65535, and not 465: the Hub speaks STARTTLS, "
                       + "not implicit TLS, so use 587.");
        if (smtp.From is null)
            errors.Add("Hub:Recaps:Smtp:From is required with Hub:Recaps:Smtp:Host.");
        else if (!IsPlainAddress(smtp.From))
            errors.Add("Hub:Recaps:Smtp:From must be one e-mail address, without a display name.");
        if (smtp.UserName is null != smtp.Password is null)
            errors.Add("Hub:Recaps:Smtp:UserName and Password go together or not at all.");
        else if (smtp.UserName is not null && !smtp.StartTls && !IsLoopbackHost(host))
            errors.Add("Hub:Recaps:Smtp:UserName needs StartTls unless the relay is on loopback: "
                       + "a password never travels in clear.");
    }

    private static bool IsLoopbackHost(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }

    // Also refuses header injection: a CR or LF is a control character.
    private static bool IsPlainAddress(string value)
    {
        return !value.Any(char.IsControl) &&
               MailAddress.TryCreate(value, out var parsed) &&
               parsed.DisplayName.Length == 0 &&
               parsed.Address == value;
    }

    private static void ValidateSubscription(RecapSubscriptionOptions sub, string path, HashSet<string> seen,
        HashSet<string> webhookNames, HubOptions options, List<string> errors)
    {
        if (!IsValidName(sub.Name) || !seen.Add(sub.Name))
            errors.Add($"{path}:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', and unique.");
        if (!IsDistinctNonEmpty(sub.Cadences, cadence => RecapPeriod.TryParseKind(cadence, out _)))
            errors.Add($"{path}:Cadences must list day, week or month, each once.");
        if (!IsDistinctNonEmpty(sub.Views, RecapViewNames.All.Contains))
            errors.Add($"{path}:Views must list pilotage, tech or both, each once.");
        ValidateSubscriptionScope(sub, path, options.Sources, errors);
        if (new[] { sub.Service, sub.Grouping }.Any(IsInvalidFilter))
            errors.Add($"{path}:Service and Grouping are 1 to 256 characters with no control character.");
        foreach (var name in sub.Webhooks.Where(name => !webhookNames.Contains(name)))
            errors.Add($"{path}:Webhooks names a webhook Hub:Recaps:Webhooks does not declare: '{name}'.");
        ValidateSubscriptionEmails(sub, path, options.Recaps.Smtp, errors);
    }

    private static bool IsInvalidFilter(string? value)
    {
        return value is not null && (value.Length is 0 or > 256 || value.Any(char.IsControl));
    }

    private static bool IsDistinctNonEmpty(IReadOnlyList<string> values, Func<string, bool> isKnown)
    {
        return values.Count > 0 && values.All(isKnown) &&
               values.Distinct(StringComparer.Ordinal).Count() == values.Count;
    }

    private static void ValidateSubscriptionScope(RecapSubscriptionOptions sub, string path,
        IReadOnlyList<SourceOptions> sources, List<string> errors)
    {
        if (sub.Environment is not null && sources.All(source => source.Environment != sub.Environment))
            errors.Add($"{path}:Environment matches no source.");
        if (sub.SourceId is null)
            return;
        var source = sources.FirstOrDefault(candidate => candidate.Id == sub.SourceId);
        if (source is null)
            errors.Add($"{path}:SourceId matches no source.");
        else if (sub.Environment is not null && source.Environment != sub.Environment)
            errors.Add($"{path}:SourceId is not in {path}:Environment.");
    }

    private static void ValidateSubscriptionEmails(RecapSubscriptionOptions sub, string path, RecapSmtpOptions smtp,
        List<string> errors)
    {
        if (!HubLang.IsKnown(sub.Language))
            errors.Add($"{path}:Language must be en or fr.");
        if (sub.Emails.Count == 0 && sub.Recipients.Count == 0)
            return;
        if (sub.Emails.Count > 0 && (sub.Emails.Count > MaxEmails || !sub.Emails.All(IsPlainAddress) ||
                                     sub.Emails.Distinct(StringComparer.Ordinal).Count() != sub.Emails.Count))
            errors.Add($"{path}:Emails holds {MaxEmails} addresses at most, each one e-mail address without a "
                       + "display name, each once.");
        ValidateRecipients(sub, path, errors);
        if (!smtp.Enabled)
            errors.Add($"{path}:{(sub.Emails.Count > 0 ? "Emails" : "Recipients")} needs Hub:Recaps:Smtp:Host.");
    }

    private static void ValidateRecipients(RecapSubscriptionOptions sub, string path, List<string> errors)
    {
        if (sub.Recipients.Count == 0)
            return;
        for (var i = 0; i < sub.Recipients.Count; i++)
        {
            var recipient = sub.Recipients[i];
            if (recipient.Address is null || !IsPlainAddress(recipient.Address))
                errors.Add($"{path}:Recipients:{i}:Address must be one e-mail address, without a display name.");
            if (recipient.Language is not null && !HubLang.IsKnown(recipient.Language))
                errors.Add($"{path}:Recipients:{i}:Language must be en or fr.");
        }

        // Ignoring case, as the deliveries do: one address, one language.
        string[] all = [.. sub.Emails, .. sub.Recipients.Select(recipient => recipient.Address ?? "")];
        if (all.Length > MaxEmails || all.Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length)
            errors.Add($"{path}:Emails and Recipients hold {MaxEmails} addresses at most together, each address "
                       + "in one list, once.");
    }
}
