using System.Globalization;

namespace PerfSentinelHub.Analysis;

/// <summary>
///     What the container's cgroup v2 can still give before its memory limit. The
///     engine runs as a subprocess in that same cgroup, so an analysis that does not
///     fit gets the whole Hub OOM-killed, every request in flight with it. Read from
///     `memory.max`, `memory.current` and `memory.stat` under the root given, the
///     kubelet's working set: the inactive page cache the kernel drops under pressure
///     counts as free.
/// </summary>
public sealed class CgroupMemory(string root)
{
    public static readonly CgroupMemory Container = new("/sys/fs/cgroup");

    /// <summary>Null without a limit, outside cgroup v2, or when a file cannot be read.</summary>
    public long? HeadroomBytes()
    {
        try
        {
            var max = File.ReadAllText(Path.Combine(root, "memory.max")).Trim();
            if (!long.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
                return null;
            var current = long.Parse(
                File.ReadAllText(Path.Combine(root, "memory.current")).Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            var inactiveFile = File.ReadLines(Path.Combine(root, "memory.stat"))
                .Where(line => line.StartsWith("inactive_file ", StringComparison.Ordinal))
                .Select(line => long.Parse(line["inactive_file ".Length..], NumberStyles.None,
                    CultureInfo.InvariantCulture))
                .FirstOrDefault();
            return limit - Math.Max(0, current - inactiveFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or FormatException or OverflowException)
        {
            return null;
        }
    }
}
