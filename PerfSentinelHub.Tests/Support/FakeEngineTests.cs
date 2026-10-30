using System.Collections.Concurrent;
using System.Diagnostics;

namespace PerfSentinelHub.Tests.Support;

/// <summary>
///     The fake engine many suites start, written while other tests start processes of their own.
/// </summary>
public sealed class FakeEngineTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-fake-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Fact]
    public async Task An_engine_written_while_other_processes_start_always_starts()
    {
        // Linux fails an exec with ETXTBSY while any process still holds the file open for
        // writing, a child forked mid-write included: a few in a thousand did on CI.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux forks with the descriptor still open.");
        var failures = new ConcurrentBag<string>();

        await Parallel.ForAsync(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, cancellationToken) =>
            {
                var path = new FakeEngine().WriteTo(Path.Combine(_workspace, $"{i}", "perf-sentinel"));
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(path, "--version")
                    {
                        RedirectStandardOutput = true
                    })!;
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    failures.Add(exception.Message);
                }
            });

        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(true, "keygen", 0)]
    [InlineData(true, "render", 0)]
    [InlineData(false, "keygen", 2)]
    [InlineData(false, "render", 2)]
    public async Task Card_help_exits_as_clap_does_for_a_known_or_unknown_subcommand(
        bool card,
        string subcommand,
        int expected)
    {
        var path = new FakeEngine { Card = card }.WriteTo(Path.Combine(_workspace, "perf-sentinel"));
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            ArgumentList = { "card", subcommand, "--help" },
            RedirectStandardOutput = true,
            WorkingDirectory = _workspace
        })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, process.ExitCode);
    }
}
