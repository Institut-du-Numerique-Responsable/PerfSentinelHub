using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Access;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Api;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Maintenance;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

// Each subcommand stays a block of its own, handled before the host is built.
// ReSharper disable once ConvertIfStatementToSwitchStatement
if (args is ["hash-password", ..])
    // Read from stdin or typed at a prompt, never from argv: an argument stays in the shell
    // history and the process list.
    return HashPasswordCommand.Run(
        args,
        Console.In,
        Console.IsInputRedirected ? null : () => Console.ReadKey(true),
        Console.Out,
        Console.Error);

if (args is ["backup", ..])
{
    // Any wrong arity must refuse loudly: the fallthrough would silently
    // discard the bare tokens and boot the full server instead.
    if (args is not ["backup", var backupDestination])
    {
        await Console.Error.WriteLineAsync("Usage: PerfSentinelHub backup <destination>");
        return 2;
    }

    // Reads the same appsettings + environment sources as the server, so the
    // configured Hub:DatabasePath applies without booting listeners or workers.
    var backupConfiguration = WebApplication.CreateSlimBuilder().Configuration;
    return await HubBackup.RunAsync(
        backupConfiguration[$"{HubOptions.SectionName}:{nameof(HubOptions.DatabasePath)}"]
        ?? new HubOptions().DatabasePath,
        backupDestination);
}

var builder = WebApplication.CreateSlimBuilder(args);

// A share-link token sits in the request path, which the hosting layer logs at Information.
// Above Warning it never reaches the Hub's logs, wherever the Hub is deployed.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Hub:Disclosures:ImportKey"]))
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HubJsonContext.Default);
    // Matches the envelope perf-sentinel itself emits, so one contract reads
    // the same on both sides. Every pre-existing field is a single word and
    // serialises identically under either policy.
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IValidateOptions<HubOptions>, HubOptionsValidator>());
builder.Services.AddOptions<HubOptions>()
    .BindConfiguration(HubOptions.SectionName)
    .ValidateOnStart();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HubDatabase>();
