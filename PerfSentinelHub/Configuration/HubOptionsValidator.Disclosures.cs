namespace PerfSentinelHub.Configuration;

public sealed partial class HubOptionsValidator
{
    private static void ValidateDisclosures(HubOptions options, List<string> errors)
    {
        var disclosures = options.Disclosures;
        if (disclosures.ImportKey is { } key)
        {
            if (key.Length < 32 || key.Any(char.IsControl))
                errors.Add("Hub:Disclosures:ImportKey must be at least 32 characters with no control character.");
            // One key per door: a key that opens two of them is leaked by either.
            else if (options.Sources.Any(source => source.ImportApiKey == key) ||
                     options.Access.MachineTokens.Any(token => token.Token == key))
                errors.Add("Hub:Disclosures:ImportKey must differ from every source import API key and machine token.");
        }

        if (disclosures.AllowInternal && !options.Access.Enabled)
            errors.Add("Hub:Disclosures:AllowInternal requires Hub:Access:Enabled: an internal report would be "
                       + "open to anyone who reaches the Hub.");
        if ((disclosures.ExpectedIdentity is null) != (disclosures.ExpectedIssuer is null))
            errors.Add("Hub:Disclosures:ExpectedIdentity and ExpectedIssuer go together or not at all.");
        if (disclosures.ShareLinkDays is < 1 or > 30)
            errors.Add("Hub:Disclosures:ShareLinkDays must be between 1 and 30.");

        if (disclosures.PublicUrl is { } publicUrl && IsInvalidSourceUrl(publicUrl))
            errors.Add("Hub:Disclosures:PublicUrl must be an absolute HTTP(S) URL " + UrlShape);
        if (disclosures.PublicUrl is null && disclosures.Webhooks.Any(webhook => webhook.IncludeShareLink))
            errors.Add("Hub:Disclosures:PublicUrl is required by a webhook with IncludeShareLink: "
                       + "the link it sends must name the Hub.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < disclosures.Webhooks.Count; i++)
            ValidateWebhook(disclosures.Webhooks[i], $"Hub:Disclosures:Webhooks:{i}", names, errors);
    }

    private static void ValidateWebhook(WebhookOptions webhook, string path, HashSet<string> names, List<string> errors)
    {
        if (!IsValidName(webhook.Name) || !names.Add(webhook.Name))
            errors.Add($"{path}:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', and unique.");
        if (IsInvalidAuthEndpoint(webhook.Url))
            errors.Add($"{path}:Url must be an absolute https URL, or http to a loopback address, "
                       + "without credentials or fragment.");
        if (webhook.Secret is not { Length: >= 32 } secret || secret.Any(char.IsControl))
            errors.Add($"{path}:Secret must be at least 32 characters with no control character.");
        if (webhook.Confidentiality != "public")
            errors.Add($"{path}:Confidentiality must be public: an internal report never leaves the Hub.");
        if (webhook.ShareLinkDays is < 1 or > 30)
            errors.Add($"{path}:ShareLinkDays must be between 1 and 30.");
    }
}
