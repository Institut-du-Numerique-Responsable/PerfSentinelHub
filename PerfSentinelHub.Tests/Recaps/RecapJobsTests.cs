using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Tests.Support;

namespace PerfSentinelHub.Tests.Recaps;

/// <summary>
///     The job registry on a fake clock, with a computation each test drives by hand: it enters the
///     stages it is told to and returns when the test lets it.
/// </summary>
public sealed class RecapJobsTests : IDisposable
{
    private static readonly RecapJobAudience Everyone = new(null, "a");
    private static readonly RecapJobAudience Alice = new("local:alice", "a");

    private static readonly RecapRequest Request = new(
        RecapPeriodKind.Week, new DateOnly(2026, 10, 29), null, null, null, null, [],
        new RecapFilters(null, null, null, null));

    private readonly FakeTimeProvider _clock =
        new(DateTimeOffset.Parse("2026-10-29T10:00:00Z", CultureInfo.InvariantCulture));

    private readonly List<Computation> _computations = [];
    private readonly ListLogger<RecapJobs> _logger = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly RecapJobs _jobs;

    public RecapJobsTests()
    {
        _jobs = new RecapJobs(ComputeAsync, _clock, _stopping.Token, _logger);
    }

    public void Dispose()
    {
        foreach (var computation in _computations)
            computation.Release.TrySetCanceled();
        _stopping.Dispose();
    }

    [Fact]
    public async Task A_job_reports_the_stage_it_is_at_and_the_share_of_the_work_behind_it()
    {
        var started = Start(Key()).Job!;
        var computation = await NextComputationAsync();

        Assert.Equal(RecapJobStatus.Running, started.Status);
        Assert.Equal(0, started.Progress);

        computation.Progress.Enter(RecapStage.FindingsPeriod);
        _clock.Advance(TimeSpan.FromSeconds(30));
        computation.Progress.Enter(RecapStage.FindingsOpen);
        computation.Progress.Skip(RecapStage.FindingsClosed);
        var running = Find(started.Id)!;

        Assert.Equal(RecapStage.FindingsOpen, running.Stage);
        Assert.Equal(RecapStageState.Done, StateOf(running, RecapStage.FindingsPeriod));
        Assert.Equal(30_000, running.Stages.Single(stage => stage.Stage == RecapStage.FindingsPeriod).Ms);
        Assert.Equal(RecapStageState.Running, StateOf(running, RecapStage.FindingsOpen));
        Assert.Equal(RecapStageState.Skipped, StateOf(running, RecapStage.FindingsClosed));
        Assert.Equal(RecapStageState.Pending, StateOf(running, RecapStage.Labels));
        // Two findings reads of the four weighted, the done one and the skipped one, out of the whole.
        Assert.Equal(40_000.0 / 95_000, running.Progress, 6);

        computation.Finish(Snapshot(inProgress: true));
        var done = await SettledAsync(started.Id);

        Assert.Equal(RecapJobStatus.Succeeded, done.Status);
        Assert.Equal(1, done.Progress);
        Assert.Null(done.Stage);
        Assert.NotNull(_jobs.Result(started.Id, Everyone));
    }

