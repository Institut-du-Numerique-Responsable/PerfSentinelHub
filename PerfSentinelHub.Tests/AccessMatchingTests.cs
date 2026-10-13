using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

public sealed class AccessMatchingTests
{
    private const string Token = "ci-shop-0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("cart", "cart", true)]
    [InlineData("cart", "cart-api", false)]
    [InlineData("cart*", "cart", true)]
    [InlineData("cart*", "cart-api", true)]
    [InlineData("cart*", "car", false)]
    [InlineData("cart*", "my-cart", false)]
    // Ordinal: no case folding, and no LIKE or GLOB meaning for any character.
    [InlineData("Cart", "cart", false)]
    [InlineData("cart*", "Cart-api", false)]
    [InlineData("c_rt", "cart", false)]
    [InlineData("c%*", "cart", false)]
    public void A_part_is_exact_or_a_prefix_and_case_sensitive(string part, string service, bool reads)
    {
        var pattern = AccessPattern.Compile(new PatternOptions { Service = part });

        Assert.Equal(reads, pattern.Reads(Copy("prod", service)));
    }

    [Fact]
    public void An_attribute_splits_on_its_first_equals_sign()
    {
        var pattern = AccessPattern.Compile(new PatternOptions { Attribute = "team=a=b*" });

        Assert.Equal("team", pattern.Key);
        Assert.Equal("a=b", pattern.Value);
        Assert.True(pattern.ValueIsPrefix);
        Assert.True(pattern.Reads(Copy("prod", "cart", Row("team", "a", "team", "a=bc"))));
        Assert.False(pattern.Reads(Copy("prod", "cart", Row("team=a", "b", "team=a", "b"))));
    }

    [Theory]
    [InlineData("*", null, null)]
    [InlineData(null, "*", null)]
    [InlineData(null, null, "team=*")]
    public void A_bare_star_is_refused_in_every_part(string? source, string? service, string? attribute)
    {
        var options = ConfigurationTests.ValidOptions() with
        {
            Auth = new AuthOptions
            {
                Enabled = true,
                AuthorizationEndpoint = new Uri("https://idp.example/authorize"),
                TokenEndpoint = new Uri("https://idp.example/token"),
                UserInformationEndpoint = new Uri("https://idp.example/userinfo"),
                ClientId = "hub",
                ClientSecret = "secret" // gitleaks:allow -- synthetic test credential
            },
            Access = new AccessOptions
            {
                Enabled = true,
                Admins = ["admin"],
                Projects =
                [
                    new ProjectOptions
                    {
                        Name = "all",
                        Patterns = [new PatternOptions { Source = source, Service = service, Attribute = attribute }]
                    }
                ]
            }
        };

        string[] failures = [.. new HubOptionsValidator().Validate(null, options).Failures ?? []];

        Assert.Contains(failures, failure => failure.StartsWith("Hub:Access:Projects:0:Patterns:0:", StringComparison.Ordinal));
    }

    // A copy seen under team=shop and team=billing stays visible to both, and writable by neither.
    [Fact]
    public void A_read_matches_any_grouping_and_a_write_needs_every_one()
    {
        var shop = AccessPattern.Compile(new PatternOptions { Attribute = "team=shop" });
        var both = Copy("prod", "cart",
            Row("team", "shop", "team", "shop"),
            Row("team", "billing", "team", "billing"));
        var onlyShop = Copy("prod", "cart",
            Row("k8s.namespace.name", "shop-ns", "k8s.namespace.name", "shop-ns"),
            Row("k8s.namespace.name", "shop-ns", "team", "shop"),
            Row("k8s.namespace.name", "shop-eu", "team", "shop"));
        // A grouping identity that never carried the key, and the "no grouping" marker H1 writes.
        var identityWithoutKey = Copy("prod", "cart",
            Row("team", "shop", "team", "shop"),
            Row("k8s.namespace.name", "other-ns", "k8s.namespace.name", "other-ns"));
        var observedWithoutGrouping = Copy("prod", "cart", Row("team", "shop", "team", "shop"), Row("", "", "", ""));

        Assert.True(shop.Reads(both));
        Assert.False(shop.Writes(both));
        Assert.True(shop.Writes(onlyShop));
        Assert.True(shop.Reads(identityWithoutKey));
        Assert.False(shop.Writes(identityWithoutKey));
        Assert.True(shop.Reads(observedWithoutGrouping));
        Assert.False(shop.Writes(observedWithoutGrouping));
        // A copy with no row never attribute-matches, for a read or a write.
        Assert.False(shop.Reads(Copy("prod", "cart")));
        Assert.False(shop.Writes(Copy("prod", "cart")));
        // The marker alone matches no key.
        Assert.False(shop.Reads(Copy("prod", "cart", Row("", "", "", ""))));
    }

