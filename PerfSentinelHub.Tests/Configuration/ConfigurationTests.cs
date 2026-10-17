using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests.Configuration;

public sealed class ConfigurationTests
{
    private const string AckSecret = "ack-secret"; // gitleaks:allow -- synthetic test credential

    private const string
        DisclosureKey = "disclosure-import-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    private const string
        WebhookSecret = "webhook-secret-0123456789abcdef0123"; // gitleaks:allow -- synthetic test credential

    private const string WebhookUrl =
        "Hub:Disclosures:Webhooks:0:Url must be an absolute https URL, or http to a loopback address, without credentials or fragment.";

    private const string
        MachineTokenValue = "ci-shop-0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential

    private const string AttributeShape =
        "Hub:Access:Projects:0:Patterns:0:Attribute must read key=value: a key of 1 to 256 characters with no "
        + "control character and no '*', and a value of 1 to 256, exact or a prefix ending in a single '*'.";

    private const string RoleName =
        "Hub:Access:Roles:0:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', unique with case ignored, "
        + "and not reader, member or admin.";

    private const string RolePermissions =
        "Hub:Access:Roles:0:Permissions must list one or more of findings.read, traces.read, sources.read, "
        + "analyses.run, acks.write, disclosures.read and disclosures.manage, each once.";

    private const string TokenName =
        "Hub:Access:MachineTokens:0:Name must match ^[a-z0-9][a-z0-9._-]{0,63}$ and be unique.";

    private const string TokenShape =
        "Hub:Access:MachineTokens:0:Token must be 32 to 512 characters of A-Z, a-z, 0-9, '-', '.', '_', '~', '+' "
        + "and '/', with trailing '=' allowed, and must not start with pshub_.";

    private const string TokenExpiry = "Hub:Access:MachineTokens:0:ExpiresAt must be set, at most 365 days from now.";

    private const string LocalPasswordHash =
        "$pbkdf2-sha256$i=600000$AAECAwQFBgcICQoLDA0ODw$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY";

    private const string LocalName =
        "Hub:Access:LocalAccounts:0:Name must match ^[a-z0-9][a-z0-9._-]{0,63}$ and be unique.";

    private const string LocalHash =
        "Hub:Access:LocalAccounts:0:PasswordHash must be the PHC string PerfSentinelHub hash-password prints: "
        + "$pbkdf2-sha256$i=600000$<salt>$<key>.";

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string> InvalidDisclosures => new()
    {
        { "short key", "Hub:Disclosures:ImportKey must be at least 32 characters with no control character." },
        { "source key", "Hub:Disclosures:ImportKey must differ from every source import API key and machine token." },
        { "days", "Hub:Disclosures:ShareLinkDays must be between 1 and 30." },
        { "remote http", WebhookUrl },
        {
            "internal webhook",
            "Hub:Disclosures:Webhooks:0:Confidentiality must be public: an internal report never leaves the Hub."
        },
        {
            "short secret",
            "Hub:Disclosures:Webhooks:0:Secret must be at least 32 characters with no control character."
        },
        {
            "webhook name",
            "Hub:Disclosures:Webhooks:1:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', and unique."
        },
        { "webhook days", "Hub:Disclosures:Webhooks:0:ShareLinkDays must be between 1 and 30." },
        { "identity alone", "Hub:Disclosures:ExpectedIdentity and ExpectedIssuer go together or not at all." },
        {
            "share link without a public url",
            "Hub:Disclosures:PublicUrl is required by a webhook with IncludeShareLink: the link it sends must name the Hub."
        },
        {
            "public url query",
            "Hub:Disclosures:PublicUrl must be an absolute HTTP(S) URL without credentials, query, or fragment."
        },
        {
            "public url path",
            "Hub:Disclosures:PublicUrl must name the Hub's root, with no path: the Hub serves every route from /."
        }
    };

    public static TheoryData<DbStatOptions, string> InvalidDatabaseStatistics => new()
    {
        { new DbStatOptions { Metric = "m" }, "Source 'prod' pg_stat requires a Prometheus URL." },
        {
            new DbStatOptions { Prometheus = new Uri("ftp://vm") },
            "Source 'prod' pg_stat Prometheus must be an absolute HTTP(S) URL without credentials, query, or fragment."
        },
        {
            new DbStatOptions { Prometheus = new Uri("http://vm"), Unit = "picoseconds" },
            "Source 'prod' pg_stat unit must be seconds or milliseconds."
        },
        {
            new DbStatOptions { Prometheus = new Uri("http://vm"), Top = 0 },
            "Source 'prod' pg_stat top must be between 1 and 1000."
        },
        {
            new DbStatOptions { Prometheus = new Uri("http://vm"), Selector = "a=\"b\"\n" },
            "Source 'prod' pg_stat selector must not contain control characters."
        },
        {
            new DbStatOptions { Prometheus = new Uri("http://vm"), AuthHeaderName = "Authorization" },
            "Source 'prod' must provide both pg_stat auth header name and value."
        }
    };