builder.Services.AddSingleton<RecapService>();
// Always registered, disabled with access off: registered only with it on, a handler taking it
// would have the Request Delegate Generator read it as a JSON body, which a GET refuses.
builder.Services.AddSingleton<AccessPolicy>();
// Always registered for the same reason; its routes are mapped with access on only.
builder.Services.AddSingleton<DeviceAuthorizations>();
builder.Services.AddSingleton<LocalSignInThrottle>();
builder.Services.AddSingleton<LocalSignInGate>();
builder.Services.AddSingleton<EngineProbe>();
// Registered here, on builder.Services, so it starts before the listener does:
// Build() adds the web host's own hosted service after every registration made
// on the builder, and hosted services start one after another. The probe has
// therefore answered before the first request arrives, which is what lets a run
// trust SupportsDaemonUrl instead of racing it. Moving this after Build(), or
// into the web host, would silently hand the first runs a static report.
builder.Services.AddHostedService(provider => provider.GetRequiredService<EngineProbe>());
builder.Services.AddSingleton<ImportGate>();
builder.Services.AddSingleton<ImportMetrics>();
builder.Services.AddSingleton<FindingMetrics>();
builder.Services.AddSingleton<ImportAdmission<ImportMetrics>>();
builder.Services.AddSingleton<IncidentImportMetrics>();
builder.Services.AddSingleton<ImportAdmission<IncidentImportMetrics>>();
builder.Services.AddSingleton<CorrelationImportMetrics>();
builder.Services.AddSingleton<ImportAdmission<CorrelationImportMetrics>>();
builder.Services.AddSingleton<DetectionImportMetrics>();
builder.Services.AddSingleton<ImportAdmission<DetectionImportMetrics>>();
builder.Services.AddSingleton<DisclosureImportGate>();
builder.Services.AddSingleton<ShareGate>();
builder.Services.AddSingleton<DisclosureMetrics>();
builder.Services.AddSingleton<DisclosureRenderer>();
builder.Services.AddSingleton<ShareDownloadCounter>();
builder.Services.AddHostedService<ShareDownloadFlusher>();
builder.Services.AddSingleton<DeliveryWorker>();
builder.Services.AddHostedService<DeliveryLoop>();
builder.Services.AddSingleton<RecapGenerator>();
builder.Services.AddSingleton<RecapMailer>();
builder.Services.AddSingleton<RecapDeliveryWorker>();
builder.Services.AddHostedService<RecapLoop>();
builder.Services.AddSingleton<CardKeys>();
builder.Services.AddSingleton<CardService>();
builder.Services.AddSingleton<CardRenderer>();
// Its own client: a webhook is a destination of its own, never followed elsewhere. No
// request logging: the factory would log each URL, and a chat webhook URL is a secret.
builder.Services.AddHttpClient(DeliveryWorker.ClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    .RemoveAllLoggers();
builder.Services.AddSingleton<DaemonViewGate>();
builder.Services.AddSingleton<IncidentRefreshGate>();
builder.Services.AddSingleton<AckRelayGate>();
builder.Services.AddSingleton<CardRenderGate>();
builder.Services.AddSingleton<AnalysisRunner>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHttpClient<DaemonClient>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = false,
    UseCookies = false
});
// Its own client, not the one the sources share: this destination is not a
// source, and keeping them apart is what lets the handler above stay described
// as "how the Hub talks to a daemon".
builder.Services.AddHttpClient(UpdateChecker.ClientName).ConfigurePrimaryHttpMessageHandler(() =>
    // Redirects off for the same reason as the sources above: a 302 would send
    // this request to a host that appears nowhere in the configuration.
    new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        UseCookies = false
    });
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<UpdateChecker>());
builder.Services.AddTransient<IncidentReader>();
builder.Services.AddTransient<AckReader>();
builder.Services.AddTransient<DetectionReader>();
builder.Services.AddSingleton<DetectionWatch>();
builder.Services.AddTransient<AckRelay>();
builder.Services.AddSingleton<RingWatch>();
builder.Services.AddTransient<SourcePoller>();
builder.Services.AddHostedService<PollWorker>();
builder.Services.AddSingleton<SignatureRate>();
builder.Services.AddHostedService<SignatureRateWorker>();
builder.Services.AddHostedService<RetentionWorker>();
var authenticates = builder.AddHubAuthentication();
// A report is several megabytes of repetitive HTML. Only the launcher's files and
// the reports are compressed: none carries a secret, while a JSON answer may hold
// a token next to reflected input, the pair BREACH reads over HTTPS.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ["text/html", "text/css", "text/javascript", "application/javascript", "image/svg+xml"];
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

var app = builder.Build();

await app.Services.GetRequiredService<HubDatabase>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

var hubOptions = app.Services.GetRequiredService<IOptions<HubOptions>>().Value;
HubAuthentication.WarnWhenAckRelayIdentifiesNobody(hubOptions, app.Services.GetRequiredService<ILogger<Program>>());
HubAuthentication.WarnOnExpiredMachineTokens(
    app.Services.GetRequiredService<AccessPolicy>(),
    app.Services.GetRequiredService<TimeProvider>(),
    app.Services.GetRequiredService<ILogger<Program>>());

// Before authentication: the metrics port answers /metrics alone, with or without Hub:Auth.
var exitCode = 0;
if (hubOptions.MetricsPort is { } metricsPort) app.UseMetricsPort(metricsPort, () => exitCode = 1);

// Before the static files: the fallback policy guards the launcher itself.
if (authenticates) app.UseHubAuthentication();

app.UseResponseCompression();

// The launcher and the reports it opens are served from the same origin: the
// theme handoff between them goes through sessionStorage, with no URL
// parameter and no postMessage.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Nothing here carries a fingerprint in its name, so a cached app.js can
    // outlive the API it talks to across a Hub upgrade. Without a Cache-Control
    // the browser picks its own freshness from the last-modified date and holds
    // the old file for minutes. no-cache still revalidates against the ETag, so
    // an unchanged file costs a 304 and not a download.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapHubApi();
app.MapAnalysisApi();
var accessPolicy = app.Services.GetRequiredService<AccessPolicy>();
if (accessPolicy.Enabled) app.MapTerminalAccess();
if (accessPolicy.HasLocalAccounts) app.MapLocalSignIn();
app.MapMetrics();
// Mapped with disclosures on only: off, /share/ stays whatever the Hub answered before.
if (hubOptions.Disclosures.Enabled) app.MapShare();

await app.RunAsync();
return exitCode;