    [Fact]
    public async Task A_second_request_joins_the_running_job_rather_than_computing_again()
    {
        var first = Start(Key()).Job!;
        var second = Start(Key()).Job!;
        var joinedByRefresh = _jobs.Start(Key(), Request, true).Job!;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, joinedByRefresh.Id);
        await NextComputationAsync();
        Assert.Single(_computations);
    }

    [Fact]
    public async Task A_finished_recap_in_progress_is_reused_for_ten_minutes()
    {
        var first = Start(Key()).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: true));
        await SettledAsync(first.Id);

        _clock.Advance(RecapJobs.OpenLifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(first.Id, Start(Key()).Job!.Id);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(first.Id, Start(Key()).Job!.Id);
    }

    [Fact]
    public async Task A_finished_recap_of_a_closed_period_is_reused_for_a_day()
    {
        var first = Start(Key()).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: false));
        var done = await SettledAsync(first.Id);

        Assert.Equal(_clock.GetUtcNow().ToUnixTimeMilliseconds() + (long)RecapJobs.ClosedLifetime.TotalMilliseconds,
            done.ExpiresAtMs);
        _clock.Advance(RecapJobs.ClosedLifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(first.Id, Start(Key()).Job!.Id);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(first.Id, Start(Key()).Job!.Id);
    }

    [Fact]
    public async Task A_refresh_computes_again_over_a_finished_recap_and_forgets_it()
    {
        var first = Start(Key()).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: false));
        await SettledAsync(first.Id);

        var refreshed = _jobs.Start(Key(), Request, true).Job!;

        Assert.NotEqual(first.Id, refreshed.Id);
        Assert.Null(Find(first.Id));
    }

    [Fact]
    public async Task Past_the_running_cap_a_new_recap_is_refused_until_one_ends()
    {
        for (var index = 0; index < RecapJobs.MaxRunning; index++)
            Start(Key("service-" + index));
        await NextComputationAsync();

        var refused = Start(Key("another"));

        Assert.True(refused.Busy);
        Assert.Null(refused.Job);
        Assert.Contains(("busy", 1L), _jobs.Outcomes());

        _computations[0].Finish(Snapshot(inProgress: true));
        await WaitUntilAsync(() => _jobs.Sizes().Running < RecapJobs.MaxRunning);
        Assert.False(Start(Key("another")).Busy);
    }

    [Fact]
    public async Task Another_audience_reads_the_job_as_unknown()
    {
        var mine = _jobs.Start(Key(audience: Alice), Request, false).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: true));
        await WaitUntilAsync(() => _jobs.Result(mine.Id, Alice) is not null);

        Assert.Null(_jobs.Find(mine.Id, Everyone));
        Assert.Null(_jobs.Find(mine.Id, Alice with { Owner = "local:bob" }));
        Assert.Null(_jobs.Find(mine.Id, Alice with { Sources = "a\nb" }));
        Assert.Null(_jobs.Result(mine.Id, Everyone));
        Assert.False(_jobs.Cancel(mine.Id, Everyone));
        Assert.NotNull(_jobs.Find(mine.Id, Alice));
    }

    [Fact]
    public async Task A_cancelled_job_stops_its_computation_and_is_forgotten()
    {
        var job = Start(Key()).Job!;
        var computation = await NextComputationAsync();

        Assert.True(_jobs.Cancel(job.Id, Everyone));

        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
        Assert.Null(Find(job.Id));
        await WaitUntilAsync(() => _jobs.Outcomes().Contains(("cancelled", 1L)));
        Assert.False(_jobs.Cancel(job.Id, Everyone));
    }

    [Fact]
    public async Task A_stopping_Hub_cancels_what_runs()
    {
        var job = Start(Key()).Job!;
        var computation = await NextComputationAsync();

        await _stopping.CancelAsync();

        await WaitUntilAsync(() => Find(job.Id)?.Status == RecapJobStatus.Cancelled);
        Assert.True(computation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_failed_job_says_so_for_a_minute_then_goes()
    {
        var job = Start(Key()).Job!;
        (await NextComputationAsync()).Release.SetException(new InvalidOperationException("disk"));

        var failed = await SettledAsync(job.Id);

        Assert.Equal(RecapJobStatus.Failed, failed.Status);
        Assert.Equal("recap_failed", failed.ErrorCode);
        Assert.Null(_jobs.Result(job.Id, Everyone));
        Assert.Contains(_logger.Messages, message => message.Contains("failed", StringComparison.Ordinal));

        _clock.Advance(RecapJobs.EndedLifetime);
        Assert.Null(Find(job.Id));
        Assert.NotEqual(job.Id, Start(Key()).Job!.Id);
    }

    [Fact]
    public async Task Past_the_kept_cap_the_oldest_finished_recap_goes_first()
    {
        var ids = new List<string>();
        for (var index = 0; index <= RecapJobs.MaxKept; index++)
        {
            var job = Start(Key("service-" + index)).Job!;
            (await NextComputationAsync()).Finish(Snapshot(inProgress: false));
            await SettledAsync(job.Id);
            ids.Add(job.Id);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Start(Key("one-more"));

        Assert.Null(Find(ids[0]));
        Assert.Null(Find(ids[1]));
        Assert.NotNull(Find(ids[^1]));
        Assert.Equal(RecapJobs.MaxKept, _jobs.Sizes().Kept);
    }

    [Fact]
    public async Task A_finished_recap_logs_how_long_each_stage_took_and_teaches_the_weights()
    {
        var first = Start(Key()).Job!;
        var computation = await NextComputationAsync();
        computation.Progress.Enter(RecapStage.FindingsPeriod);
        _clock.Advance(TimeSpan.FromSeconds(90));
        computation.Progress.Enter(RecapStage.Labels);
        _clock.Advance(TimeSpan.FromSeconds(2));
        computation.Finish(Snapshot(inProgress: false));
        await SettledAsync(first.Id);

        Assert.Contains(_logger.Messages, message =>
            message.Contains("findings_period 90000 ms, labels 2000 ms", StringComparison.Ordinal));

        // The period read now weighs more than the default it started from.
        var second = _jobs.Start(Key("other"), Request, false).Job!;
        var next = await NextComputationAsync();
        next.Progress.Enter(RecapStage.FindingsPeriod);
        next.Progress.Enter(RecapStage.FindingsOpen);
        Assert.True(Find(second.Id, Key("other"))!.Progress > 20_000.0 / 95_000);
    }

    // ------------------------------------------------------------ helpers

    private static RecapJobKey Key(string? service = null, RecapJobAudience? audience = null)
    {
        return new RecapJobKey(audience ?? Everyone, RecapPeriodKind.Week, new DateOnly(2026, 10, 29), null, null,
            service, null);
    }

    private RecapJobStart Start(RecapJobKey key)
    {
        return _jobs.Start(key, Request, false);
    }

    private RecapJobView? Find(string id, RecapJobKey? key = null)
    {
        return _jobs.Find(id, (key ?? Key()).Audience);
    }

    private static RecapStageState StateOf(RecapJobView view, RecapStage stage)
    {
        return view.Stages.Single(item => item.Stage == stage).State;
    }

    private static RecapSnapshot Snapshot(bool inProgress)
    {
        var snapshot = RecapSnapshots.Distinctive();
        return snapshot with { Period = snapshot.Period with { InProgress = inProgress } };
    }

    private Task<RecapSnapshot> ComputeAsync(RecapRequest request, IRecapProgress progress, CancellationToken token)
    {
        var computation = new Computation(progress, token);
        token.Register(() => computation.Release.TrySetCanceled(token));
        lock (_computations)
        {
            _computations.Add(computation);
        }

        return computation.Release.Task;
    }

    private async Task<Computation> NextComputationAsync()
    {
        var seen = _computations.Count(computation => computation.Claimed);
        await WaitUntilAsync(() =>
        {
            lock (_computations)
            {
                return _computations.Count > seen;
            }
        });
        lock (_computations)
        {
            var next = _computations[seen];
            next.Claimed = true;
            return next;
        }
    }

    private async Task<RecapJobView> SettledAsync(string id)
    {
        RecapJobView? view = null;
        await WaitUntilAsync(() => (view = Find(id)) is { Status: not RecapJobStatus.Running });
        return view!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The recap job never reached the expected state.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class Computation(IRecapProgress progress, CancellationToken token)
    {
        public IRecapProgress Progress { get; } = progress;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<RecapSnapshot> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Claimed { get; set; }

        public void Finish(RecapSnapshot snapshot)
        {
            Release.SetResult(snapshot);
        }
    }
}