    [Fact]
    public void A_write_is_judged_on_the_source_and_service_parts_like_a_read()
    {
        var pattern = AccessPattern.Compile(new PatternOptions { Source = "prod", Service = "cart*" });

        Assert.True(pattern.Writes(Copy("prod", "cart-api")));
        Assert.False(pattern.Writes(Copy("prod-eu", "cart-api")));
        Assert.False(pattern.Writes(Copy("prod", "billing")));
    }

    // findings.service is fleet-wide and any import key rewrites it, so only the copy's own counts.
    [Fact]
    public void A_copy_without_its_own_service_matches_no_service_part()
    {
        var unnamed = Copy("prod", null, Row("team", "shop", "team", "shop"));

        Assert.False(AccessPattern.Compile(new PatternOptions { Service = "cart" }).Reads(unnamed));
        Assert.False(AccessPattern.Compile(new PatternOptions { Service = "c*" }).Reads(unnamed));
        Assert.True(AccessPattern.Compile(new PatternOptions { Source = "prod" }).Reads(unnamed));
        Assert.True(AccessPattern.Compile(new PatternOptions { Attribute = "team=shop" }).Writes(unnamed));
    }

    // The environment bound is the caller's: H3 selects the daemon copies of the service in
    // T's environment (spec §7.8) and passes only those, so every copy here sits in it.
    [Fact]
    public void A_trace_subject_needs_every_copy_of_the_service_in_its_environment_to_write_match()
    {
        var shop = AccessPattern.Compile(new PatternOptions { Attribute = "team=shop" });
        var shopCopy = Copy("prod-daemon", "cart", Row("team", "shop", "team", "shop"));
        var billingCopy = Copy("prod-daemon-2", "cart", Row("team", "billing", "team", "billing"));

        Assert.True(shop.CoversSubject("tempo", "cart", [shopCopy, shopCopy with { Source = "prod-daemon-eu" }]));
        // One planted copy in that environment is enough to deny: it cannot pull a service into a project.
        Assert.False(shop.CoversSubject("tempo", "cart", [shopCopy, billingCopy]));
        // With no copy, an attribute part has no evidence and matches nothing.
        Assert.False(shop.CoversSubject("tempo", "cart", []));
        // A source or service part needs no copy at all.
        Assert.True(AccessPattern.Compile(new PatternOptions { Source = "tem*" }).CoversSubject("tempo", "cart", []));
        Assert.True(AccessPattern.Compile(new PatternOptions { Service = "cart" }).CoversSubject("tempo", "cart", []));
        Assert.False(AccessPattern.Compile(new PatternOptions { Service = "cart" }).CoversSubject("tempo", "billing", []));
        Assert.False(AccessPattern.Compile(new PatternOptions { Source = "jaeger" })
            .CoversSubject("tempo", "cart", [shopCopy]));
        // The copies' own source is not the subject's: a source part reads T alone.
        Assert.True(AccessPattern.Compile(new PatternOptions { Source = "tempo", Attribute = "team=shop" })
            .CoversSubject("tempo", "cart", [shopCopy]));
    }

