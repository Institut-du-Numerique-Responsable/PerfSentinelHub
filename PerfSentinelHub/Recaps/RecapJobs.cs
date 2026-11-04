using System.Globalization;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     Recap computations the Hub runs off the request that asked for them, so the screen can show
///     where one is and a reader can leave and come back to it. A finished recap is kept for its
///     audience and query, 24 hours for a closed period and 10 minutes for one in progress or with a
///     day still being counted, so a
///     view switch, the whole list of a group or a return to the screen reuses it. In memory only:
///     a restart forgets every job, and the next request computes again.
/// </summary>
public sealed partial class RecapJobs(
    Func<RecapRequest, IRecapProgress, CancellationToken, Task<RecapSnapshot>> compute,
    Func<long> inputsVersion,
    TimeProvider timeProvider,
    CancellationToken stopping,
    ILogger<RecapJobs> logger)
{
    // The caps are a contract, not a detail, as the request gates' are.
    public const int MaxRunning = 2;

    // One person computes one recap at a time, so nobody holds every place: a caller who starts
    // another while one they follow still runs is busy until they let it go. A caller with no
    // identity, a Hub without sign-in, is held by MaxRunning alone.
    public const int MaxRunningPerCaller = 1;

    // How long a follow counts toward MaxRunningPerCaller without its screen reading the job again. A
    // tab closed or reloaded without letting go stops holding its caller past it; the job runs on.
    public static readonly TimeSpan FollowLease = TimeSpan.FromSeconds(30);

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
    ///     unless <see cref="MaxRunning" /> already run or the caller of <paramref name="follower" />
    ///     already follows <see cref="MaxRunningPerCaller" /> running from any screen. <paramref name="refresh" /> always starts a new
    ///     one: it follows a write, which a computation already running may have read before. The one it
    ///     replaces stays readable by its id for the screens that follow it, and no request joins it.
    ///     <paramref name="follower" /> follows the job it gets, until it lets it go.
    /// </summary>
    public RecapJobStart Start(RecapJobKey key, RecapRequest request, bool refresh, RecapFollower follower)
    {
        Job job;
        RecapJobState view;
        lock (_gate)
        {
            Prune();
            var version = inputsVersion();
            // Reused or joined only while nothing it read has been written since it started: an ack,
            // an action or a name changes the recap, wherever it was written.
            if (!refresh && _byKey.TryGetValue(key, out var known) && known.InputsVersion == version &&
                known.Status is RecapJobStatus.Running or RecapJobStatus.Succeeded)
            {
                if (known.Status == RecapJobStatus.Running)
                    known.Follow(follower);
                Count(RecapJobOutcome.Reused);
                return new RecapJobStart(ViewOf(known));
            }

            // The new job replaces this screen's own follow of the running one it supersedes, refreshed or
            // made stale by a write, so it never finds the caller busy with the very computation it
            // replaces. A follower with neither screen nor identity lets go only on a refresh: its
            // follow is not told apart from another's.
            if ((refresh || follower != RecapFollower.Anonymous) && _byKey.TryGetValue(key, out var replaced) &&
                replaced.Status == RecapJobStatus.Running)
                LetGo(replaced, follower);

            if (_jobs.Values.Count(item => item.Status == RecapJobStatus.Running) >= MaxRunning ||
                (follower.Caller is { } caller && _jobs.Values.Count(item =>
                    item is { Status: RecapJobStatus.Running, Abandoned: false } &&
                    item.FollowedLatelyBy(caller)) >= MaxRunningPerCaller))
            {
                Count(RecapJobOutcome.Busy);
                return new RecapJobStart(null);
            }

            job = new Job(this, key, _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                InputsVersion = version
            };
            job.Follow(follower);
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
        return new RecapJobStart(view);
    }

    /// <summary>
    ///     The job, for its audience only: any other caller reads it as unknown. A read by a screen that
    ///     follows it renews its follow, which is how a screen keeps holding its place.
    /// </summary>
    public RecapJobState? Find(string id, RecapJobAudience audience, RecapFollower? follower = null)
    {
        lock (_gate)
        {
            Prune();
            if (Visible(id, audience) is not { } job)
                return null;
            if (follower is not null)
                job.Renew(follower);
            return ViewOf(job);
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
    ///     Ends the follow of <paramref name="follower" />, its screen's or, for a client that names none,
    ///     its caller's: one that does not follow it changes nothing, so nobody stops a computation
    ///     another screen waits on. A
    ///     running one stops once none follows it, reads as unknown at once and holds its place under
    ///     <see cref="MaxRunning" /> until its computation returns; a finished one stays kept for the
    ///     others. False for an unknown one.
    /// </summary>
    public bool Cancel(string id, RecapJobAudience audience, RecapFollower follower)
    {
        lock (_gate)
        {
            if (Visible(id, audience) is not { } job)
                return false;
            if (job.Status == RecapJobStatus.Running)
                LetGo(job, follower);
            return true;
        }
    }

    // One follow of the caller ends; the computation stops with the last one.
    private void LetGo(Job job, RecapFollower follower)
    {
        if (!job.Unfollow(follower) || job.Followed)
            return;
        // Marked first: Cancel runs its callbacks inline, and one that ends the computation reaches
        // RunAsync's finally, which forgets only a job already abandoned.
        job.Abandoned = true;
        Unkey(job);
        job.Cancellation.Cancel();
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
                // A closed period with a day still being counted (open_day) still changes: kept as one in progress.
                var settled = !snapshot.Period.InProgress && !snapshot.Period.Incomplete.Contains("open_day");
                job.ExpiresAtMs = nowMs + (long)(settled ? ClosedLifetime : OpenLifetime).TotalMilliseconds;
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
    private RecapJobState ViewOf(Job job)
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
            // Measured on the Hub's clock, so a browser whose clock differs still counts from here.
            (job.FinishedAtMs ?? _clock.GetUtcNow().ToUnixTimeMilliseconds()) - job.StartedAtMs,
            job.FinishedAtMs,
            job.ExpiresAtMs,
            job.Status == RecapJobStatus.Failed ? ProblemCodes.RecapFailed : null);
    }

    private void Learn(Job job)
    {
        foreach (var stage in RecapStages.All)
            // At least 1 ms: weights that all decayed to 0 would make the progress 0/0, which JSON cannot write.
            if (job.States[(int)stage] == RecapStageState.Done && job.Ms[(int)stage] is { } ms)
                _expectedMs[(int)stage] = (_expectedMs[(int)stage] * (1 - Smoothing)) + (Math.Max(1, ms) * Smoothing);
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

        // Each follow and when it was last read: one per screen, the id a launcher page sends, or one
        // per signed-in caller for a client that sends none. Callers with neither, who cannot be told
        // apart, count once per start or join.
        private readonly Dictionary<string, (string? Caller, long Seen)> _follows = new(StringComparer.Ordinal);
        private int _anonymousFollows;

        public long InputsVersion { get; init; }

        public bool Followed => _follows.Count > 0 || _anonymousFollows > 0;

        private static string? KeyOf(RecapFollower follower)
        {
            return follower switch
            {
                { Screen: { } screen } => (follower.Caller ?? "") + "\n" + screen,
                { Caller: { } caller } => caller + "\n",
                _ => null
            };
        }

        public void Follow(RecapFollower follower)
        {
            if (KeyOf(follower) is { } key)
                _follows[key] = (follower.Caller, owner._clock.GetTimestamp());
            else
                _anonymousFollows++;
        }

        // A read of the job by a screen or caller that follows it keeps that follow counting.
        public void Renew(RecapFollower follower)
        {
            if (KeyOf(follower) is { } key && _follows.ContainsKey(key))
                _follows[key] = (follower.Caller, owner._clock.GetTimestamp());
        }

        public bool Unfollow(RecapFollower follower)
        {
            if (KeyOf(follower) is { } key)
                return _follows.Remove(key);
            if (_anonymousFollows == 0)
                return false;
            _anonymousFollows--;
            return true;
        }

        // Whether one of the caller's screens read it within the lease.
        public bool FollowedLatelyBy(string caller)
        {
            return _follows.Values.Any(follow =>
                follow.Caller == caller && owner._clock.GetElapsedTime(follow.Seen) < FollowLease);
        }

        // Let go by its last follower: unknown to every read, though it holds its place until it returns.
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
