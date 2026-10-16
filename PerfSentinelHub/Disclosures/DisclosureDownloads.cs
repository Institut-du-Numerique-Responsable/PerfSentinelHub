using System.Security.Cryptography;
using System.Text;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Disclosures;

/// <summary>
///     The five names a disclosure's files are served under, on the restricted screen and on
///     a share link alike, and nothing else. SHA256SUMS is computed on the fly, in the format
///     sha256sum -c reads.
/// </summary>
public static class DisclosureDownloads
{
    public const string Sums = "SHA256SUMS";

    private static readonly (string Name, DisclosureFile File, string ContentType)[] Stored =
    [
        ("report.json", DisclosureFile.Report, "application/json"),
        ("attestation.intoto.jsonl", DisclosureFile.Attestation, "application/x-ndjson"),
        ("bundle.sig", DisclosureFile.Bundle, "application/octet-stream"),
        ("report.pdf", DisclosureFile.Pdf, "application/pdf")
    ];

    public static bool IsServed(string name)
    {
        return name == Sums || Array.Exists(Stored, entry => entry.Name == name);
    }

    /// <summary>The file and its content type, or null for a name not served or a file absent.</summary>
    public static async Task<(byte[] Bytes, string ContentType)?> ReadAsync(
        HubDatabase database,
        string id,
        string name,
        CancellationToken cancellationToken)
    {
        if (name == Sums)
        {
            var sums = new StringBuilder();
            foreach (var (fileName, file, _) in Stored)
                if (await database.ReadFileAsync(id, file, cancellationToken) is { } bytes)
                    sums.Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append("  ").Append(fileName)
                        .Append('\n');
            return sums.Length == 0 ? null : (Encoding.UTF8.GetBytes(sums.ToString()), "text/plain; charset=utf-8");
        }

        foreach (var (fileName, file, contentType) in Stored)
            if (fileName == name)
                return await database.ReadFileAsync(id, file, cancellationToken) is { } bytes
                    ? (bytes, contentType)
                    : null;
        return null;
    }
}