    [Fact]
    public void Indexed_source_configuration_binds_to_the_options_model()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Sources:0:Id"] = "test",
                ["Hub:Sources:0:Name"] = "Test",
                ["Hub:Sources:0:Environment"] = "test",
                ["Hub:Sources:0:BaseUrl"] = "http://127.0.0.1:4318"
            })
            .Build();

        var options = configuration.GetSection(HubOptions.SectionName).Get<HubOptions>();

        Assert.NotNull(options);
        Assert.Single(options.Sources);
    }

    [Theory]
    [InlineData("", "http://daemon:4318", "prod")]
    [InlineData("bad/source", "http://daemon:4318", "prod")]
    [InlineData("prod", "file:///tmp/findings", "prod")]
    [InlineData("prod", "http://user@daemon:4318", "prod")]
    // An environment is a Prometheus label value and a member of the read API's closed set.
    [InlineData("prod", "http://daemon:4318", "pro\nd")]
    public void Invalid_source_is_rejected(string id, string url, string environment)
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { Id = id, BaseUrl = new Uri(url), Environment = environment }]
        };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_blank_name_does_not_hide_a_control_character_in_the_environment()
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { Name = "", Environment = "pro\nd" }]
        };

        // Both in one pass, so a bad source costs one restart and not two.
        string[] failures = [.. new HubOptionsValidator().Validate(null, options).Failures ?? []];
        Assert.Contains(failures, failure => failure.Contains("requires a name", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("control characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_global_options_are_rejected()
    {
        HubOptions[] invalid =
        [
            ValidOptions() with { Sources = [] },
            ValidOptions() with { Sources = [ValidSource() with { BaseUrl = null }] },
            ValidOptions() with { Sources = [ValidSource() with { BaseUrl = new Uri("https://daemon.example?a=b") }] },
            ValidOptions() with { Sources = [ValidSource() with { PublicUrl = new Uri("https://daemon.example#a") }] },
            ValidOptions() with { Sources = [ValidSource() with { PublicUrl = new Uri("ftp://daemon.example") }] },
            // A public header name with no public route has nothing to describe.
            ValidOptions() with { Sources = [ValidSource() with { PublicAuthHeaderName = "Authorization" }] },
            ValidOptions() with
            {
                Sources =
                [
                    ValidSource() with
                    {
                        PublicUrl = new Uri("https://public.example"), PublicAuthHeaderName = "Bad Header"
                    }
                ]
            },
            ValidOptions() with { Sources = [ValidSource(), ValidSource()] },
            ValidOptions() with { DatabasePath = "relative.db" },
            ValidOptions() with { PollInterval = TimeSpan.Zero },
            ValidOptions() with { HttpTimeout = TimeSpan.Zero },
            ValidOptions() with { Retention = TimeSpan.Zero },
            ValidOptions() with { MaxConcurrentPolls = 0 },
            ValidOptions() with { MaxConcurrentPolls = 33 },
            ValidOptions() with { DefaultReadLimit = 0 },
            ValidOptions() with { MaxReadLimit = 10_001 },
            ValidOptions() with { DefaultReadLimit = 101, MaxReadLimit = 100 },
            // A run row has to outlive the report it produced: an expired run
            // keeps its parameters so it can be relaunched as it stands.
            ValidOptions() with
            {
                Analysis = new AnalysisOptions
                {
                    ReportRetention = TimeSpan.FromDays(2),
                    RunRetention = TimeSpan.FromDays(1)
                }
            },
            ValidOptions() with
            {
                Analysis = new AnalysisOptions
                {
                    ReportRetention = TimeSpan.FromDays(1),
                    RunRetention = TimeSpan.FromDays(1)
                }
            }
        ];

        Assert.All(invalid, options =>
            Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded));
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("Authorization\r\nInjected", "secret")]
    [InlineData("Authorization", "secret\r\nInjected")]
    public void Invalid_auth_header_is_rejected(string? name, string? value)
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { AuthHeaderName = name, AuthHeaderValue = value }]
        };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    // One validator words both pairs, and the read pair's wording predates it.
    [Theory]
    [InlineData(null, "secret", "Source 'prod' must provide both auth header name and value.")]
    [InlineData("Authorization", "secret\nInjected", "Source 'prod' auth header contains a newline.")]
    [InlineData("Bad Header", "secret", "Source 'prod' auth header is invalid.")]
    public void The_read_credential_keeps_its_messages(string? name, string value, string message)
    {
        var source = ValidSource() with { AuthHeaderName = name, AuthHeaderValue = value };

        Assert.Equal(message, Assert.Single(Failures(source)));
    }

    [Theory]
    [InlineData("X-API-Key", null, "Source 'prod' must provide both ack header name and value.")]
    [InlineData(null, "ack-secret", "Source 'prod' must provide both ack header name and value.")]
    [InlineData("X-API-Key\r\nInjected", "ack-secret", "Source 'prod' ack header contains a newline.")]
    [InlineData("X-API-Key", "ack-secret\r\nInjected", "Source 'prod' ack header contains a newline.")]
    [InlineData("Bad Header", "ack-secret", "Source 'prod' ack header is invalid.")]
    public void Invalid_ack_header_is_rejected(string? name, string? value, string message)
    {
        var source = ValidSource() with { AckHeaderName = name, AckHeaderValue = value };

        Assert.Equal(message, Assert.Single(Failures(source)));
    }

    [Fact]
    public void Disclosures_bind_trimmed_and_are_off_without_an_import_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Disclosures:ImportKey"] = DisclosureKey + "\n",
                ["Hub:Disclosures:ShareLinkDays"] = "14",
                ["Hub:Disclosures:Webhooks:0:Name"] = "auditors",
                ["Hub:Disclosures:Webhooks:0:Url"] = "https://hooks.example/disclosures",
                ["Hub:Disclosures:Webhooks:0:Secret"] = WebhookSecret + "\n",
                ["Hub:Disclosures:Webhooks:0:IncludeShareLink"] = "true"
            })
            .Build();

        var disclosures = configuration.GetSection(HubOptions.SectionName).Get<HubOptions>()!.Disclosures;

        Assert.True(disclosures.Enabled);
        Assert.Equal(DisclosureKey, disclosures.ImportKey);
        Assert.Equal(14, disclosures.ShareLinkDays);
        var webhook = Assert.Single(disclosures.Webhooks);
        Assert.Equal(WebhookSecret, webhook.Secret);
        Assert.Equal("public", webhook.Confidentiality);
        Assert.Equal(7, webhook.ShareLinkDays);
        Assert.False(new DisclosuresOptions().Enabled);
        Assert.Empty(DisclosureFailures(new DisclosuresOptions()));
    }

    [Theory]
    [MemberData(nameof(InvalidDisclosures))]
    public void Invalid_disclosures_are_refused(string rule, string expected)
    {
        var webhook = new WebhookOptions
        { Name = "auditors", Url = new Uri("https://hooks.example/x"), Secret = WebhookSecret };
        var disclosures = new DisclosuresOptions { ImportKey = DisclosureKey, Webhooks = [webhook] };
        var options = ValidOptions() with { Disclosures = disclosures };
        options = rule switch
        {
            "short key" => options with { Disclosures = disclosures with { ImportKey = new string('k', 31) } },
            "source key" => options with { Sources = [ValidSource() with { ImportApiKey = DisclosureKey }] },
            "days" => options with { Disclosures = disclosures with { ShareLinkDays = 31 } },
            "remote http" => options with
            {
                Disclosures = disclosures with
                {
                    Webhooks = [webhook with { Url = new Uri("http://hooks.example/x") }]
                }
            },
            "internal webhook" => options with
            {
                Disclosures = disclosures with { Webhooks = [webhook with { Confidentiality = "internal" }] }
            },
            "short secret" => options with
            {
                Disclosures = disclosures with { Webhooks = [webhook with { Secret = new string('s', 31) }] }
            },
            "webhook name" => options with { Disclosures = disclosures with { Webhooks = [webhook, webhook] } },
            "webhook days" => options with
            {
                Disclosures = disclosures with { Webhooks = [webhook with { ShareLinkDays = 0 }] }
            },
            "identity alone" => options with
            {
                Disclosures = disclosures with { ExpectedIdentity = "https://github.com/acme/x" }
            },
            "share link without a public url" => options with
            {
                Disclosures = disclosures with { Webhooks = [webhook with { IncludeShareLink = true }] }
            },
            "public url query" => options with
            {
                Disclosures = disclosures with { PublicUrl = new Uri("https://hub.example/?x=1") }
            },
            "public url path" => options with
            {
                Disclosures = disclosures with { PublicUrl = new Uri("https://corp.example/perf-hub/") }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
        };

        Assert.Equal(expected, Assert.Single(new HubOptionsValidator().Validate(null, options).Failures ?? []));
    }

    [Fact]
    public void A_webhook_to_a_loopback_address_may_use_http()
    {
        var webhook = new WebhookOptions
        { Name = "local", Url = new Uri("http://127.0.0.1:9000/hook"), Secret = WebhookSecret };

        Assert.Empty(DisclosureFailures(new DisclosuresOptions { ImportKey = DisclosureKey, Webhooks = [webhook] }));
    }

    [Fact]
    public void An_import_key_equal_to_a_machine_token_is_refused()
    {
        var options = AccessOn() with { Disclosures = new DisclosuresOptions { ImportKey = MachineTokenValue } };

        Assert.Contains(
            "Hub:Disclosures:ImportKey must differ from every source import API key and machine token.",
            AccessFailures(options));
    }

    [Fact]
    public void Internal_reports_need_access_control()
    {
        Assert.Equal(
            "Hub:Disclosures:AllowInternal requires Hub:Access:Enabled: an internal report would be open to "
            + "anyone who reaches the Hub.",
            Assert.Single(
                DisclosureFailures(new DisclosuresOptions { ImportKey = DisclosureKey, AllowInternal = true })));
        Assert.Empty(AccessFailures(AccessOn() with
        {
            Disclosures = new DisclosuresOptions { ImportKey = DisclosureKey, AllowInternal = true }
        }));
    }

    [Fact]
    public void Disclosure_permissions_on_a_project_grant_are_refused()
    {
        var options = AccessOn(access => access with
        {
            Roles = [.. access.Roles, new RoleOptions { Name = "publisher", Permissions = ["disclosures.read"] }],
            Grants =
            [
                .. access.Grants, new GrantOptions { Group = "/teams/shop", Project = "shop", Role = "publisher" }
            ]
        });

        Assert.Equal(
            $"Grant {options.Access.Grants.Count - 1}: disclosures.read and disclosures.manage apply to the whole Hub; "
            + "grant a role carrying them on '*'.",
            Assert.Single(AccessFailures(options)));
        Assert.Empty(AccessFailures(options with
        {
            Access = options.Access with
            {
                Grants = [.. options.Access.Grants.SkipLast(1), options.Access.Grants[^1] with { Project = "*" }]
            }
        }));
    }

    [Fact]
    public void Only_admin_holds_the_disclosure_permissions_and_never_a_machine_token()
    {
        string[] disclosures = [AccessPermissions.DisclosuresRead, AccessPermissions.DisclosuresManage];

        Assert.Equal(["disclosures.read", "disclosures.manage"], disclosures);
        Assert.All(disclosures, permission => Assert.Contains(permission, AccessPermissions.BuiltInRoles["admin"]));
        Assert.All(disclosures,
            permission => Assert.DoesNotContain(permission, AccessPermissions.BuiltInRoles["reader"]));
        Assert.All(disclosures,
            permission => Assert.DoesNotContain(permission, AccessPermissions.BuiltInRoles["member"]));
        Assert.All(disclosures,
            permission => Assert.DoesNotContain(permission, AccessPermissions.MachineTokenPermissions));
    }

    private static string[] DisclosureFailures(DisclosuresOptions disclosures)
    {
        return
        [
            .. new HubOptionsValidator().Validate(null, ValidOptions() with { Disclosures = disclosures }).Failures ??
               []
        ];
    }

    [Fact]
    public void Database_statistics_bind_per_source()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Sources:0:Id"] = "test",
                ["Hub:Sources:0:PgStat:Prometheus"] = "http://vm.svc:8428",
                ["Hub:Sources:0:PgStat:Metric"] = "pg_stat_statements_total_exec_time",
                ["Hub:Sources:0:PgStat:Selector"] = "namespace=\"shop\",db_role=\"primary\"",
                ["Hub:Sources:0:PgStat:Unit"] = "milliseconds",
                ["Hub:Sources:0:PgStat:Top"] = "20",
                ["Hub:Sources:0:MySqlStat:Prometheus"] = "http://vm.svc:8428"
            })
            .Build();

        var source = Assert.Single(configuration.GetSection(HubOptions.SectionName).Get<HubOptions>()!.Sources);

        Assert.Equal(new Uri("http://vm.svc:8428"), source.PgStat!.Prometheus);
        Assert.Equal("pg_stat_statements_total_exec_time", source.PgStat.Metric);
        Assert.Equal("namespace=\"shop\",db_role=\"primary\"", source.PgStat.Selector);
        Assert.Equal("milliseconds", source.PgStat.Unit);
        Assert.Equal(20, source.PgStat.Top);
        Assert.Equal(new Uri("http://vm.svc:8428"), source.MySqlStat!.Prometheus);
    }

    [Fact]
    public void The_chart_s_database_statistics_shape_binds_and_validates()
    {
        // The chart writes every key, absent ones as null.
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Hub":{"Sources":[{"Id":"prod","Name":"P","Environment":"prod","BaseUrl":"https://d.example","PgStat":{"AuthHeaderName":null,"CallsMetric":null,"Metric":null,"Prometheus":"http://vm:8428","QueryLabel":null,"Selector":null,"Top":null,"Unit":null}}]}}"""));

        var source = Assert.Single(new ConfigurationBuilder().AddJsonStream(json).Build()
            .GetSection(HubOptions.SectionName).Get<HubOptions>()!.Sources);

        Assert.Equal(new Uri("http://vm:8428"), source.PgStat!.Prometheus);
        Assert.Null(source.PgStat.Top);
        Assert.Empty(Failures(source));
    }

    [Fact]
    public void Valid_database_statistics_pass()
    {
        var stats = new DbStatOptions
        {
            Prometheus = new Uri("https://vm.example"),
            Unit = "milliseconds",
            Top = 1000,
            Selector = "db_role=\"primary\"",
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer token" // gitleaks:allow -- synthetic test credential
        };

        Assert.Empty(Failures(ValidSource() with { PgStat = stats, MySqlStat = stats with { Unit = "picoseconds" } }));
    }

    [Theory]
    [MemberData(nameof(InvalidDatabaseStatistics), DisableDiscoveryEnumeration = true)]
    public void Invalid_database_statistics_are_rejected(DbStatOptions stats, string expected)
    {
        Assert.Equal(expected, Assert.Single(Failures(ValidSource() with { PgStat = stats })));
    }

    [Fact]
    public void A_mysql_unit_also_takes_picoseconds_and_names_its_own_family()
    {
        var stats = new DbStatOptions { Prometheus = new Uri("http://vm"), Unit = "minutes" };

        Assert.Equal(
            "Source 'prod' mysql_stat unit must be seconds, milliseconds or picoseconds.",
            Assert.Single(Failures(ValidSource() with { MySqlStat = stats })));
    }

    [Fact]
    public void Only_a_daemon_can_carry_an_ack_credential()
    {
        // A trace backend has no ack route: the credential would sit there for a
        // relay that can never use it.
        var source = AckSource() with { Kind = SourceKinds.Tempo };

        Assert.Equal(
            "Source 'prod' is not a daemon and cannot carry an ack credential.",
            Assert.Single(Failures(source)));
    }

    [Theory]
    [InlineData(AckSecret, AckSecret)]
    [InlineData("Bearer " + AckSecret, AckSecret)]
    [InlineData(AckSecret, "bearer  " + AckSecret)]
    public void An_ack_credential_differs_from_the_read_credential(string readValue, string ackValue)
    {
        // The daemon refuses a read key equal to its ack key, since every reader
        // could then write. It reads a key bare or behind a Bearer scheme.
        var source = AckSource() with
        {
            AuthHeaderName = "Authorization",
            AuthHeaderValue = readValue,
            AckHeaderValue = ackValue
        };

        Assert.Equal(
            "Source 'prod' ack credential must differ from its read credential.",
            Assert.Single(Failures(source)));
    }

    [Fact]
    public void A_daemon_can_carry_an_ack_credential_of_its_own()
    {
        Assert.Empty(Failures(AckSource()));
        Assert.Empty(Failures(AckSource() with { AuthHeaderName = "X-API-Key", AuthHeaderValue = "read-secret" }));
    }

    [Theory]
    [InlineData(false, false, true, 1)]
    [InlineData(true, false, true, 0)]
    [InlineData(false, true, true, 0)]
    [InlineData(false, false, false, 0)]
    public void An_ack_credential_is_warned_about_when_the_relay_can_identify_nobody(
        bool signsIn,
        bool trustsIdentityHeader,
        bool relays,
        int warnings)
    {
        var logger = new ListLogger<Program>();
        var options = ValidOptions() with
        {
            Auth = new AuthOptions { Enabled = signsIn },
            AckRelay = new AckRelayOptions { TrustIdentityHeader = trustsIdentityHeader },
            Sources = [relays ? AckSource() : ValidSource()]
        };

        HubAuthentication.WarnWhenAckRelayIdentifiesNobody(options, logger);

        Assert.Equal(warnings, logger.Messages.Count);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(AckSecret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("false", 1)]
    [InlineData("true", 0)]
    public async Task The_host_warns_at_startup_unless_the_identity_header_is_trusted(
        string trustIdentityHeader,
        int warnings)
    {
        var logger = new ListLogger<Program>();
        await using var hub = new HubApplicationFactory();
        await using var scoped = hub.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hub:AckRelay:TrustIdentityHeader", trustIdentityHeader);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILogger<Program>>(logger);
                services.PostConfigure<HubOptions>(options => options.Sources = [AckSource()]);
            });
        });

        using var client = scoped.CreateClient();

        Assert.Equal(warnings, logger.Messages.Count(message =>
            message.Contains("ack credential", StringComparison.Ordinal)));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(AckSecret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef\t0123456789abcdef")]
    public void Invalid_import_key_is_rejected(string value)
    {
        var options = ValidOptions() with
        {
            Sources = [ValidSource() with { ImportApiKey = value }]
        };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Import_key_is_trimmed_like_the_daemon_trims_its_key_file()
    {
        var source = ValidSource() with
        {
            // gitleaks:allow has to sit on the line the scanner matched, and the
            // match starts here, not on the brace that closed the initialiser.
            ImportApiKey = "0123456789abcdef0123456789abcdef\n" // gitleaks:allow -- synthetic test credential
        };
        var options = ValidOptions() with { Sources = [source] };

        Assert.Equal("0123456789abcdef0123456789abcdef", source.ImportApiKey);
        Assert.True(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Enabled_authentication_requires_a_usable_provider()
    {
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with { Auth = ValidAuth() }).Succeeded);

        AuthOptions[] invalid =
        [
            ValidAuth() with { ClientId = "" },
            ValidAuth() with { ClientSecret = null },
            ValidAuth() with { IdentityClaim = " " },
            ValidAuth() with { AuthorizationEndpoint = null },
            ValidAuth() with { TokenEndpoint = new Uri("/token", UriKind.Relative) },
            // Plain http carries the code and the access token in clear, loopback excepted.
            ValidAuth() with { UserInformationEndpoint = new Uri("http://idp.example/userinfo") },
            ValidAuth() with { TokenEndpoint = new Uri("https://user:pw@idp.example/token") }
        ];

        Assert.All(invalid, auth => Assert.False(
            new HubOptionsValidator().Validate(null, ValidOptions() with { Auth = auth }).Succeeded));
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with
        {
            Auth = ValidAuth() with { TokenEndpoint = new Uri("http://127.0.0.1:8081/token") }
        }).Succeeded);
        // Off, nothing about the provider is read, so nothing is required.
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with
        {
            Auth = new AuthOptions { Enabled = false }
        }).Succeeded);
    }

    [Fact]
    public void Required_groups_need_a_groups_field_no_blank_entry_and_sign_in_on()
    {
        string[] groups = ["/hub-users"];
        Assert.Empty(AuthFailures(ValidAuth() with { RequiredGroups = groups }));
        // Without a gate the groups field is never read, so it may be anything.
        Assert.Empty(AuthFailures(ValidAuth() with { GroupsClaim = " " }));

        Assert.Equal(
            "Hub:Auth:GroupsClaim must name a userinfo field when Hub:Auth:RequiredGroups is set.",
            Assert.Single(AuthFailures(ValidAuth() with { RequiredGroups = groups, GroupsClaim = " " })));
        Assert.Equal(
            "Hub:Auth:RequiredGroups must not hold a blank entry.",
            Assert.Single(AuthFailures(ValidAuth() with { RequiredGroups = ["/hub-users", " "] })));
        // Off, nobody signs in and nothing would check the groups: a gate the
        // operator believes is up would let every reader of the port in.
        Assert.Equal(
            "Hub:Auth:RequiredGroups requires Hub:Auth:Enabled: off, the Hub signs nobody in and checks no group.",
            Assert.Single(AuthFailures(new AuthOptions { RequiredGroups = groups })));
    }

    [Fact]
    public void Trusted_proxy_networks_must_each_be_a_CIDR_network()
    {
        Assert.Empty(AuthFailures(ValidAuth() with
        {
            TrustedProxyNetworks = ["10.42.0.0/16", "127.0.0.1/32", "fd00::/8"]
        }));
        Assert.Equal(
            "Hub:Auth:TrustedProxyNetworks entry 'ingress' is not a CIDR network.",
            Assert.Single(AuthFailures(ValidAuth() with { TrustedProxyNetworks = ["10.42.0.0/16", "ingress"] })));
        Assert.Equal(
            "Hub:Auth:TrustedProxyNetworks entry '10.42.0.0' is not a CIDR network.",
            Assert.Single(AuthFailures(ValidAuth() with { TrustedProxyNetworks = ["10.42.0.0"] })));
        // Off, nothing reads the list.
        Assert.Empty(AuthFailures(new AuthOptions { TrustedProxyNetworks = ["ingress"] }));
        Assert.Empty(new AuthOptions().TrustedProxyNetworks);
    }

    [Fact]
    public void Required_groups_bind_from_indexed_keys_onto_an_empty_default()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Auth:RequiredGroups:0"] = "/hub-admins",
                ["Hub:Auth:RequiredGroups:1"] = "/hub-users"
            })
            .Build();

        var auth = configuration.GetSection("Hub:Auth").Get<AuthOptions>();

        Assert.NotNull(auth);
        Assert.Equal(["/hub-admins", "/hub-users"], auth.RequiredGroups);
        Assert.Equal("groups", auth.GroupsClaim);
        Assert.Empty(new AuthOptions().RequiredGroups);
    }

    private static string[] AuthFailures(AuthOptions auth)
    {
        return [.. new HubOptionsValidator().Validate(null, ValidOptions() with { Auth = auth }).Failures ?? []];
    }

    private static AuthOptions ValidAuth()
    {
        return new AuthOptions
        {
            Enabled = true,
            AuthorizationEndpoint = new Uri("https://idp.example/authorize"),
            TokenEndpoint = new Uri("https://idp.example/token"),
            UserInformationEndpoint = new Uri("https://idp.example/userinfo"),
            ClientId = "hub",
            ClientSecret = "secret" // gitleaks:allow -- synthetic test credential
        };
    }

    [Fact]
    public void Analysis_settings_bind_from_the_documented_strings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Analysis:EngineBinaryPath"] = "/opt/perf-sentinel/perf-sentinel",
                ["Hub:Analysis:Timeout"] = "00:05:00",
                // A day is not "24:00:00": TimeSpan hours stop at 23, and the
                // README hands the operator this exact string to copy.
                ["Hub:Analysis:ReportRetention"] = "1.00:00:00",
                ["Hub:Sources:0:Id"] = "test",
                ["Hub:Sources:0:Name"] = "Test",
                ["Hub:Sources:0:Environment"] = "test",
                ["Hub:Sources:0:Kind"] = "jaeger_query",
                ["Hub:Sources:0:BaseUrl"] = "http://127.0.0.1:10428"
            })
            .Build();

        var options = configuration.GetSection(HubOptions.SectionName).Get<HubOptions>();

        Assert.NotNull(options);
        Assert.Equal("/opt/perf-sentinel/perf-sentinel", options.Analysis.EngineBinaryPath);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Analysis.Timeout);
        Assert.Equal(TimeSpan.FromHours(24), options.Analysis.ReportRetention);
        Assert.Equal(SourceKinds.JaegerQuery, options.Sources[0].Kind);
        Assert.True(new HubOptionsValidator().Validate(null, options with
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "hub.db"),
            // The documented paths are the container's, and a path with no drive is not
            // absolute on Windows, as the configuration reference says.
            Analysis = OperatingSystem.IsWindows()
                ? options.Analysis with
                {
                    EngineBinaryPath = Path.Combine(Path.GetTempPath(), "perf-sentinel"),
                    ReportDirectory = ReportDirectoryFixture.ReportDirectory
                }
                : options.Analysis
        }).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Daemon")]
    [InlineData("victoria")]
    public void Unknown_source_kind_is_rejected(string kind)
    {
        var options = ValidOptions() with { Sources = [ValidSource() with { Kind = kind }] };

        Assert.False(new HubOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Only_a_daemon_can_carry_an_import_key()
    {
        // A trace backend never pushes: a key on one is a misconfiguration
        // that would otherwise sit there authorising an import path nothing
        // uses.
        var source = ValidSource() with
        {
            Kind = SourceKinds.Tempo,
            ImportApiKey = "0123456789abcdef0123456789abcdef" // gitleaks:allow -- synthetic test credential
        };

        Assert.False(new HubOptionsValidator().Validate(null, ValidOptions() with { Sources = [source] }).Succeeded);
    }

    [Fact]
    public void Trace_retention_belongs_to_a_backend_and_stays_in_range()
    {
        SourceOptions[] invalid =
        [
            // A daemon takes no window, so nothing would read the value.
            ValidSource() with { RetentionHours = 24 },
            ValidSource() with { Kind = SourceKinds.Tempo, RetentionHours = 0 },
            ValidSource() with { Kind = SourceKinds.Tempo, RetentionHours = 87_601 }
        ];

        Assert.All(invalid, source => Assert.False(
            new HubOptionsValidator().Validate(null, ValidOptions() with { Sources = [source] }).Succeeded));
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions() with
        {
            Sources = [ValidSource() with { Kind = SourceKinds.Tempo, RetentionHours = 24 }]
        }).Succeeded);
    }

    [Fact]
    public void Invalid_analysis_options_are_rejected()
    {
        AnalysisOptions[] invalid =
        [
            new() { EngineBinaryPath = "relative/perf-sentinel" },
            new() { EngineBinaryPath = "  " },
            new() { Workers = 0 },
            new() { Workers = 17 },
            new() { MaxTracesCap = 0 },
            // Above the engine's own ceiling: accepted here, this only moved the
            // failure to argument parsing, after the operator had been shown the
            // number and the command carrying it.
            new() { MaxTracesCap = AnalysisOptions.EngineMaxTraces + 1 },
            new() { Timeout = TimeSpan.Zero },
            new() { Timeout = TimeSpan.FromHours(2) },
            new() { ReportRetention = TimeSpan.Zero }
        ];

        Assert.All(invalid, analysis => Assert.False(
            new HubOptionsValidator().Validate(null, ValidOptions() with { Analysis = analysis }).Succeeded));
    }

    [Fact]
    public void Invalid_bound_configuration_stops_the_host()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.PostConfigure<HubOptions>(options =>
            {
                options.DatabasePath = Path.Combine(Path.GetTempPath(), $"hub-{Guid.NewGuid():N}.db");
                options.Sources =
                [
                    new SourceOptions
                    {
                        Id = "prod",
                        Name = "Production",
                        Environment = "prod",
                        BaseUrl = new Uri("file:///tmp/x")
                    }
                ];
            })));

        Assert.Throws<OptionsValidationException>(factory.CreateClient);
    }

    [Fact]
    public void A_valid_access_configuration_is_accepted()
    {
        Assert.Empty(AccessFailures(AccessOn()));
    }

    // A3: the authorization key must be an id the provider never reassigns.
    [Theory]
    [InlineData("sub", true)]
    [InlineData("oid", true)]
    [InlineData("uuid", true)]
    [InlineData("account_id", true)]
    [InlineData("email", false)]
    [InlineData("Sub", false)]
    public void The_subject_claim_names_an_immutable_account_id(string claim, bool accepted)
    {
        var options = AccessOn();
        options = options with { Auth = options.Auth with { SubjectClaim = claim } };

        Assert.Equal(
            accepted
                ? []
                :
                [
                    $"Hub:Auth:SubjectClaim must name an immutable account id (sub, oid, uuid or account_id) when Hub:Access:Enabled; '{claim}' can be changed or reused at the provider."
                ],
            AccessFailures(options));
    }

    // A14: with access on /metrics answers on its own port only, so one must be set.
    [Fact]
    public void Access_needs_a_metrics_port()
    {
        Assert.Equal(
            [
                "Hub:Access:Enabled needs Hub:MetricsPort: with access on, /metrics answers only on its own port, and without one it would stop answering everywhere."
            ],
            AccessFailures(AccessOn() with { MetricsPort = null }));
    }

    // M1, with access on or off: the key is opt-in.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void A_metrics_port_is_a_tcp_port(int port, bool accepted)
    {
        var failures = new HubOptionsValidator().Validate(null, ValidOptions() with { MetricsPort = port }).Failures ??
                       [];

        Assert.Equal(accepted ? [] : ["Hub:MetricsPort must be between 1 and 65535."], failures);
    }

    [Fact]
    public void Off_nothing_under_access_is_validated()
    {
        Assert.True(new HubOptionsValidator().Validate(null, ValidOptions()).Succeeded);
        Assert.False(ValidOptions().Access.Enabled);
        var garbage = AccessOn(access => access with
        {
            Enabled = false,
            Admins = ["machine:x", " "],
            Projects = [new ProjectOptions { Name = "*" }],
            MachineTokens = [new MachineTokenOptions { Name = "BAD", Token = "short" }],
            LocalAccounts = [new LocalAccountOptions { Name = "BAD", PasswordHash = "plain" }]
        });

        Assert.Empty(AccessFailures(garbage));
    }

    // Decided, question 9: the provider is the source of truth for admins, Admins stays as break-glass.
    [Fact]
    public void A_provider_admin_group_and_a_provider_admin_grant_on_the_whole_hub_are_accepted()
    {
        var options = AccessOn(access => access with
        {
            Admins = [],
            AdminGroups = ["/hub-admins"],
            Grants = [.. access.Grants, new GrantOptions { Group = "/platform/leads", Project = "*", Role = "admin" }]
        });

        Assert.Empty(AccessFailures(options));
    }

    // A4 ties GroupsClaim to provider references: internal ones need no claim.
    [Fact]
    public void A_blank_groups_claim_is_accepted_when_every_group_reference_is_internal()
    {
        var options = AccessOn(access => access with
        {
            AdminGroups = ["internal:oncall"],
            Grants = [.. access.Grants.Where(grant => grant.Group.StartsWith("internal:", StringComparison.Ordinal))]
        });
        options = options with { Auth = options.Auth with { GroupsClaim = " " } };

        Assert.Empty(AccessFailures(options));
    }

    [Theory]
    [InlineData("A1", "Hub:Access:Enabled requires Hub:Auth:Enabled: access control needs a signed-in caller.")]
    [InlineData("A2",
        "With Hub:Access:Enabled only a Hub session or token names a caller; Hub:AckRelay:TrustIdentityHeader cannot be set.")]
    [InlineData("A4", "Hub:Auth:GroupsClaim must name a userinfo field when Hub:Access refers to a provider group.")]
    [InlineData("A5", "Hub:Access needs at least one entry in Admins or AdminGroups.")]
    [InlineData("A6 blank", "Hub:Access:Admins:0 must be a subject of 1 to 256 characters with no control character.")]
    [InlineData("A6 long", "Hub:Access:Admins:0 must be a subject of 1 to 256 characters with no control character.")]
    [InlineData("A6 control",
        "Hub:Access:Groups:0:Members:0 must be a subject of 1 to 256 characters with no control character.")]
    [InlineData("A6 machine",
        "Hub:Access:Admins:0: a machine token is never a person; it gets its group through Hub:Access:MachineTokens.")]
    [InlineData("A6 local", "Hub:Access:Groups:0:Members:0: 'local:ops' names no account in Hub:Access:LocalAccounts.")]
    [InlineData("A6 local case", "Hub:Access:Admins:0: 'Local:ops' names no account in Hub:Access:LocalAccounts.")]
    [InlineData("A7 name",
        "Hub:Access:Groups:0:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', unique with case ignored.")]
    [InlineData("A7 duplicate",
        "Hub:Access:Groups:2:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', unique with case ignored.")]
    [InlineData("A8 undeclared", "Hub:Access:Grants:0:Group: 'internal:nope' names no group in Hub:Access:Groups.")]
    [InlineData("A8 case", "Hub:Access:Grants:0:Group: write the internal: prefix in lower case.")]
    [InlineData("A8 star",
        "Hub:Access:AdminGroups:0 must be internal:<name> or a provider group of 1 to 256 characters with no control character, '*' excepted.")]
    [InlineData("A8 control",
        "Hub:Access:Grants:0:Group must be internal:<name> or a provider group of 1 to 256 characters with no control character, '*' excepted.")]
    [InlineData("A8 machine provider",
        "Hub:Access:MachineTokens:0:Group must name an internal group: a machine token gets no provider group.")]
    [InlineData("A9 name",
        "Hub:Access:Projects:0:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', unique with case ignored.")]
    [InlineData("A9 duplicate",
        "Hub:Access:Projects:2:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', unique with case ignored.")]
    [InlineData("A9 no pattern", "Hub:Access:Projects:0 needs at least one pattern.")]
    [InlineData("A9 empty pattern", "Hub:Access:Projects:0:Patterns:0 must set Source, Service or Attribute.")]
    [InlineData("A9 source star inside",
        "Hub:Access:Projects:0:Patterns:0:Source must be a source id, or a prefix of one ending in a single '*'.")]
    [InlineData("A9 source character",
        "Hub:Access:Projects:0:Patterns:0:Source must be a source id, or a prefix of one ending in a single '*'.")]
    [InlineData("A9 unknown source", "Hub:Access:Projects:0:Patterns:0:Source 'nope' matches no configured source.")]
    [InlineData("A9 prefix of no source",
        "Hub:Access:Projects:0:Patterns:0:Source 'zz*' matches no configured source.")]
    [InlineData("A9 service control",
        "Hub:Access:Projects:0:Patterns:0:Service must be 1 to 256 characters with no control character, exact or a prefix ending in a single '*'.")]
    [InlineData("A9 service long",
        "Hub:Access:Projects:0:Patterns:0:Service must be 1 to 256 characters with no control character, exact or a prefix ending in a single '*'.")]
    [InlineData("A9 attribute without equals", AttributeShape)]
    [InlineData("A9 attribute without key", AttributeShape)]
    [InlineData("A9 attribute without value", AttributeShape)]
    [InlineData("A9 attribute key prefix", AttributeShape)]
    [InlineData("A9 attribute value star inside", AttributeShape)]
    [InlineData("A10 built-in name", RoleName)]
    [InlineData("A10 duplicate", "Hub:Access:Roles:1:Name must be 1 to 64 ASCII letters, digits, '.', '_' or '-', "
                                 + "unique with case ignored, and not reader, member or admin.")]
    [InlineData("A10 no permission", RolePermissions)]
    [InlineData("A10 unknown permission", RolePermissions)]
    [InlineData("A10 repeated permission", RolePermissions)]
    [InlineData("A10 access.admin",
        "Role 'auditor': access.admin is held only through the built-in admin role, Hub:Access:Admins or Hub:Access:AdminGroups.")]
    [InlineData("A11 blank", "Hub:Access:Grants:0 needs Group, Project and Role.")]
    [InlineData("A11 project",
        "Hub:Access:Grants:0:Project 'nope' is neither '*' nor a project in Hub:Access:Projects.")]
    [InlineData("A11 project case",
        "Hub:Access:Grants:0:Project 'Shop' is neither '*' nor a project in Hub:Access:Projects.")]
    [InlineData("A11 role", "Hub:Access:Grants:0:Role 'nope' is neither a built-in role nor one in Hub:Access:Roles.")]
    [InlineData("A11 admin on a project",
        "Grant 0: the admin role applies to the whole Hub; grant it on '*', or use Hub:Access:Admins or Hub:Access:AdminGroups.")]
    [InlineData("A12 name", TokenName)]
    [InlineData("A12 duplicate name",
        "Hub:Access:MachineTokens:1:Name must match ^[a-z0-9][a-z0-9._-]{0,63}$ and be unique.")]
    [InlineData("A12 group with members",
        "Group 'oncall' backs machine token 'ci-shop' and must have no members: its grants are sized for a machine.")]
    [InlineData("A12 no token", TokenShape)]
    [InlineData("A12 short token", TokenShape)]
    [InlineData("A12 token alphabet", TokenShape)]
    [InlineData("A12 terminal prefix", TokenShape)]
    [InlineData("A12 token twice",
        "Hub:Access:MachineTokens:1:Token must differ from every other machine token and every import API key.")]
    [InlineData("A12 import key",
        "Hub:Access:MachineTokens:0:Token must differ from every other machine token and every import API key.")]
    [InlineData("A12 no expiry", TokenExpiry)]
    [InlineData("A12 beyond a year", TokenExpiry)]
    [InlineData("A13 name", LocalName)]
    [InlineData("A13 duplicate",
        "Hub:Access:LocalAccounts:1:Name must match ^[a-z0-9][a-z0-9._-]{0,63}$ and be unique.")]
    [InlineData("A13 no hash", LocalHash)]
    [InlineData("A13 iterations", LocalHash)]
    [InlineData("A13 unlisted",
        "Local account 'ops' is listed by no internal group and not in Hub:Access:Admins, so it could sign in and reach nothing.")]
    [InlineData("A13 no proxy",
        "Hub:Access:LocalAccounts needs Hub:Auth:TrustedProxyNetworks: without it every client behind a proxy shares one address, and anyone could keep the break-glass account throttled. A Hub with no proxy in front lists 127.0.0.1/32.")]
    public void Each_access_rule_names_the_key_it_refuses(string rule, string message)
    {
        var failures = AccessFailures(AccessCase(rule));

        Assert.Contains(message, failures);
    }

    [Fact]
    public void A_local_account_listed_by_a_group_or_as_an_admin_is_accepted()
    {
        Assert.Empty(AccessFailures(LocalOn()));
        Assert.Empty(AccessFailures(LocalOn(access => access with
        {
            Admins = [.. access.Admins, "local:ops"],
            Groups = [access.Groups[0] with { Members = [] }, .. access.Groups.Skip(1)]
        })));
    }

    [Fact]
    public void A_local_account_error_never_echoes_its_hash()
    {
        var hash = LocalPasswordHash.Replace("i=600000", "i=1", StringComparison.Ordinal);

        var failures = AccessFailures(LocalOn(access => access with
        {
            LocalAccounts = [access.LocalAccounts[0] with { PasswordHash = hash }]
        }));

        Assert.Equal([LocalHash], failures);
        Assert.DoesNotContain(failures, failure => failure.Contains("AAECAwQF", StringComparison.Ordinal));
    }

    [Fact]
    public void An_access_error_never_echoes_a_machine_token()
    {
        var options = AccessOn(access => access with
        {
            MachineTokens =
            [
                access.MachineTokens[0] with { Token = MachineTokenValue + "!" },
                access.MachineTokens[0] with { Name = "ci-other", Token = MachineTokenValue + "!" }
            ]
        });

        var failures = AccessFailures(options);
        Assert.True(failures.Length > 1);
        Assert.DoesNotContain(failures, failure => failure.Contains(MachineTokenValue, StringComparison.Ordinal));
    }

    [Fact]
    public void A_machine_token_lives_at_most_a_year_and_an_expired_one_does_not_stop_the_hub()
    {
        // An expired entry is refused at each request, so an old one never keeps the Hub down.
        DateTimeOffset[] accepted = [Now.AddDays(365), Now.AddDays(-1)];
        foreach (var expiresAt in accepted)
            Assert.Empty(AccessFailures(WithExpiry(expiresAt)));

        Assert.Contains(TokenExpiry, AccessFailures(WithExpiry(Now.AddDays(365).AddSeconds(1))));

        // The validator takes its clock as a parameter, so the limit moves with it.
        var later = new FakeTimeProvider(Now.AddDays(10));
        Assert.Empty(new HubOptionsValidator(later).Validate(null, WithExpiry(Now.AddDays(370))).Failures ?? []);

        static HubOptions WithExpiry(DateTimeOffset expiresAt)
        {
            return AccessOn(access => access with
            {
                MachineTokens = [access.MachineTokens[0] with { ExpiresAt = expiresAt }]
            });
        }
    }

    [Fact]
    public void Access_binds_from_the_chart_s_camel_case_values()
    {
        // The chart passes hub.access as is: camelCase keys, and the Secret's name and key,
        // which name a Secret, hold no secret and are ignored by the binder.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {"Hub": {"Access": {"enabled": false, "admins": [], "adminGroups": ["/hub-admins"],
              "groups": [{"name": "ci-a", "members": []}],
              "projects": [{"name": "shop", "patterns": [{"attribute": "team=shop"}, {"source": "prod", "service": "cart*"}]}],
              "roles": [{"name": "auditor", "permissions": ["findings.read", "traces.read"]}],
              "grants": [{"group": "internal:ci-a", "project": "shop", "role": "auditor"}],
              "machineTokens": [{"name": "ci-shop", "group": "internal:ci-a", "expiresAt": "2027-06-30T00:00:00Z",
                                 "secretName": "hub-machine-tokens", "secretKey": "ci-shop"}],
              "localAccounts": [{"name": "ops", "secretName": "hub-break-glass", "secretKey": "ops"}]}}}
            """));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Access:MachineTokens:0:Token"] = MachineTokenValue + "\n",
                ["Hub:Access:LocalAccounts:0:PasswordHash"] = LocalPasswordHash + "\n"
            })
            .Build();

        var access = configuration.GetSection("Hub:Access").Get<AccessOptions>();

        Assert.NotNull(access);
        Assert.Empty(access.Admins);
        Assert.Equal(["/hub-admins"], access.AdminGroups);
        Assert.Equal("ci-a", Assert.Single(access.Groups).Name);
        Assert.Equal("team=shop", access.Projects[0].Patterns[0].Attribute);
        Assert.Equal("cart*", access.Projects[0].Patterns[1].Service);
        Assert.Equal(["findings.read", "traces.read"], Assert.Single(access.Roles).Permissions);
        Assert.Equal("auditor", Assert.Single(access.Grants).Role);
        var token = Assert.Single(access.MachineTokens);
        Assert.Equal(new DateTimeOffset(2027, 6, 30, 0, 0, 0, TimeSpan.Zero), token.ExpiresAt);
        Assert.Equal(MachineTokenValue, token.Token);
        var local = Assert.Single(access.LocalAccounts);
        Assert.Equal("ops", local.Name);
        Assert.Equal(LocalPasswordHash, local.PasswordHash);
    }

    // No shipped example may teach a bare top-level name: on GitLab anyone can create one.
    [Fact]
    public void Every_provider_group_in_the_reference_configuration_is_a_path()
    {
        var access = ReferenceConfigurationTests.Reference().GetProperty("Access");
        string[] references =
        [
            .. access.GetProperty("Grants").EnumerateArray().Select(grant => grant.GetProperty("Group").GetString()!),
            .. access.GetProperty("AdminGroups").EnumerateArray().Select(group => group.GetString()!)
        ];

        Assert.Contains(references, reference => !reference.StartsWith("internal:", StringComparison.Ordinal));
        Assert.All(
            references.Where(reference => !reference.StartsWith("internal:", StringComparison.Ordinal)),
            reference => Assert.Contains('/', reference));
    }

    private static HubOptions AccessCase(string rule)
    {
        return rule switch
        {
            "A1" => AccessOn() with { Auth = new AuthOptions() },
            "A2" => AccessOn() with { AckRelay = new AckRelayOptions { TrustIdentityHeader = true } },
            "A4" => AccessOn() with { Auth = ValidAuth() with { GroupsClaim = " " } },
            "A5" => AccessOn(access => access with { Admins = [], AdminGroups = [] }),
            "A6 blank" => AccessOn(access => access with { Admins = [" "] }),
            "A6 long" => AccessOn(access => access with { Admins = [new string('s', 257)] }),
            "A6 control" => Members("7c1e\t9a02"),
            "A6 machine" => AccessOn(access => access with { Admins = ["Machine:ci-shop"] }),
            "A6 local" => Members("local:ops"),
            "A7 name" => AccessOn(access => access with
            {
                Groups = [access.Groups[0] with { Name = "on call" }, .. access.Groups.Skip(1)]
            }),
            "A7 duplicate" => AccessOn(access => access with
            {
                Groups = [.. access.Groups, new InternalGroupOptions { Name = "OnCall" }]
            }),
            "A8 undeclared" => FirstGrant(grant => grant with { Group = "internal:nope" }),
            "A8 case" => FirstGrant(grant => grant with { Group = "Internal:oncall" }),
            "A8 star" => AccessOn(access => access with { AdminGroups = ["*"] }),
            "A8 control" => FirstGrant(grant => grant with { Group = "/teams/\nshop" }),
            "A8 machine provider" => FirstToken(token => token with { Group = "/teams/ci" }),
            "A9 name" => FirstProject(project => project with { Name = "shop!" }),
            "A9 duplicate" => AccessOn(access => access with
            {
                Projects = [.. access.Projects, access.Projects[0] with { Name = "Shop" }]
            }),
            "A9 no pattern" => FirstProject(project => project with { Patterns = [] }),
            "A9 empty pattern" => FirstPattern(new PatternOptions()),
            "A9 source star inside" => FirstPattern(new PatternOptions { Source = "pr*d" }),
            "A9 source character" => FirstPattern(new PatternOptions { Source = "prod/eu" }),
            "A9 unknown source" => FirstPattern(new PatternOptions { Source = "nope" }),
            "A9 prefix of no source" => FirstPattern(new PatternOptions { Source = "zz*" }),
            "A9 service control" => FirstPattern(new PatternOptions { Service = "cart\u0007" }),
            "A9 service long" => FirstPattern(new PatternOptions { Service = new string('s', 257) }),
            "A9 attribute without equals" => FirstPattern(new PatternOptions { Attribute = "team" }),
            "A9 attribute without key" => FirstPattern(new PatternOptions { Attribute = "=shop" }),
            "A9 attribute without value" => FirstPattern(new PatternOptions { Attribute = "team=" }),
            "A9 attribute key prefix" => FirstPattern(new PatternOptions { Attribute = "te*=shop" }),
            "A9 attribute value star inside" => FirstPattern(new PatternOptions { Attribute = "team=sh*p" }),
            "A10 built-in name" => FirstRole(role => role with { Name = "Reader" }),
            "A10 duplicate" => AccessOn(access => access with
            {
                Roles = [.. access.Roles, access.Roles[0] with { Name = "Auditor" }]
            }),
            "A10 no permission" => FirstRole(role => role with { Permissions = [] }),
            "A10 unknown permission" => FirstRole(role => role with { Permissions = ["findings.write"] }),
            "A10 repeated permission" => FirstRole(role =>
                role with { Permissions = ["findings.read", "findings.read"] }),
            "A10 access.admin" => FirstRole(role => role with { Permissions = ["findings.read", "access.admin"] }),
            "A11 blank" => FirstGrant(grant => grant with { Role = "" }),
            "A11 project" => FirstGrant(grant => grant with { Project = "nope" }),
            "A11 project case" => FirstGrant(grant => grant with { Project = "Shop" }),
            "A11 role" => FirstGrant(grant => grant with { Role = "nope" }),
            "A11 admin on a project" => FirstGrant(grant => grant with { Role = "admin" }),
            "A12 name" => FirstToken(token => token with { Name = "CI" }),
            "A12 duplicate name" => AccessOn(access => access with
            {
                MachineTokens =
                [.. access.MachineTokens, access.MachineTokens[0] with { Token = MachineTokenValue + "2" }]
            }),
            "A12 group with members" => FirstToken(token => token with { Group = "internal:oncall" }),
            "A12 no token" => FirstToken(token => token with { Token = null }),
            "A12 short token" => FirstToken(token => token with { Token = new string('t', 31) }),
            "A12 token alphabet" => FirstToken(token => token with { Token = MachineTokenValue + "!" }),
            "A12 terminal prefix" => FirstToken(token => token with { Token = "pshub_" + MachineTokenValue }),
            "A12 token twice" => AccessOn(access => access with
            {
                MachineTokens = [.. access.MachineTokens, access.MachineTokens[0] with { Name = "ci-other" }]
            }),
            "A12 import key" => AccessOn() with
            {
                Sources = [ValidSource() with { ImportApiKey = MachineTokenValue }]
            },
            "A12 no expiry" => FirstToken(token => token with { ExpiresAt = null }),
            "A12 beyond a year" => FirstToken(token => token with { ExpiresAt = Now.AddDays(366) }),
            "A6 local case" => LocalOn(access => access with { Admins = ["Local:ops"] }),
            "A13 name" => LocalOn(access => Local(access, local => local with { Name = "Ops" }, "local:Ops")),
            "A13 duplicate" => LocalOn(access =>
                access with { LocalAccounts = [access.LocalAccounts[0], access.LocalAccounts[0]] }),
            "A13 no hash" => LocalOn(access => Local(access, local => local with { PasswordHash = null })),
            "A13 iterations" => LocalOn(access => Local(access, local => local with
            {
                PasswordHash = LocalPasswordHash.Replace("600000", "600001", StringComparison.Ordinal)
            })),
            "A13 unlisted" => LocalOn(access => access with
            {
                Groups = [access.Groups[0] with { Members = [] }, .. access.Groups.Skip(1)]
            }),
            "A13 no proxy" => LocalOn() with { Auth = ValidAuth() },
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null)
        };

        static AccessOptions Local(AccessOptions access, Func<LocalAccountOptions, LocalAccountOptions> change,
            string? member = null)
        {
            return access with
            {
                LocalAccounts = [change(access.LocalAccounts[0])],
                Groups = member is null
                    ? access.Groups
                    : [access.Groups[0] with { Members = [member] }, .. access.Groups.Skip(1)]
            };
        }

        static HubOptions Members(string member)
        {
            return AccessOn(access => access with
            {
                Groups = [access.Groups[0] with { Members = [member] }, .. access.Groups.Skip(1)]
            });
        }

        static HubOptions FirstGrant(Func<GrantOptions, GrantOptions> change)
        {
            return AccessOn(access => access with { Grants = [change(access.Grants[0]), .. access.Grants.Skip(1)] });
        }

        static HubOptions FirstToken(Func<MachineTokenOptions, MachineTokenOptions> change)
        {
            return AccessOn(access => access with { MachineTokens = [change(access.MachineTokens[0])] });
        }

        static HubOptions FirstProject(Func<ProjectOptions, ProjectOptions> change)
        {
            return AccessOn(access => access with
            {
                Projects = [change(access.Projects[0]), .. access.Projects.Skip(1)]
            });
        }

        static HubOptions FirstPattern(PatternOptions pattern)
        {
            return FirstProject(project => project with { Patterns = [pattern] });
        }

        static HubOptions FirstRole(Func<RoleOptions, RoleOptions> change)
        {
            return AccessOn(access => access with { Roles = [change(access.Roles[0]), .. access.Roles.Skip(1)] });
        }
    }

    // Every kind of entry, each valid.
    private static HubOptions AccessOn(Func<AccessOptions, AccessOptions>? change = null)
    {
        var access = new AccessOptions
        {
            Enabled = true,
            Admins = ["2f6d0c1e-8a4b-4e2f-9c71-0d3b5a6e7f81"],
            AdminGroups = ["/hub-admins", "internal:oncall"],
            Groups =
            [
                new InternalGroupOptions { Name = "oncall", Members = ["7c1e9a02-55d3-4b8e-a1f0-3e2d4c5b6a79"] },
                new InternalGroupOptions { Name = "ci-a" }
            ],
            Projects =
            [
                new ProjectOptions { Name = "shop", Patterns = [new PatternOptions { Attribute = "team=shop=eu" }] },
                new ProjectOptions
                {
                    Name = "prod-cart",
                    Patterns =
                    [
                        new PatternOptions { Source = "prod", Service = "cart*" },
                        new PatternOptions { Source = "pr*" }
                    ]
                }
            ],
            Roles = [new RoleOptions { Name = "auditor", Permissions = ["findings.read", "traces.read"] }],
            Grants =
            [
                new GrantOptions { Group = "/teams/shop", Project = "shop", Role = "member" },
                new GrantOptions { Group = "internal:oncall", Project = "*", Role = "member" },
                new GrantOptions { Group = "internal:ci-a", Project = "shop", Role = "auditor" },
                new GrantOptions { Group = "internal:ci-a", Project = "shop", Role = "auditor" }
            ],
            MachineTokens =
            [
                new MachineTokenOptions
                {
                    Name = "ci-shop",
                    Group = "internal:ci-a",
                    Token = MachineTokenValue,
                    ExpiresAt = Now.AddDays(30)
                }
            ]
        };
        return ValidOptions() with
        {
            Auth = ValidAuth(),
            MetricsPort = 9464,
            Access = change?.Invoke(access) ?? access
        };
    }

    // A local account ops in the oncall group, behind a known proxy.
    private static HubOptions LocalOn(Func<AccessOptions, AccessOptions>? change = null)
    {
        var options = AccessOn(access => access with
        {
            Groups =
            [
                access.Groups[0] with { Members = [.. access.Groups[0].Members, "local:ops"] }, .. access.Groups.Skip(1)
            ],
            LocalAccounts = [new LocalAccountOptions { Name = "ops", PasswordHash = LocalPasswordHash }]
        });
        options = options with
        {
            Auth = options.Auth with { TrustedProxyNetworks = ["127.0.0.1/32"] },
            Access = change?.Invoke(options.Access) ?? options.Access
        };
        return options;
    }

    private static string[] AccessFailures(HubOptions options)
    {
        return [.. new HubOptionsValidator(new FakeTimeProvider(Now)).Validate(null, options).Failures ?? []];
    }

    internal static HubOptions ValidOptions()
    {
        return new HubOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "hub.db"),
            Analysis = new AnalysisOptions { ReportDirectory = ReportDirectoryFixture.ReportDirectory },
            Sources = [ValidSource()]
        };
    }

    private static SourceOptions ValidSource()
    {
        return new SourceOptions
        {
            Id = "prod",
            Name = "Production",
            Environment = "prod",
            BaseUrl = new Uri("https://daemon.example")
        };
    }

    private static SourceOptions AckSource()
    {
        return ValidSource() with { AckHeaderName = "X-API-Key", AckHeaderValue = AckSecret };
    }

    private static string[] Failures(SourceOptions source)
    {
        return [.. new HubOptionsValidator().Validate(null, ValidOptions() with { Sources = [source] }).Failures ?? []];
    }
}
