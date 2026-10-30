using System.Net;
using Microsoft.Extensions.Options;

namespace PerfSentinelHub.Configuration;

public sealed partial class HubOptionsValidator(TimeProvider? timeProvider = null) : IValidateOptions<HubOptions>
{
    private const string UrlShape = "without credentials, query, or fragment.";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ValidateOptionsResult Validate(string? name, HubOptions options)
    {
        var errors = new List<string>();
        ValidateHubSettings(options, errors);
        ValidateAccessSettings(options, _timeProvider.GetUtcNow(), errors);
        ValidateDisclosures(options, errors);
        ValidateRecaps(options, errors);
        ValidateCards(options, errors);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in options.Sources)
            ValidateSource(source, ids, errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateHubSettings(HubOptions options, List<string> errors)
    {
        if (!Path.IsPathFullyQualified(options.DatabasePath))
            errors.Add("Hub:DatabasePath must be absolute.");
        if (options.PollInterval <= TimeSpan.Zero)
            errors.Add("Hub:PollInterval must be positive.");
        if (options.HttpTimeout <= TimeSpan.Zero)
            errors.Add("Hub:HttpTimeout must be positive.");
        ValidateRetentionSettings(options, errors);
        if (options.MaxConcurrentPolls is < 1 or > 32)
            errors.Add("Hub:MaxConcurrentPolls must be between 1 and 32.");
        if (options.MaxReadLimit is < 1 or > 10_000)
            errors.Add("Hub:MaxReadLimit must be between 1 and 10000.");
        if (options.NewSignaturesPerHourWarning < 0)
            errors.Add("Hub:NewSignaturesPerHourWarning must be 0 or more, 0 turning the check off.");
        if (options.DefaultReadLimit < 1 || options.DefaultReadLimit > options.MaxReadLimit)
            errors.Add("Hub:DefaultReadLimit must be between 1 and MaxReadLimit.");
        if (options.Sources.Count == 0)
            errors.Add("Hub:Sources must contain at least one source.");
        // M1. Whether Kestrel listens there is known only once it has started (Program.cs).
        if (options.MetricsPort is < 1 or > 65535)
            errors.Add("Hub:MetricsPort must be between 1 and 65535.");
        if (!(options.Recaps.MovementThreshold > 0 && options.Recaps.MovementThreshold <= 10))
            errors.Add("Hub:Recaps:MovementThreshold must be above 0 and at most 10.");
        ValidateAnalysisSettings(options.Analysis, errors);
        ValidateUpdateCheckSettings(options.UpdateCheck, errors);
        ValidateAuthSettings(options.Auth, errors);
    }

    private static void ValidateRetentionSettings(HubOptions options, List<string> errors)
    {
        if (options.Retention <= TimeSpan.Zero)
            errors.Add("Hub:Retention must be positive.");
        // Below Retention it reads as Retention (RetentionWorker), so an older Hub keeping findings longer still starts.
        if (options.DetectionRetention <= TimeSpan.Zero)
            errors.Add("Hub:DetectionRetention must be positive.");
        if (options.ResolutionGrace <= TimeSpan.Zero || options.ResolutionGrace >= options.Retention)
            errors.Add("Hub:ResolutionGrace must be positive and below Hub:Retention.");
    }

    private static void ValidateAuthSettings(AuthOptions auth, List<string> errors)
    {
        // Refused rather than ignored: a gate the operator believes is up would
        // leave the Hub open to every reader of the port.
        if (auth is { Enabled: false, RequiredGroups.Count: > 0 })
            errors.Add("Hub:Auth:RequiredGroups requires Hub:Auth:Enabled: off, the Hub signs nobody in "
                       + "and checks no group.");
        if (!auth.Enabled) return;
        if (IsInvalidAuthEndpoint(auth.AuthorizationEndpoint) ||
            IsInvalidAuthEndpoint(auth.TokenEndpoint) ||
            IsInvalidAuthEndpoint(auth.UserInformationEndpoint))
            errors.Add("Hub:Auth endpoints must be absolute HTTPS URLs (HTTP on loopback only) "
                       + "without credentials or fragment.");
        if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret))
            errors.Add("Hub:Auth:ClientId and Hub:Auth:ClientSecret are required.");
        if (string.IsNullOrWhiteSpace(auth.IdentityClaim))
            errors.Add("Hub:Auth:IdentityClaim must name a userinfo field.");
        if (string.IsNullOrWhiteSpace(auth.DisplayNameClaim))
            errors.Add("Hub:Auth:DisplayNameClaim must name a userinfo field.");
        if (auth.RequiredGroups.Count > 0 && string.IsNullOrWhiteSpace(auth.GroupsClaim))
            errors.Add("Hub:Auth:GroupsClaim must name a userinfo field when Hub:Auth:RequiredGroups is set.");
        if (auth.RequiredGroups.Any(string.IsNullOrWhiteSpace))
            errors.Add("Hub:Auth:RequiredGroups must not hold a blank entry.");
        errors.AddRange(auth.TrustedProxyNetworks
            .Where(network => !IPNetwork.TryParse(network, out _))
            .Select(network => $"Hub:Auth:TrustedProxyNetworks entry '{network}' is not a CIDR network."));
    }

    private static bool IsInvalidAuthEndpoint(Uri? endpoint)
    {
        return endpoint is null ||
               !endpoint.IsAbsoluteUri ||
               !(endpoint.Scheme == Uri.UriSchemeHttps ||
                 (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
               !string.IsNullOrEmpty(endpoint.UserInfo) ||
               !string.IsNullOrEmpty(endpoint.Fragment);
    }

    private static void ValidateAnalysisSettings(AnalysisOptions analysis, List<string> errors)
    {
        ValidateAnalysisPaths(analysis, errors);
        if (IsInvalidHeaderName(analysis.IdentityHeader))
            errors.Add("Hub:Analysis:IdentityHeader must be a header name.");
        if (analysis.Workers is < 1 or > 16)
            errors.Add("Hub:Analysis:Workers must be between 1 and 16.");
        if (analysis.MaxTracesCap is < 1 or > AnalysisOptions.EngineMaxTraces)
            errors.Add($"Hub:Analysis:MaxTracesCap must be between 1 and {AnalysisOptions.EngineMaxTraces}, "
                       + "the engine's own limit on --max-traces.");
        if (analysis.MaxTracesEmbedded is < 0 or > 10_000)
            errors.Add("Hub:Analysis:MaxTracesEmbedded must be between 0 and 10000.");
        if (analysis.Timeout <= TimeSpan.Zero || analysis.Timeout > TimeSpan.FromHours(1))
            errors.Add("Hub:Analysis:Timeout must be positive and at most one hour.");
        if (analysis.MinFreeMemoryMiB is < 0 or > 65_536)
            errors.Add("Hub:Analysis:MinFreeMemoryMiB must be between 0 and 65536, 0 turning the check off.");
        if (analysis.ReportRetention <= TimeSpan.Zero)
            errors.Add("Hub:Analysis:ReportRetention must be positive.");
        if (analysis.RunRetention <= analysis.ReportRetention)
            errors.Add("Hub:Analysis:RunRetention must be longer than Hub:Analysis:ReportRetention, "
                       + "since an expired run keeps its parameters after its report is deleted.");
    }

    private static void ValidateAnalysisPaths(AnalysisOptions analysis, List<string> errors)
    {
        if (analysis.EngineBinaryPath is { } path &&
            (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            errors.Add("Hub:Analysis:EngineBinaryPath must be absolute.");
        if (!Path.IsPathFullyQualified(analysis.ReportDirectory))
            errors.Add("Hub:Analysis:ReportDirectory must be absolute.");
    }

    private static void ValidateUpdateCheckSettings(UpdateCheckOptions update, List<string> errors)
    {
        if (!update.Enabled) return;
        if (update.Interval < TimeSpan.FromMinutes(15))
            errors.Add("Hub:UpdateCheck:Interval must be at least 15 minutes.");
        // Held to a tighter shape than a source: https only, and no query, so
        // whoever reads the configuration sees the whole destination.
        if (IsInvalidUpdateEndpoint(update.EngineEndpoint))
            errors.Add("Hub:UpdateCheck:EngineEndpoint must be an absolute HTTPS URL "
                       + UrlShape);
        if (IsInvalidUpdateEndpoint(update.HubEndpoint))
            errors.Add("Hub:UpdateCheck:HubEndpoint must be an absolute HTTPS URL "
                       + UrlShape);
    }

    private static bool IsInvalidUpdateEndpoint(Uri? endpoint)
    {
        return endpoint is null ||
               !endpoint.IsAbsoluteUri ||
               endpoint.Scheme != Uri.UriSchemeHttps ||
               !string.IsNullOrEmpty(endpoint.UserInfo) ||
               !string.IsNullOrEmpty(endpoint.Query) ||
               !string.IsNullOrEmpty(endpoint.Fragment);
    }

    private static void ValidateSource(SourceOptions source, HashSet<string> ids, List<string> errors)
    {
        if (!IsValidName(source.Id) || !ids.Add(source.Id))
            errors.Add("Source IDs must be unique and contain 1-64 ASCII letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Environment))
            errors.Add($"Source '{source.Id}' requires a name and environment.");
        // An environment becomes a Prometheus label value and a member of the
        // closed set the read API matches a query parameter against.
        if (!string.IsNullOrWhiteSpace(source.Environment) && source.Environment.Any(char.IsControl))
            errors.Add($"Source '{source.Id}' environment must not contain control characters.");
        if (!SourceKinds.IsKnown(source.Kind))
            errors.Add($"Source '{source.Id}' kind must be 'daemon', 'tempo' or 'jaeger_query'.");
        if (source.Kind != SourceKinds.Daemon && source.ImportApiKey is not null)
            errors.Add($"Source '{source.Id}' is not a daemon and cannot carry an import API key.");
        ValidateRetentionHours(source, errors);
        ValidateBaseUrl(source, errors);
        ValidateHeaderPair(source.Id, "auth", source.AuthHeaderName, source.AuthHeaderValue, errors);
        ValidateAckHeader(source, errors);
        ValidatePublicAuthHeader(source, errors);
        if (source.ImportApiKey is { } importApiKey && IsInvalidImportApiKey(importApiKey))
            errors.Add($"Source '{source.Id}' import API key must contain at least 32 characters and no controls.");
        ValidateDbStat(source.Id, "pg_stat", source.PgStat, ["seconds", "milliseconds"],
            "seconds or milliseconds", errors);
        ValidateDbStat(source.Id, "mysql_stat", source.MySqlStat, ["seconds", "milliseconds", "picoseconds"],
            "seconds, milliseconds or picoseconds", errors);
    }

    // The engine checks the names and the selector grammar itself, and a render it
    // refuses is tried again without the tab. What it would never reach is checked here.
    private static void ValidateDbStat(
        string sourceId,
        string family,
        DbStatOptions? stats,
        string[] units,
        string unitsText,
        List<string> errors)
    {
        if (stats is null)
            return;
        if (stats.Prometheus is null)
            errors.Add($"Source '{sourceId}' {family} requires a Prometheus URL.");
        else if (IsInvalidSourceUrl(stats.Prometheus))
            errors.Add($"Source '{sourceId}' {family} Prometheus must be an absolute HTTP(S) URL " + UrlShape);
        if (stats.Unit is { } unit && !units.Contains(unit))
            errors.Add($"Source '{sourceId}' {family} unit must be {unitsText}.");
        if (stats.Top is < 1 or > 1000)
            errors.Add($"Source '{sourceId}' {family} top must be between 1 and 1000.");
        if (stats.Selector is { } selector && selector.Any(char.IsControl))
            errors.Add($"Source '{sourceId}' {family} selector must not contain control characters.");
        ValidateHeaderPair(sourceId, $"{family} auth", stats.AuthHeaderName, stats.AuthHeaderValue, errors);
    }

    private static void ValidateRetentionHours(SourceOptions source, List<string> errors)
    {
        if (source.RetentionHours is not { } retentionHours)
            return;

        // A daemon takes no window, so nothing would ever read the value. A
        // setting with no consumer is worse than a missing one: someone tunes
        // it and nothing happens.
        if (source.Kind == SourceKinds.Daemon)
            errors.Add($"Source '{source.Id}' is a daemon and takes no trace retention.");
        else if (retentionHours is < 1 or > 87_600)
            errors.Add($"Source '{source.Id}' retention must be between 1 hour and 10 years.");
    }

    private static void ValidateBaseUrl(SourceOptions source, List<string> errors)
    {
        if (IsInvalidSourceUrl(source.BaseUrl))
            errors.Add(
                $"Source '{source.Id}' requires an absolute HTTP(S) URL " +
                UrlShape);
        if (source.PublicUrl is not null && IsInvalidSourceUrl(source.PublicUrl))
            errors.Add(
                $"Source '{source.Id}' public URL must be an absolute HTTP(S) URL " +
                UrlShape);
    }

    private static bool IsInvalidSourceUrl(Uri? url)
    {
        return url is null ||
               !url.IsAbsoluteUri ||
               (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
               !string.IsNullOrEmpty(url.UserInfo) ||
               !string.IsNullOrEmpty(url.Query) ||
               !string.IsNullOrEmpty(url.Fragment);
    }

    // Both credentials a source can carry: "auth" reads the daemon, "ack" writes to it.
    private static void ValidateHeaderPair(
        string sourceId,
        string label,
        string? name,
        string? value,
        List<string> errors)
    {
        if (name is null && value is null)
            return;

        if (name is null || value is null)
        {
            errors.Add($"Source '{sourceId}' must provide both {label} header name and value.");
            return;
        }

        if (name.Contains('\r', StringComparison.Ordinal) ||
            name.Contains('\n', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            errors.Add($"Source '{sourceId}' {label} header contains a newline.");
            return;
        }

        using var request = new HttpRequestMessage();
        try
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                errors.Add($"Source '{sourceId}' {label} header is invalid.");
        }
        catch (FormatException)
        {
            errors.Add($"Source '{sourceId}' {label} header is invalid.");
        }
    }

    private static void ValidateAckHeader(SourceOptions source, List<string> errors)
    {
        ValidateHeaderPair(source.Id, "ack", source.AckHeaderName, source.AckHeaderValue, errors);
        // A trace backend has no ack route, so nothing would ever send the credential.
        if (source.Kind != SourceKinds.Daemon && (source.AckHeaderName ?? source.AckHeaderValue) is not null)
            errors.Add($"Source '{source.Id}' is not a daemon and cannot carry an ack credential.");
        // The daemon refuses a read key equal to its ack key: every reader could then write.
        if (source is { AckHeaderValue: { } ackHeaderValue, AuthHeaderValue: { } authHeaderValue } &&
            KeyOf(ackHeaderValue) == KeyOf(authHeaderValue))
            errors.Add($"Source '{source.Id}' ack credential must differ from its read credential.");
    }

    // The key a header value carries, as the daemon reads it: bare in X-API-Key, or
    // behind a Bearer scheme of any case in Authorization.
    private static string KeyOf(string headerValue)
    {
        return headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? headerValue[7..].TrimStart(' ')
            : headerValue;
    }

    private static void ValidatePublicAuthHeader(SourceOptions source, List<string> errors)
    {
        if (source.PublicAuthHeaderName is not { } name)
            return;

        if (source.PublicUrl is null)
            errors.Add($"Source '{source.Id}' public auth header name requires a public URL.");
        if (IsInvalidHeaderName(name))
            errors.Add($"Source '{source.Id}' public auth header name is invalid.");
    }

    private static bool IsInvalidImportApiKey(string value)
    {
        return value.Length < 32 || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl);
    }

    private static bool IsInvalidHeaderName(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Any(character => char.IsControl(character) || character == ' ');
    }

    // A source id, and an internal group, project or role name.
    private static bool IsValidName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               name.Length <= 64 &&
               name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}
