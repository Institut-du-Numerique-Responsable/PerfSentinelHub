using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Cards;

/// <summary>
///     Has the embedded engine render and sign a card (<c>card render</c>) and check a fresh card's
///     DNS attachment (<c>verify-card --online</c>), in a private directory deleted afterwards. The
///     signing key travels as a file copied there, never on the command line. Reads the exit code and
///     the result line only: the engine's stderr never reaches a response.
/// </summary>
public sealed class CardRenderer(EngineProbe engine, IOptions<HubOptions> options)
{
    /// <summary>The DNS-over-HTTPS service verify-card asks for the TXT record, the one host besides the domain.</summary>
    public const string DohHost = "cloudflare-dns.com";

    private const string DohUrl = "https://" + DohHost + "/dns-query";
    private const int MaxLineBytes = 4096;
    private const long MaxCardBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(20);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);

    private string? Binary => engine.SupportsCard ? options.Value.Analysis.EngineBinaryPath : null;

    public async Task<CardRender> RenderAsync(
        CardInput input,
        StoredCardKey key,
        string keyFile,
        string format,
        CancellationToken cancellationToken)
    {
        if (Binary is not { } binary)
            return new CardRender.Unsupported();

        var directory = Directory.CreateTempSubdirectory("perf-sentinel-hub-card-");
        try
        {
            var json = Path.Combine(directory.FullName, "card.json");
            await File.WriteAllBytesAsync(json,
                JsonSerializer.SerializeToUtf8Bytes(input, CardJsonContext.Default.CardInput), cancellationToken);
            var signingKey = Path.Combine(directory.FullName, "key.pk8");
            var output = Path.Combine(directory.FullName, "out");
            // A rotation or a revocation may delete the file between the read of the key and here.
            if (!TryPrepare(keyFile, signingKey, output))
                return new CardRender.Malformed();

            var result = await RunAsync(binary,
                ["card", "render", "--input", json, "--signing-key", signingKey, "--out-dir", output, "--format", format],
                Timeout, cancellationToken);
            return result is null
                ? new CardRender.Timeout()
                : await OutcomeAsync(result, Path.Combine(output, $"card.{format}"), key, cancellationToken);
        }
        catch (EngineOutputTooLargeException)
        {
            return new CardRender.Malformed();
        }
        finally
        {
            Delete(directory);
        }
    }

    /// <summary>verify-card's verdict, read whatever its exit code; null when its line lacks one of the five keys.</summary>
    public async Task<CardAttachmentResponse?> VerifyAsync(byte[] png, CancellationToken cancellationToken)
    {
        if (Binary is not { } binary)
            return null;

        var directory = Directory.CreateTempSubdirectory("perf-sentinel-hub-card-");
        try
        {
            var card = Path.Combine(directory.FullName, "card.png");
            await File.WriteAllBytesAsync(card, png, cancellationToken);
            var result = await RunAsync(binary, ["verify-card", card, "--online", "--doh", DohUrl], VerifyTimeout,
                cancellationToken);
            return result is null ? null : Verdict(result.StandardOutput);
        }
        catch (EngineOutputTooLargeException)
        {
            return null;
        }
        finally
        {
            Delete(directory);
        }
    }

    // The key copy readable by its owner only, and the output directory the engine does not create.
    private static bool TryPrepare(string keyFile, string signingKey, string output)
    {
        try
        {
            File.Copy(keyFile, signingKey);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(output);
            return true;
        }

        File.SetUnixFileMode(signingKey, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return true;
    }

    // Null past the deadline.
    private async Task<EngineResult?> RunAsync(
        string binary,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await EngineProcess.RunAsync(binary, arguments, MaxLineBytes, options.Value.Analysis.ReportDirectory,
                deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // Exit 0 needs the file and a line signed by the key the Hub handed over; any other exit needs its reason.
    private static async Task<CardRender> OutcomeAsync(
        EngineResult result,
        string file,
        StoredCardKey key,
        CancellationToken cancellationToken)
    {
        using var line = Line(result.StandardOutput);
        var root = line?.RootElement;
        if (result.ExitCode != 0)
            return Text(root, "reason") is { } reason
                ? new CardRender.Refused(reason, Text(root, "field"))
                : new CardRender.Malformed();

        return Text(root, "fingerprint") == key.Fingerprint &&
               Text(root, "compact_url") is { } compactUrl &&
               File.Exists(file) && new FileInfo(file).Length is > 0 and <= MaxCardBytes
            ? new CardRender.Rendered(await File.ReadAllBytesAsync(file, cancellationToken), compactUrl)
            : new CardRender.Malformed();
    }

    private static CardAttachmentResponse? Verdict(byte[] output)
    {
        using var line = Line(output);
        if (line?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            Text(root, "integrity") is not { } integrity ||
            !root.TryGetProperty("level", out var level) || level.ValueKind != JsonValueKind.Number ||
            !level.TryGetInt32(out var number) ||
            !TryNullableText(root, "domain", out var domain) ||
            Text(root, "key") is not { } key ||
            !TryNullableText(root, "reason", out var reason))
            return null;
        return new CardAttachmentResponse(integrity, number, domain, key, reason, []);
    }

    private static JsonDocument? Line(byte[] output)
    {
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement? root, string name)
    {
        return root is { ValueKind: JsonValueKind.Object } element &&
               element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    // Present, as a string or null.
    private static bool TryNullableText(JsonElement root, string name, out string? text)
    {
        text = null;
        if (!root.TryGetProperty(name, out var value))
            return false;
        text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Null;
    }

    private static void Delete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(true);
        }
        catch (IOException)
        {
            // A file still held open: the operating system's temporary cleanup takes it.
        }
    }
}
