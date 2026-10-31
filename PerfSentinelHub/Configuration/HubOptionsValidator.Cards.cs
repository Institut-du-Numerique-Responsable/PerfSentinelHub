using System.Text.RegularExpressions;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Configuration;

public sealed partial class HubOptionsValidator
{
    // The engine's bounds: it refuses a card.json past them, so the Hub refuses the setting first.
    private const int MaxOrganisationChars = 60;

    // The engine's equivalence_country values.
    internal static readonly string[] CardCountries = ["fr", "gb", "us"];

    private static void ValidateCards(HubOptions options, List<string> errors)
    {
        var cards = options.Cards;
        // Checked with cards off too, as Hub:MaxConcurrentPolls is: a wrong value waits for the day they turn on.
        if (cards.MaxConcurrentRenders is < 1 or > 16)
            errors.Add("Hub:Cards:MaxConcurrentRenders must be between 1 and 16.");
        if (cards.Country is { } country && !CardCountries.Contains(country, StringComparer.Ordinal))
            errors.Add("Hub:Cards:Country must be fr, gb or us.");
        if (cards.Organisation is { } organisation &&
            (organisation.Length is 0 or > MaxOrganisationChars || organisation.Any(LabelWrite.Hidden)))
            errors.Add("Hub:Cards:Organisation must be 1 to 60 characters with no control or hidden character.");
        if (cards.Domain is not { } domain)
            return;
        if (!cards.Enabled)
            errors.Add("Hub:Cards:Domain needs Hub:Cards:Organisation.");
        else if (!IsCardDomain(domain))
            errors.Add("Hub:Cards:Domain must be a lower-case DNS name such as example.com, without scheme, "
                       + "port or trailing dot.");
    }

    // An all-digit last label would let an IPv4 address pass for a name.
    private static bool IsCardDomain(string domain)
    {
        return CardDomainPattern().IsMatch(domain) &&
               !domain[(domain.LastIndexOf('.') + 1)..].All(char.IsAsciiDigit);
    }

    // At most 253 characters, two labels or more, each 1 to 63 of [a-z0-9-] with no '-' at either end.
    [GeneratedRegex(@"^(?=.{1,253}\z)[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\z")]
    private static partial Regex CardDomainPattern();
}
