using PerfSentinelHub.Cards;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Cards;

/// <summary>The C3 lines an administrator publishes: one DNS TXT record per key and the .well-known file.</summary>
public sealed class CardKeyLinesTests
{
    // 2026-10-30 and 2026-11-02 at noon UTC.
    private const long Oct30 = 1_793_361_600_000;
    private const long Nov2 = 1_793_620_800_000;

    private static readonly string Fingerprint = "ps1:" + new string('A', 22);
    private static readonly string Other = "ps1:" + new string('B', 22);
    private static readonly string PublicKey = new('A', 43);

    [Fact]
    public void A_current_key_line_has_no_end()
    {
        Assert.Equal($"v=ps1; k={Fingerprint}; from=2026-10-30",
            CardKeyLines.Dns(new StoredCardKey(Fingerprint, PublicKey, Oct30, null, null)));
    }

    [Fact]
    public void A_rotated_key_line_has_its_end()
    {
        Assert.Equal($"v=ps1; k={Fingerprint}; from=2026-10-30; to=2026-11-02",
            CardKeyLines.Dns(new StoredCardKey(Fingerprint, PublicKey, Oct30, Nov2, null)));
    }

    [Fact]
    public void A_revoked_key_line_names_the_date()
    {
        Assert.Equal($"v=ps1; k={Fingerprint}; from=2026-10-30; to=2026-11-02; revoked=2026-11-01",
            CardKeyLines.Dns(new StoredCardKey(Fingerprint, PublicKey, Oct30, Nov2, "2026-11-01")));
    }

    [Fact]
    public void The_well_known_file_lists_every_key_oldest_first()
    {
        var wellKnown = CardKeyLines.WellKnown(
        [
            new StoredCardKey(Fingerprint, PublicKey, Oct30, Nov2, "2026-11-01"),
            new StoredCardKey(Other, new string('B', 43), Nov2, null, null)
        ]);

        Assert.Equal(
            $$"""
              {
                "keys": [
                  {
                    "fingerprint": "{{Fingerprint}}",
                    "public_key": "{{PublicKey}}",
                    "from": "2026-10-30",
                    "to": "2026-11-02",
                    "revoked": "2026-11-01"
                  },
                  {
                    "fingerprint": "{{Other}}",
                    "public_key": "{{new string('B', 43)}}",
                    "from": "2026-11-02"
                  }
                ]
              }
              """.ReplaceLineEndings("\n"),
            wellKnown);
    }

    [Theory]
    [InlineData("example.com", "_perf-sentinel.example.com")]
    [InlineData(null, null)]
    public void Record_name_needs_a_domain(string? domain, string? expected)
    {
        Assert.Equal(expected, CardKeyLines.RecordName(domain));
    }
}