    [Fact]
    public void Admin_comes_only_from_admins_admin_groups_or_the_admin_role_on_the_whole_hub()
    {
        var policy = Policy(access => access with
        {
            Admins = ["root-sub"],
            AdminGroups = ["/hub-admins", "internal:ops"],
            Groups = [new InternalGroupOptions { Name = "ops", Members = ["ops-sub"] }],
            Grants =
            [
                new GrantOptions { Group = "/leads", Project = "*", Role = "admin" },
                new GrantOptions { Group = "/everyone", Project = "*", Role = "member" }
            ]
        });

        Assert.True(policy.IsAdmin(Person("root-sub")));
        Assert.True(policy.IsAdmin(Person("someone", "/hub-admins")));
        Assert.True(policy.IsAdmin(Person("ops-sub")));
        Assert.True(policy.IsAdmin(Person("someone", "/leads")));
        Assert.False(policy.IsAdmin(Person("someone", "/everyone")));
        // A provider cannot hand out an internal group, whatever it names.
        Assert.False(policy.IsAdmin(Person("someone", "internal:ops")));
        Assert.False(policy.IsAdmin(Person("someone", "INTERNAL:ops")));
        // Subjects are compared ordinally.
        Assert.False(policy.IsAdmin(Person("Root-sub")));
        // A terminal token keeps the person's grants but never the whole-Hub powers.
        Assert.False(policy.IsAdmin(new AccessCaller(AccessCaller.TerminalToken, "root-sub", [])));
        Assert.Equal(AccessScope.Nothing, policy.Scope(
            new AccessCaller(AccessCaller.TerminalToken, "root-sub", []), AccessPermissions.AcksWrite));
        Assert.Equal(AccessScope.Unrestricted, policy.Scope(Person("root-sub"), AccessPermissions.AccessAdmin));
        Assert.Equal(AccessScope.Unrestricted, policy.Scope(Person("root-sub"), AccessPermissions.AcksWrite));
    }

    [Fact]
    public void A_scope_unions_the_patterns_of_every_project_granted_the_permission()
    {
        var policy = Policy(access => access with
        {
            Groups = [new InternalGroupOptions { Name = "oncall", Members = ["oncall-sub"] }],
            Grants =
            [
                new GrantOptions { Group = "/teams/shop", Project = "shop", Role = "reader" },
                new GrantOptions { Group = "/teams/shop", Project = "lab", Role = "auditor" },
                new GrantOptions { Group = "internal:oncall", Project = "*", Role = "member" }
            ]
        });
        var shopper = Person("shop-sub", "/teams/shop");

        var read = policy.Scope(shopper, AccessPermissions.FindingsRead);
        Assert.Equal<string?>(["team", null], read.Patterns!.Select(pattern => pattern.Key));
        Assert.True(read.Reads(Copy("lab", "anything")));
        Assert.True(read.HoldsAtSource("lab"));
        Assert.False(read.HoldsAtSource("prod"));

        var run = policy.Scope(shopper, AccessPermissions.AnalysesRun);
        Assert.Equal("team", Assert.Single(run.Patterns!).Key);
        Assert.False(run.HoldsAtSource("lab"));

        Assert.Equal(AccessScope.Nothing, policy.Scope(shopper, AccessPermissions.AcksWrite));
        Assert.Equal(AccessScope.Nothing, policy.Scope(shopper, AccessPermissions.AccessAdmin));
        Assert.Equal(AccessScope.Nothing, policy.Scope(Person("stranger", "/teams/billing"), AccessPermissions.FindingsRead));
        Assert.Equal(AccessScope.Unrestricted, policy.Scope(Person("oncall-sub"), AccessPermissions.AcksWrite));
        Assert.Equal(AccessScope.Nothing, policy.Scope(Person("oncall-sub"), AccessPermissions.AccessAdmin));
        Assert.True(AccessScope.Unrestricted.HoldsAtSource("anything"));
        Assert.False(AccessScope.Nothing.Reads(Copy("lab", "anything")));
    }

