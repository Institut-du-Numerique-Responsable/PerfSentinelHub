using System.Text.RegularExpressions;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Configuration;

public sealed partial class HubOptionsValidator
{
    // The engine's bounds: it refuses a card.json past them, so the Hub refuses the setting first.
    private const int MaxOrganisationChars = 60;

    private static void ValidateCards(HubOptions options, List<string> errors)
    {
        var cards = options.Cards;
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
