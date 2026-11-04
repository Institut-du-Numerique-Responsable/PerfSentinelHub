using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Api;
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
    private long _version;

    public RecapJobsTests()
    {
        _jobs = new RecapJobs(ComputeAsync, () => _version, _clock, _stopping.Token, _logger);
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

        Assert.Equal("running", started.Status);
        Assert.Equal(0, started.Progress);

        computation.Progress.Enter(RecapStage.FindingsPeriod);
        _clock.Advance(TimeSpan.FromSeconds(30));
        computation.Progress.Enter(RecapStage.FindingsOpen);
        computation.Progress.Skip(RecapStage.FindingsClosed);
        var running = Find(started.Id)!;

        Assert.Equal("findings_open", running.Stage);
        Assert.Equal("done", StateOf(running, RecapStage.FindingsPeriod));
        Assert.Equal(30_000, running.Stages.Single(stage => stage.Name == "findings_period").Ms);
        Assert.Equal("running", StateOf(running, RecapStage.FindingsOpen));
        Assert.Equal("skipped", StateOf(running, RecapStage.FindingsClosed));
        Assert.Equal("pending", StateOf(running, RecapStage.Labels));
        // Two findings reads of the four weighted, the done one and the skipped one, out of the whole.
        Assert.Equal(40_000.0 / 95_000, running.Progress, 4);

        computation.Finish(Snapshot(inProgress: true));
        var done = await SettledAsync(started.Id);

        Assert.Equal("succeeded", done.Status);
        Assert.Equal(1, done.Progress);
        Assert.Null(done.Stage);
        Assert.NotNull(_jobs.Result(started.Id, Everyone));
    }

    [Fact]
    public async Task A_second_request_joins_the_running_job_rather_than_computing_again()
    {
        var first = Start(Key()).Job!;
        var second = Start(Key()).Job!;

        Assert.Equal(first.Id, second.Id);
        await NextComputationAsync();
        Assert.Single(_computations);
    }

    // A refresh follows a write, which the running computation may have read before: it computes from
    // now, and the one it replaces still answers by its id to whoever follows it.
    [Fact]
    public async Task A_refresh_while_one_runs_computes_again_and_leaves_the_other_to_its_followers()
    {
        var first = _jobs.Start(Key(), Request, false, new RecapFollower("alice", null)).Job!;
        await NextComputationAsync();
        _jobs.Start(Key(), Request, false, new RecapFollower("bob", null));

        // Alice wrote: her follow moves to the new computation, Bob's stays on the first.
        var refreshed = _jobs.Start(Key(), Request, true, new RecapFollower("alice", null)).Job!;
        await NextComputationAsync();

        Assert.NotEqual(first.Id, refreshed.Id);
        Assert.Equal(2, _computations.Count);
        Assert.Equal("running", Find(first.Id)!.Status);
        Assert.False(_computations[0].Token.IsCancellationRequested);
        Assert.Equal(refreshed.Id, Start(Key()).Job!.Id);
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

    // A day not sealed yet still changes the figures: such a recap is kept as one in progress is.
    [Fact]
    public async Task A_closed_recap_with_a_day_still_being_counted_is_reused_for_ten_minutes_only()
    {
        var first = Start(Key()).Job!;
        var closed = Snapshot(inProgress: false);
        (await NextComputationAsync()).Finish(closed with { Period = closed.Period with { Incomplete = ["open_day"] } });
        var done = await SettledAsync(first.Id);

        Assert.Equal(_clock.GetUtcNow().ToUnixTimeMilliseconds() + (long)RecapJobs.OpenLifetime.TotalMilliseconds,
            done.ExpiresAtMs);
    }

    [Fact]
    public async Task A_refresh_computes_again_over_a_finished_recap()
    {
        var first = Start(Key()).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: false));
        await SettledAsync(first.Id);

        var refreshed = _jobs.Start(Key(), Request, true, RecapFollower.Anonymous).Job!;

        Assert.NotEqual(first.Id, refreshed.Id);
        // Still read by its id until it expires; a new request reaches the refreshed one.
        Assert.NotNull(_jobs.Result(first.Id, Everyone));
        Assert.Equal(refreshed.Id, Start(Key()).Job!.Id);
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
        var mine = _jobs.Start(Key(audience: Alice), Request, false, RecapFollower.Anonymous).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: true));
        await WaitUntilAsync(() => _jobs.Result(mine.Id, Alice) is not null);

        Assert.Null(_jobs.Find(mine.Id, Everyone));
        Assert.Null(_jobs.Find(mine.Id, Alice with { Owner = "local:bob" }));
        Assert.Null(_jobs.Find(mine.Id, Alice with { Sources = "a\nb" }));
        Assert.Null(_jobs.Result(mine.Id, Everyone));
        Assert.False(_jobs.Cancel(mine.Id, Everyone, RecapFollower.Anonymous));
        Assert.NotNull(_jobs.Find(mine.Id, Alice));
    }

    [Fact]
    public async Task A_cancelled_job_stops_its_computation_and_is_forgotten()
    {
        var job = Start(Key()).Job!;
        var computation = await NextComputationAsync();

        Assert.True(_jobs.Cancel(job.Id, Everyone, RecapFollower.Anonymous));

        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
        Assert.Null(Find(job.Id));
        await WaitUntilAsync(() => _jobs.Outcomes().Contains(("cancelled", 1L)));
        Assert.False(_jobs.Cancel(job.Id, Everyone, RecapFollower.Anonymous));
    }

    [Fact]
    public async Task A_job_two_screens_follow_stops_only_once_both_let_it_go()
    {
        var job = Start(Key()).Job!;
        Assert.Equal(job.Id, Start(Key()).Job!.Id);
        var computation = await NextComputationAsync();

        Assert.True(_jobs.Cancel(job.Id, Everyone, RecapFollower.Anonymous));

        Assert.False(computation.Token.IsCancellationRequested);
        Assert.Equal("running", Find(job.Id)!.Status);

        Assert.True(_jobs.Cancel(job.Id, Everyone, RecapFollower.Anonymous));

        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
        Assert.Null(Find(job.Id));
    }

    [Fact]
    public async Task A_cancelled_job_holds_its_place_until_its_computation_stops()
    {
        var release = new TaskCompletionSource<RecapSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A read that does not look at its token, as a SQLite step does not until it returns.
        var jobs = new RecapJobs((_, _, _) => release.Task, () => _version, _clock, _stopping.Token, _logger);
        var first = jobs.Start(Key("a"), Request, false, RecapFollower.Anonymous).Job!;
        jobs.Start(Key("b"), Request, false, RecapFollower.Anonymous);

        Assert.True(jobs.Cancel(first.Id, Everyone, RecapFollower.Anonymous));

        Assert.Null(jobs.Find(first.Id, Everyone));
        Assert.True(jobs.Start(Key("c"), Request, false, RecapFollower.Anonymous).Busy);

        release.SetCanceled(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => jobs.Sizes().Running == 0);
        Assert.Equal(1, jobs.Sizes().Kept);
        Assert.False(jobs.Start(Key("c"), Request, false, RecapFollower.Anonymous).Busy);
    }

    [Fact]
    public async Task A_cancellation_that_arrives_after_the_recap_finished_leaves_it_kept()
    {
        var job = Start(Key()).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: false));
        await SettledAsync(job.Id);

        Assert.True(_jobs.Cancel(job.Id, Everyone, RecapFollower.Anonymous));

        Assert.Equal(job.Id, Start(Key()).Job!.Id);
    }

    [Fact]
    public async Task A_running_job_keeps_the_weights_it_started_with()
    {
        var slow = Start(Key("slow")).Job!;
        var slowComputation = await NextComputationAsync();
        slowComputation.Progress.Enter(RecapStage.FindingsPeriod);
        slowComputation.Progress.Enter(RecapStage.FindingsOpen);
        var before = Find(slow.Id)!.Progress;

        var other = Start(Key("other")).Job!;
        var otherComputation = await NextComputationAsync();
        otherComputation.Progress.Enter(RecapStage.FindingsPeriod);
        _clock.Advance(TimeSpan.FromMinutes(5));
        otherComputation.Finish(Snapshot(inProgress: false));
        await SettledAsync(other.Id);

        // The finished one taught the weights; the bar of the one still running does not jump for it.
        Assert.Equal(before, Find(slow.Id)!.Progress);
    }

    [Fact]
    public async Task A_stopping_Hub_cancels_what_runs()
    {
        var job = Start(Key()).Job!;
        var computation = await NextComputationAsync();

        await _stopping.CancelAsync();

        await WaitUntilAsync(() => Find(job.Id)?.Status == "cancelled");
        Assert.True(computation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_failed_job_says_so_for_a_minute_then_goes()
    {
        var job = Start(Key()).Job!;
        (await NextComputationAsync()).Release.SetException(new InvalidOperationException("disk"));

        var failed = await SettledAsync(job.Id);

        Assert.Equal("failed", failed.Status);
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
        var second = _jobs.Start(Key("other"), Request, false, RecapFollower.Anonymous).Job!;
        var next = await NextComputationAsync();
        next.Progress.Enter(RecapStage.FindingsPeriod);
        next.Progress.Enter(RecapStage.FindingsOpen);
        Assert.True(Find(second.Id, Key("other"))!.Progress > 20_000.0 / 95_000);
    }

    [Fact]
    public async Task One_caller_computes_one_recap_at_a_time_and_others_keep_their_place()
    {
        var mine = _jobs.Start(Key("a"), Request, false, new RecapFollower("alice", null)).Job!;
        await NextComputationAsync();

        var second = _jobs.Start(Key("b"), Request, false, new RecapFollower("alice", null));
        var joined = _jobs.Start(Key("a"), Request, false, new RecapFollower("alice", null));
        var bobs = _jobs.Start(Key("b"), Request, false, new RecapFollower("bob", null));

        Assert.True(second.Busy);
        Assert.Equal(mine.Id, joined.Job!.Id);
        Assert.False(bobs.Busy);
        await NextComputationAsync();

        // Joining again, as a reload or a second tab does, is still one follow: one let-go stops it.
        Assert.True(_jobs.Cancel(mine.Id, Everyone, new RecapFollower("alice", null)));
        await WaitUntilAsync(() => _jobs.Sizes().Running == 1);
        Assert.False(_jobs.Start(Key("c"), Request, false, new RecapFollower("alice", null)).Busy);
    }

    [Fact]
    public async Task A_follow_its_screen_stops_reading_no_longer_holds_its_caller_and_the_job_runs_on()
    {
        var mine = _jobs.Start(Key("a"), Request, false, new RecapFollower("alice", null)).Job!;
        var computation = await NextComputationAsync();

        _clock.Advance(RecapJobs.FollowLease - TimeSpan.FromSeconds(1));
        Assert.True(_jobs.Start(Key("b"), Request, false, new RecapFollower("alice", null)).Busy);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_jobs.Start(Key("b"), Request, false, new RecapFollower("alice", null)).Busy);
        Assert.Equal("running", Find(mine.Id)!.Status);
        Assert.False(computation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Reading_the_job_keeps_the_callers_follow_counting()
    {
        var mine = _jobs.Start(Key("a"), Request, false, new RecapFollower("alice", null)).Job!;
        await NextComputationAsync();

        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.NotNull(_jobs.Find(mine.Id, Everyone, new RecapFollower("alice", null)));
        _clock.Advance(TimeSpan.FromSeconds(20));

        Assert.True(_jobs.Start(Key("b"), Request, false, new RecapFollower("alice", null)).Busy);
    }

    [Fact]
    public async Task A_write_a_recap_reads_makes_the_next_request_compute_again()
    {
        var kept = Start(Key("a")).Job!;
        (await NextComputationAsync()).Finish(Snapshot(inProgress: false));
        await SettledAsync(kept.Id);
        var running = Start(Key("b")).Job!;
        await NextComputationAsync();

        _version++;

        // The running one may have read before the write: not joined, computed again beside it.
        Assert.NotEqual(running.Id, Start(Key("b")).Job!.Id);
        foreach (var computation in _computations.Skip(1))
            computation.Release.TrySetResult(Snapshot(inProgress: false));
        await WaitUntilAsync(() => _jobs.Sizes().Running == 0);
        Assert.NotEqual(kept.Id, Start(Key("a")).Job!.Id);
    }

    [Fact]
    public async Task A_caller_who_does_not_follow_a_job_cannot_stop_it()
    {
        var job = _jobs.Start(Key(), Request, false, new RecapFollower("alice", null)).Job!;
        var computation = await NextComputationAsync();

        Assert.True(_jobs.Cancel(job.Id, Everyone, new RecapFollower("bob", null)));
        Assert.True(_jobs.Cancel(job.Id, Everyone, RecapFollower.Anonymous));

        Assert.False(computation.Token.IsCancellationRequested);
        Assert.Equal("running", Find(job.Id)!.Status);
        Assert.True(_jobs.Cancel(job.Id, Everyone, new RecapFollower("alice", null)));
        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_refresh_lets_go_of_the_callers_own_running_job_rather_than_finding_them_busy()
    {
        var first = _jobs.Start(Key(), Request, false, new RecapFollower("alice", null)).Job!;
        var computation = await NextComputationAsync();

        var refreshed = _jobs.Start(Key(), Request, true, new RecapFollower("alice", null));

        Assert.False(refreshed.Busy);
        Assert.NotEqual(first.Id, refreshed.Job!.Id);
        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
    }

    // A reload after a write: the job the caller follows is not joined, and must not find them busy either.
    [Fact]
    public async Task A_start_over_a_running_job_a_write_made_stale_moves_the_callers_follow()
    {
        var first = _jobs.Start(Key(), Request, false, new RecapFollower("alice", null)).Job!;
        var computation = await NextComputationAsync();

        _version++;
        var again = _jobs.Start(Key(), Request, false, new RecapFollower("alice", null));

        Assert.False(again.Busy);
        Assert.NotEqual(first.Id, again.Job!.Id);
        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_job_says_how_long_it_has_run_on_the_Hubs_clock()
    {
        var job = Start(Key()).Job!;
        var computation = await NextComputationAsync();

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(30_000, Find(job.Id)!.ElapsedMs);

        computation.Finish(Snapshot(inProgress: true));
        await SettledAsync(job.Id);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(30_000, Find(job.Id)!.ElapsedMs);
    }

    [Fact]
    public async Task Two_tabs_of_one_caller_follow_a_job_apart()
    {
        var first = new RecapFollower("alice", "tab-1");
        var second = new RecapFollower("alice", "tab-2");
        var job = _jobs.Start(Key(), Request, false, first).Job!;
        var computation = await NextComputationAsync();
        _jobs.Start(Key(), Request, false, second);

        Assert.True(_jobs.Cancel(job.Id, Everyone, first));
        Assert.False(computation.Token.IsCancellationRequested);
        Assert.Equal("running", Find(job.Id)!.Status);

        Assert.True(_jobs.Cancel(job.Id, Everyone, second));
        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Two_screens_without_sign_in_are_told_apart_and_a_screen_joining_twice_counts_once()
    {
        var one = new RecapFollower(null, "screen-1");
        var two = new RecapFollower(null, "screen-2");
        var job = _jobs.Start(Key(), Request, false, one).Job!;
        var computation = await NextComputationAsync();
        _jobs.Start(Key(), Request, false, one);
        _jobs.Start(Key(), Request, false, two);

        Assert.True(_jobs.Cancel(job.Id, Everyone, one));
        Assert.False(computation.Token.IsCancellationRequested);
        Assert.True(_jobs.Cancel(job.Id, Everyone, two));
        await WaitUntilAsync(() => computation.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task The_one_recap_per_caller_counts_every_screen_of_theirs()
    {
        _jobs.Start(Key("a"), Request, false, new RecapFollower("alice", "tab-1"));
        await NextComputationAsync();

        Assert.True(_jobs.Start(Key("b"), Request, false, new RecapFollower("alice", "tab-2")).Busy);
        Assert.False(_jobs.Start(Key("b"), Request, false, new RecapFollower("bob", "tab-1")).Busy);
    }

    // ------------------------------------------------------------ helpers

    private static RecapJobKey Key(string? service = null, RecapJobAudience? audience = null)
    {
        return new RecapJobKey(audience ?? Everyone, RecapPeriodKind.Week, new DateOnly(2026, 10, 29), null, null,
            service, null);
    }

    private RecapJobStart Start(RecapJobKey key)
    {
        return _jobs.Start(key, Request, false, RecapFollower.Anonymous);
    }

    private RecapJobState? Find(string id, RecapJobKey? key = null)
    {
        return _jobs.Find(id, (key ?? Key()).Audience);
    }

    private static string StateOf(RecapJobState view, RecapStage stage)
    {
        return view.Stages.Single(item => item.Name == RecapStages.Name(stage)).State;
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
        int seen;
        // Under the lock: a job's thread may be adding its computation while this counts.
        lock (_computations)
        {
            seen = _computations.Count(computation => computation.Claimed);
        }

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

    private async Task<RecapJobState> SettledAsync(string id)
    {
        RecapJobState? view = null;
        await WaitUntilAsync(() => (view = Find(id)) is { Status: not "running" });
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
