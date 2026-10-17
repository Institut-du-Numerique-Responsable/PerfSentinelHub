using System.Buffers.Text;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     The pending device codes, held in memory: their shape, the caps, expiry at read,
///     slow_down, single use, denial and the failed-entry window.
/// </summary>
public sealed class DeviceAuthorizationsTests
{
    private const string Here = "203.0.113.7";

    // One instance: the record compares its group list by reference.
    private static readonly DeviceApproval Approval = new(
        "u-member", "member@example.internal", ["/teams/shop"], null,
        DateTimeOffset.FromUnixTimeMilliseconds(30_000_000), false);

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly DeviceAuthorizations _devices;

    public DeviceAuthorizationsTests()
    {
        _devices = new DeviceAuthorizations(_clock);
    }

    [Fact]
    public void A_device_code_is_256_bits_and_a_user_code_eight_letters_of_the_alphabet()
    {
        var codes = Enumerable.Range(0, 50).Select(index => _devices.Start("198.51.100." + index % 20)!).ToList();

        foreach (var code in codes)
        {
            Assert.Equal(32, Base64Url.DecodeFromChars(code.DeviceCode).Length);
            Assert.Equal(8, code.UserCode.Length);
            Assert.All(code.UserCode, letter => Assert.Contains(letter, TerminalAccess.UserCodeAlphabet));
        }

        Assert.Equal(50, codes.Select(code => code.DeviceCode).Distinct().Count());
        Assert.Equal(50, codes.Select(code => code.UserCode).Distinct().Count());
        Assert.Equal("BCDF-GHJK", DeviceAuthorizations.Display("BCDFGHJK"));
        Assert.Equal("BCDFGHJK", DeviceAuthorizations.NormalizeUserCode(" bcdf-ghjk "));
        Assert.Null(DeviceAuthorizations.NormalizeUserCode("BCDF-GHJA"));
        Assert.Null(DeviceAuthorizations.NormalizeUserCode("BCDF-GHJ"));
    }

    [Fact]
    public void Ten_codes_per_address_and_two_hundred_in_all()
    {
        for (var index = 0; index < TerminalAccess.MaxPendingPerAddress; index++)
            Assert.NotNull(_devices.Start(Here));
        Assert.Null(_devices.Start(Here));
        Assert.NotNull(_devices.Start("198.51.100.1"));

        for (var address = 2; _devices.Count < TerminalAccess.MaxPending; address++)
            for (var index = 0;
                 index < TerminalAccess.MaxPendingPerAddress && _devices.Count < TerminalAccess.MaxPending;
                 index++)
                Assert.NotNull(_devices.Start("198.51.100." + address));
        Assert.Null(_devices.Start("192.0.2.1"));
    }

    [Fact]
    public void Eleven_codes_from_eleven_addresses_in_one_IPv6_64_hit_the_per_address_cap()
    {
        var addresses = Enumerable.Range(1, 11)
            .Select(host => ClientAddress.Normalize(IPAddress.Parse($"2001:db8:1:2::{host:x}")))
            .ToList();

        Assert.Single(addresses.Distinct());
        Assert.Equal("2001:db8:1:2::/64", addresses[0]);
        Assert.All(addresses.Take(10), address => Assert.NotNull(_devices.Start(address)));
        Assert.Null(_devices.Start(addresses[10]));
    }

    [Fact]
    public void An_address_is_normalized_once_for_every_caller()
    {
        Assert.Equal("203.0.113.7", ClientAddress.Normalize(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.Equal("203.0.113.7", ClientAddress.Normalize(IPAddress.Parse("203.0.113.7")));
        Assert.Equal("unknown", ClientAddress.Normalize(null));
    }

    [Fact]
    public void An_expired_code_reads_as_expired_once_then_as_unknown()
    {
        var code = _devices.Start(Here)!;

        _clock.Advance(TimeSpan.FromSeconds(TerminalAccess.ExpiresInSeconds));

        Assert.Null(_devices.Find(code.UserCode));
        var started = _devices.Start(Here)!;
        _clock.Advance(TimeSpan.FromSeconds(TerminalAccess.ExpiresInSeconds));
        Assert.Equal("expired_token", _devices.Poll(started.DeviceCode).Error);
        Assert.Equal("invalid_grant", _devices.Poll(started.DeviceCode).Error);
        Assert.False(_devices.Decide(started.UserCode, Approval));
    }

    [Fact]
    public void A_start_sweeps_the_expired_codes_first()
    {
        for (var index = 0; index < TerminalAccess.MaxPendingPerAddress; index++)
            _devices.Start(Here);

        _clock.Advance(TimeSpan.FromSeconds(TerminalAccess.ExpiresInSeconds));

        Assert.NotNull(_devices.Start(Here));
        Assert.Equal(1, _devices.Count);
    }

    [Fact]
    public void Polling_under_the_interval_answers_slow_down_and_adds_five_seconds()
    {
        var code = _devices.Start(Here)!;

        Assert.Equal("authorization_pending", _devices.Poll(code.DeviceCode).Error);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("slow_down", _devices.Poll(code.DeviceCode).Error);
        // Ten seconds now, from the poll that was refused.
        _clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("slow_down", _devices.Poll(code.DeviceCode).Error);
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("authorization_pending", _devices.Poll(code.DeviceCode).Error);
    }

    [Fact]
    public void An_approved_code_is_handed_out_once()
    {
        var code = _devices.Start(Here)!;
        Assert.NotNull(_devices.Find(code.UserCode));
        Assert.True(_devices.Decide(code.UserCode, Approval));
        Assert.False(_devices.Decide(code.UserCode, Approval));
        // Decided: the approval page no longer finds it.
        Assert.Null(_devices.Find(code.UserCode));

        var poll = _devices.Poll(code.DeviceCode);
        Assert.Equal(Approval, poll.Approval);
        Assert.Equal(Here, poll.ClientAddress);
        // Claimed by the first poll while its row is written.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("authorization_pending", _devices.Poll(code.DeviceCode).Error);
        _devices.Unclaim(code.DeviceCode);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(Approval, _devices.Poll(code.DeviceCode).Approval);
        _devices.Issued(code.DeviceCode);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("invalid_grant", _devices.Poll(code.DeviceCode).Error);
    }

    [Fact]
    public void A_denied_code_answers_access_denied_once()
    {
        var code = _devices.Start(Here)!;

        Assert.True(_devices.Decide(code.UserCode, null));

        Assert.Equal("access_denied", _devices.Poll(code.DeviceCode).Error);
        Assert.Equal("invalid_grant", _devices.Poll(code.DeviceCode).Error);
        Assert.Equal("invalid_grant", _devices.Poll("not-a-code").Error);
    }

    [Fact]
    public void Ten_failed_codes_close_the_window_for_the_rest_of_its_ten_minutes()
    {
        for (var index = 0; index < TerminalAccess.MaxFailures; index++)
        {
            Assert.Null(_devices.Throttled("u-member"));
            _devices.Fail("u-member");
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(10), _devices.Throttled("u-member"));
        Assert.Null(_devices.Throttled("u-other"));
        _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(10));
        Assert.Null(_devices.Throttled("u-member"));
    }
}
