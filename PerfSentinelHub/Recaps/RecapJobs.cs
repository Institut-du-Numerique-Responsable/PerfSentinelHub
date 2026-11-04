using System.Globalization;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     Recap computations the Hub runs off the request that asked for them, so the screen can show
///     where one is and a reader can leave and come back to it. A finished recap is kept for its
///     audience and query, 24 hours for a closed period and 10 minutes for one in progress, so a
///     view switch, the whole list of a group or a return to the screen reuses it. In memory only:
///     a restart forgets every job, and the next request computes again.
/// </summary>
public sealed partial class RecapJobs(
    Func<RecapRequest, IRecapProgress, CancellationToken, Task<RecapSnapshot>> compute,
    TimeProvider timeProvider,
    CancellationToken stopping,
    ILogger<RecapJobs> logger)
{
    // The caps are a contract, not a detail, as the request gates' are.
    public const int MaxRunning = 2;

    // A recap of 5,000 findings weighs a few MB, beside a Hub that runs in 1 GiB.
    public const int MaxKept = 32;

    public static readonly TimeSpan ClosedLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan OpenLifetime = TimeSpan.FromMinutes(10);

    // Long enough for the screen that polled it to read the failure.
    public static readonly TimeSpan EndedLifetime = TimeSpan.FromMinutes(1);

    // How much one finished computation moves the expected duration of each stage.
    private const double Smoothing = 0.3;

    private static readonly RecapJobOutcome[] AllOutcomes = Enum.GetValues<RecapJobOutcome>();

    private readonly TimeProvider _clock = timeProvider;
    private readonly long[] _counts = new long[AllOutcomes.Length];

    // What each stage is expected to take, in ms: the weights of the progress. The defaults put the
    // findings reads first, which is where the time goes on a large database.
    private readonly double[] _expectedMs =
        [.. RecapStages.All.Select(stage => stage <= RecapStage.FindingsPrevious ? 20_000.0 : 3_000.0)];

    private readonly Lock _gate = new();
    private readonly Dictionary<RecapJobKey, Job> _byKey = [];
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    /// <summary>
    ///     The job for this key: the one running or the finished one while it is kept, else a new one,
    ///     unless <see cref="MaxRunning" /> already run. <paramref name="refresh" /> always starts a new
    ///     one: it follows a write, which a computation already running may have read before. The one it
    ///     replaces stays readable by its id for the screens that follow it, and no request joins it.
    /// </summary>
    public RecapJobStart Start(RecapJobKey key, RecapRequest request, bool refresh)
    {
        Job job;
        RecapJobState view;
        lock (_gate)
        {
            Prune();
            if (!refresh && _byKey.TryGetValue(key, out var known) &&
                known.Status is RecapJobStatus.Running or RecapJobStatus.Succeeded)
            {
                if (known.Status == RecapJobStatus.Running)
                    known.Followers++;
                Count(RecapJobOutcome.Reused);
                return new RecapJobStart(ViewOf(known), false);
            }

            if (_jobs.Values.Count(item => item.Status == RecapJobStatus.Running) >= MaxRunning)
            {
                Count(RecapJobOutcome.Busy);
                return new RecapJobStart(null, true);
            }

            job = new Job(this, key, _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                CancellationTokenSource.CreateLinkedTokenSource(stopping));
            _jobs[job.Id] = job;
            _byKey[key] = job;
            EvictOverflow();
            Count(RecapJobOutcome.Started);
            view = ViewOf(job);
        }

        // A thread of its own: SQLite runs its Async methods synchronously, so on the pool a long
        // recap would hold a pool thread the requests queue behind.
        _ = Task.Factory.StartNew(
            () => RunAsync(job, request),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        return new RecapJobStart(view, false);
    }

    /// <summary>The job, for its audience only: any other caller reads it as unknown.</summary>
    public RecapJobState? Find(string id, RecapJobAudience audience)
    {
        lock (_gate)
        {
            Prune();
            return Visible(id, audience) is { } job ? ViewOf(job) : null;
        }
    }

    /// <summary>The finished recap, or null while it runs, when it failed or for another audience.</summary>
    public RecapSnapshot? Result(string id, RecapJobAudience audience)
    {
        lock (_gate)
        {
            Prune();
            return Visible(id, audience)?.Result;
        }
    }

    /// <summary>
    ///     Lets a job go for one screen that started or joined it. A running one stops once none follows
    ///     it, reads as unknown at once and holds its place under <see cref="MaxRunning" /> until its
    ///     computation returns; a finished one stays kept for the others. False for an unknown one.
    /// </summary>
    public bool Cancel(string id, RecapJobAudience audience)
    {
        lock (_gate)
        {
            if (Visible(id, audience) is not { } job)
                return false;
            if (job.Status == RecapJobStatus.Running && --job.Followers == 0)
            {
                job.Cancellation.Cancel();
                job.Abandoned = true;
                Unkey(job);
            }

            return true;
        }
    }

    /// <summary>The jobs running and the jobs held, finished or not, for the metrics.</summary>
    public (int Running, int Kept) Sizes()
    {
        lock (_gate)
        {
            Prune();
            return (_jobs.Values.Count(job => job.Status == RecapJobStatus.Running), _jobs.Count);
        }
    }

    /// <summary>Every outcome with its count, the zeros included.</summary>
    public IEnumerable<(string Outcome, long Count)> Outcomes()
    {
        return AllOutcomes.Select(outcome => (Label(outcome), Interlocked.Read(ref _counts[(int)outcome])));
    }

    public static string Label(RecapJobOutcome outcome)
    {
        return outcome switch
        {
            RecapJobOutcome.Started => "started",
            RecapJobOutcome.Reused => "reused",
            RecapJobOutcome.Busy => "busy",
            RecapJobOutcome.Succeeded => "succeeded",
            RecapJobOutcome.Failed => "failed",
            RecapJobOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
    }

    private async Task RunAsync(Job job, RecapRequest request)
    {
        try
        {
            var snapshot = await compute(request, job, job.Cancellation.Token);
            string stages;
            long totalMs;
            lock (_gate)
            {
                job.CloseStage();
                var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                job.Finish(RecapJobStatus.Succeeded, nowMs);
                job.Result = snapshot;
                job.ExpiresAtMs = nowMs + (long)(snapshot.Period.InProgress ? OpenLifetime : ClosedLifetime)
                    .TotalMilliseconds;
                Learn(job);
                stages = job.Timings();
                totalMs = nowMs - job.StartedAtMs;
            }

            Count(RecapJobOutcome.Succeeded);
            LogComputed(logger, snapshot.Period.Kind, snapshot.Period.From, totalMs, stages);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            lock (_gate)
            {
                job.Finish(RecapJobStatus.Cancelled, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            }

            Count(RecapJobOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                job.Finish(RecapJobStatus.Failed, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            }

            Count(RecapJobOutcome.Failed);
            LogFailed(logger, exception);
        }
        finally
        {
            // Under the lock, Cancel only reaches a running job, so none reaches a disposed source.
            lock (_gate)
            {
                job.Cancellation.Dispose();
                // Unknown since its last follower let it go: its place under MaxRunning is free now.
                if (job.Abandoned)
                    Forget(job);
            }
        }
    }

    private Job? Visible(string id, RecapJobAudience audience)
    {
        return _jobs.TryGetValue(id, out var job) && !job.Abandoned && job.Key.Audience == audience ? job : null;
    }

    // The job as GET /api/recap/jobs/{id} answers it.
    private static RecapJobState ViewOf(Job job)
    {
        var total = job.Weights.Sum();
        var reached = RecapStages.All
            .Where(stage => job.States[(int)stage] is RecapStageState.Done or RecapStageState.Skipped)
            .Sum(stage => job.Weights[(int)stage]);
        var progress = job.Status == RecapJobStatus.Succeeded ? 1 : Math.Min(1, reached / total);
        return new RecapJobState(
            job.Id,
            RecapStages.Name(job.Status),
            job.Status == RecapJobStatus.Running && job.Current is { } current ? RecapStages.Name(current) : null,
            [
                .. RecapStages.All.Select(stage => new RecapJobStageItem(
                    RecapStages.Name(stage), RecapStages.Name(job.States[(int)stage]), job.Ms[(int)stage]))
            ],
            Math.Round(progress, 4),
            job.StartedAtMs,
            job.FinishedAtMs,
            job.ExpiresAtMs,
            job.Status == RecapJobStatus.Failed ? "recap_failed" : null);
    }

    private void Learn(Job job)
    {
        foreach (var stage in RecapStages.All)
            if (job.States[(int)stage] == RecapStageState.Done && job.Ms[(int)stage] is { } ms)
                _expectedMs[(int)stage] = (_expectedMs[(int)stage] * (1 - Smoothing)) + (ms * Smoothing);
    }

    private void Prune()
    {
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var job in _jobs.Values.ToList())
        {
            var gone = job.Status switch
            {
                RecapJobStatus.Succeeded => nowMs >= job.ExpiresAtMs,
                RecapJobStatus.Failed or RecapJobStatus.Cancelled =>
                    nowMs - job.FinishedAtMs >= (long)EndedLifetime.TotalMilliseconds,
                _ => false
            };
            if (gone)
                Forget(job);
        }
    }

    // The oldest finished jobs go first. A running one is never dropped: MaxRunning bounds those.
    private void EvictOverflow()
    {
        while (_jobs.Count > MaxKept &&
               _jobs.Values.Where(job => job.Status != RecapJobStatus.Running)
                   .MinBy(job => job.FinishedAtMs) is { } oldest)
            Forget(oldest);
    }

    private void Forget(Job job)
    {
        _jobs.Remove(job.Id);
        Unkey(job);
    }

    // No request joins or reuses it any more; a newer job for the key stays.
    private void Unkey(Job job)
    {
        if (_byKey.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job))
            _byKey.Remove(job.Key);
    }

    private void Count(RecapJobOutcome outcome)
    {
        Interlocked.Increment(ref _counts[(int)outcome]);
    }

    [LoggerMessage(2507, LogLevel.Information, "Recap {Kind} from {From} computed in {TotalMs} ms: {Stages}.")]
    private static partial void LogComputed(ILogger logger, string kind, string from, long totalMs, string stages);

    [LoggerMessage(2508, LogLevel.Warning, "A recap computation failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    /// <summary>One computation, told its stages as RecapService takes them. Every field is read and written under the registry's lock.</summary>
    private sealed class Job(RecapJobs owner, RecapJobKey key, long startedAtMs, CancellationTokenSource cancellation)
        : IRecapProgress
    {
        private long _stageStarted;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public RecapJobKey Key { get; } = key;
        public long StartedAtMs { get; } = startedAtMs;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public RecapJobStatus Status { get; private set; } = RecapJobStatus.Running;
        public RecapStage? Current { get; private set; }
        // The expected durations as they stood at its start, so a job finishing elsewhere never moves its bar back.
        public double[] Weights { get; } = [.. owner._expectedMs];

        // The screens that started or joined it while it ran, and whether the last one let it go.
        public int Followers { get; set; } = 1;
        public bool Abandoned { get; set; }

        public RecapStageState[] States { get; } = new RecapStageState[RecapStages.All.Count];
        public long?[] Ms { get; } = new long?[RecapStages.All.Count];
        public long? FinishedAtMs { get; private set; }
        public long? ExpiresAtMs { get; set; }
        public RecapSnapshot? Result { get; set; }

        public void Enter(RecapStage stage)
        {
            lock (owner._gate)
            {
                CloseStage();
                States[(int)stage] = RecapStageState.Running;
                Current = stage;
                _stageStarted = owner._clock.GetTimestamp();
            }
        }

        public void Skip(RecapStage stage)
        {
            lock (owner._gate)
            {
                States[(int)stage] = RecapStageState.Skipped;
            }
        }

        public void CloseStage()
        {
            if (Current is not { } stage || States[(int)stage] != RecapStageState.Running)
                return;
            States[(int)stage] = RecapStageState.Done;
            Ms[(int)stage] = (long)owner._clock.GetElapsedTime(_stageStarted).TotalMilliseconds;
        }

        public void Finish(RecapJobStatus status, long nowMs)
        {
            Status = status;
            FinishedAtMs = nowMs;
        }

        public string Timings()
        {
            return string.Join(", ", RecapStages.All
                .Where(stage => Ms[(int)stage] is not null)
                .Select(stage => RecapStages.Name(stage) + " " +
                                 Ms[(int)stage]!.Value.ToString(CultureInfo.InvariantCulture) + " ms"));
        }
    }
}
