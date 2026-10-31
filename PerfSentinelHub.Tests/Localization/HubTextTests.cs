using System.Text.RegularExpressions;
using PerfSentinelHub.Localization;

namespace PerfSentinelHub.Tests.Localization;

public sealed partial class HubTextTests
{
    [Fact]
    public void Every_key_exists_in_both_languages_with_the_same_placeholders()
    {
        Assert.Equal(HubText.English.Keys.Order(StringComparer.Ordinal), HubText.French.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, english) in HubText.English)
            Assert.True(Placeholders(english) == Placeholders(HubText.French[key]), "placeholders differ in " + key);
    }

    [Fact]
    public void A_value_fills_its_placeholder_and_an_unknown_key_throws()
    {
        Assert.Equal("Scope: prod", HubText.Get(HubLang.En, "recap.scope", ("parts", "prod")));
        Assert.Equal("Périmètre : prod", HubText.Get(HubLang.Fr, "recap.scope", ("parts", "prod")));
        Assert.Throws<KeyNotFoundException>(() => HubText.Get(HubLang.En, "no.such.key"));
    }

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("en", "en")]
    [InlineData("de", "en")]
    [InlineData(null, "en")]
    public void A_language_normalizes_to_one_the_hub_writes(string? value, string expected)
    {
        Assert.Equal(expected, HubLang.Normalize(value));
    }

    [Fact]
    public void French_numbers_group_as_the_launcher_writes_them()
    {
        Assert.Equal("1 234,5", 1234.5.ToString("#,0.###", HubLang.Culture(HubLang.Fr)));
        Assert.Equal("1,234.5", 1234.5.ToString("#,0.###", HubLang.Culture(HubLang.En)));
        Assert.Equal("one", HubLang.Plural(HubLang.Fr, 0));
        Assert.Equal("other", HubLang.Plural(HubLang.En, 0));
    }

    private static string Placeholders(string text)
    {
        return string.Join(',', Placeholder().Matches(text).Select(match => match.Value).Distinct().Order(StringComparer.Ordinal));
    }

    [GeneratedRegex(@"\{\w+\}")]
    private static partial Regex Placeholder();
}