    // Decided, question 5: a machine token reads findings, its group's grants intersected with that.
    [Fact]
    public void A_machine_token_holds_findings_read_at_most_and_is_never_an_admin()
    {
        var policy = Policy(access => access with
        {
            AdminGroups = ["internal:ci-root"],
            Groups = [new InternalGroupOptions { Name = "ci-a" }, new InternalGroupOptions { Name = "ci-root" }],
            Grants = [new GrantOptions { Group = "internal:ci-a", Project = "shop", Role = "member" }],
            MachineTokens =
            [
                new MachineTokenOptions { Name = "ci-shop", Group = "internal:ci-a", Token = Token, ExpiresAt = Now },
                new MachineTokenOptions
                {
                    Name = "ci-root", Group = "internal:ci-root", Token = Token + "-root", ExpiresAt = Now
                }
            ]
        });
        var shop = Machine("ci-shop");
        var root = Machine("ci-root");

        Assert.Equal("internal:ci-a", Assert.Single(policy.EffectiveGroups(shop)));
        Assert.Equal("team", Assert.Single(policy.Scope(shop, AccessPermissions.FindingsRead).Patterns!).Key);
        Assert.Equal(AccessScope.Nothing, policy.Scope(shop, AccessPermissions.AcksWrite));
        Assert.Equal(AccessScope.Nothing, policy.Scope(shop, AccessPermissions.TracesRead));
        Assert.False(policy.IsAdmin(root));
        Assert.Equal(AccessScope.Unrestricted, policy.Scope(root, AccessPermissions.FindingsRead));
        Assert.Equal(AccessScope.Nothing, policy.Scope(root, AccessPermissions.SourcesRead));
        // A removed entry leaves a token naming nothing.
        Assert.Empty(policy.EffectiveGroups(Machine("ci-gone")));
    }

    // Only fingerprints are kept, every one is compared, and an expired token names nobody.
    [Fact]
    public void A_machine_token_is_found_by_its_fingerprint_until_it_expires()
    {
        var policy = Policy(access => access with
        {
            Groups = [new InternalGroupOptions { Name = "ci-a" }],
            MachineTokens =
            [
                new MachineTokenOptions { Name = "ci-shop", Group = "internal:ci-a", Token = Token, ExpiresAt = Now },
                new MachineTokenOptions
                {
                    Name = "ci-later", Group = "internal:ci-a", Token = Token + "-later", ExpiresAt = Now.AddDays(1)
                }
            ]
        });

        Assert.Equal("ci-shop", policy.MachineTokenName(Token, Now.AddTicks(-1)));
        Assert.Null(policy.MachineTokenName(Token, Now));
        Assert.Equal("ci-later", policy.MachineTokenName(Token + "-later", Now));
        Assert.Null(policy.MachineTokenName(Token[..^1], Now.AddDays(-1)));
        Assert.Null(policy.MachineTokenName("", Now.AddDays(-1)));
    }

    // Shared handlers take the policy as a service in both modes, so it exists with access off.
    [Fact]
    public void Off_the_policy_is_registered_disabled_and_restricts_nothing()
    {
        using var hub = new HubApplicationFactory();
        var policy = hub.Services.GetRequiredService<AccessPolicy>();

        Assert.False(policy.Enabled);
        Assert.Equal(AccessScope.Unrestricted, policy.Scope(Person("anyone"), AccessPermissions.AcksWrite));
        Assert.Equal(AccessScope.Unrestricted, policy.Scope(Machine("ci"), AccessPermissions.AcksWrite));
        Assert.False(policy.IsAdmin(Person("anyone")));
        Assert.Null(policy.MachineTokenName(Token, Now));
    }

    private static AccessPolicy Policy(Func<AccessOptions, AccessOptions> change)
    {
        var access = new AccessOptions
        {
            Enabled = true,
            Projects =
            [
                new ProjectOptions { Name = "shop", Patterns = [new PatternOptions { Attribute = "team=shop" }] },
                new ProjectOptions { Name = "lab", Patterns = [new PatternOptions { Source = "lab" }] }
            ],
            Roles = [new RoleOptions { Name = "auditor", Permissions = ["findings.read", "traces.read"] }]
        };
        return new AccessPolicy(Options.Create(new HubOptions { Access = change(access) }));
    }

    private static AccessCaller Person(string subject, params string[] groups)
    {
        return new AccessCaller(AccessCaller.Session, subject, groups);
    }

    private static AccessCaller Machine(string name)
    {
        return new AccessCaller(AccessCaller.MachineToken, AccessCaller.MachinePrefix + name, []);
    }

    private static AccessCopy Copy(string source, string? service, params AccessAttribute[] rows)
    {
        return new AccessCopy(source, service, rows);
    }

    private static AccessAttribute Row(string groupingKey, string groupingValue, string key, string value)
    {
        return new AccessAttribute(groupingKey, groupingValue, key, value);
    }
}
