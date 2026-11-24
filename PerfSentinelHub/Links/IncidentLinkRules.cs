namespace PerfSentinelHub.Links;

/// <summary>
///     How strongly one frozen finding links to its incident: mechanism, intensity and recurrence
///     signals folded into one level. Any change to a threshold bumps <see cref="RulesVersion" />.
/// </summary>
public static class IncidentLinkRules
{
    public const int RulesVersion = 4;

    private const double HighIntensityRatio = 3;
    private const double MediumIntensityRatio = 1.5;
    private const double SaturationBoost = 0.9;
    private const int RecurrenceMinEarlier = 2;
    private const double RecurrenceHighShare = 0.5;
    private const double RecurrenceHighFactor = 2;

    private enum Mechanism { Compatible, Partial, Incompatible }

    private enum Signal { None, Medium, High }

    public static LinkResult Assess(LinkInput input)
    {
        var reasons = new List<LinkReason>();
        var mechanism = Judge(input);
        if (mechanism == Mechanism.Compatible && input.IncidentKind == "deploy")
            reasons.Add(new LinkReason("appeared_after_deploy"));
        else
            reasons.Add(new LinkReason($"mechanism_{mechanism.ToString().ToLowerInvariant()}"));

        var intensity = Intensity(input, reasons);
        var recurrence = Recurrence(input, reasons);

        if (mechanism == Mechanism.Compatible && input.Saturation is >= SaturationBoost
            && (intensity == Signal.Medium || recurrence == Signal.Medium))
        {
            // The reason is stored only when the boost lifts the level, not when a signal was already high.
            var lifted = intensity != Signal.High && recurrence != Signal.High;
            intensity = intensity == Signal.Medium ? Signal.High : intensity;
            recurrence = recurrence == Signal.Medium ? Signal.High : recurrence;
            if (lifted) reasons.Add(new LinkReason("saturation", Value: input.Saturation));
        }

        var best = (Signal)Math.Max((int)intensity, (int)recurrence);
        var level = mechanism switch
        {
            Mechanism.Compatible => best switch
            {
                Signal.High => LinkLevel.Strong,
                Signal.Medium => LinkLevel.Medium,
                _ => LinkLevel.Weak
            },
            Mechanism.Partial when best == Signal.High => LinkLevel.Medium,
            _ => LinkLevel.Weak
        };
        return new LinkResult(level, reasons);
    }

    private static Mechanism Judge(LinkInput input)
    {
        var type = input.FindingType;
        switch (input.IncidentKind)
        {
            case "deploy":
                return input.AppearedAfterDeploy ? Mechanism.Compatible : Mechanism.Incompatible;
            case "oom_kill" or "memory_saturation":
                if (type.StartsWith("n_plus_one_", StringComparison.Ordinal)
                    || type.StartsWith("redundant_", StringComparison.Ordinal)
                    || type is "excessive_fanout" or "chatty_service")
                    return Mechanism.Compatible;
                return type is "pool_saturation" or "serialized_calls" ? Mechanism.Partial : Mechanism.Incompatible;
            default:
                return Mechanism.Partial;
        }
    }

    private static Signal Intensity(LinkInput input, List<LinkReason> reasons)
    {
        var baseline = input.Baseline;
        if (baseline.CoveredDays <= 0)
        {
            reasons.Add(new LinkReason("intensity_unknown"));
            return Signal.None;
        }

        // Seen only on days a gap or an overflow leaves uncovered: not new, its usual share is unknown.
        if (baseline.Detections <= 0 && input.SeenInBaselineDays)
        {
            reasons.Add(new LinkReason("intensity_unknown"));
            return Signal.None;
        }

        if (baseline.Detections <= 0)
        {
            reasons.Add(new LinkReason("intensity_new"));
            return Signal.High;
        }

        // An empty total has no share to compare: the ratio would be infinite or NaN, which cannot be serialized.
        // Checked after a new finding, since an incident of new findings only has no known total.
        if (input.WindowTotal <= 0 || input.BaselineTotal <= 0)
        {
            reasons.Add(new LinkReason("intensity_unknown"));
            return Signal.None;
        }

        // The only known finding makes up both totals: a share of 1 against 1 says nothing about a spike.
        if (input.BaselineTotal <= baseline.Detections)
        {
            reasons.Add(new LinkReason("intensity_alone"));
            return Signal.None;
        }

        // Its share of the incident's findings against its usual share: a busy hour raises every count alike, so
        // comparing counts to a daily mean read nearly every finding of a daytime incident as a spike.
        var ratio = (double)input.SeenCount / input.WindowTotal / ((double)baseline.Detections / input.BaselineTotal);
        if (ratio < MediumIntensityRatio) return Signal.None;
        reasons.Add(new LinkReason("intensity", Ratio: ratio));
        return ratio >= HighIntensityRatio ? Signal.High : Signal.Medium;
    }

    private static Signal Recurrence(LinkInput input, List<LinkReason> reasons)
    {
        var (n, k) = (input.Recurrence.Earlier, input.Recurrence.WithSignature);
        if (n <= 0 || k <= 0) return Signal.None;

        var covered = input.Baseline.CoveredDays;
        double? usual = covered > 0 ? (double)input.Baseline.DaysSeen / covered : null;
        var share = (double)k / n;
        var signal = Signal.None;
        if (usual is not null
            && n >= RecurrenceMinEarlier && share >= RecurrenceHighShare && share >= RecurrenceHighFactor * usual.Value)
            signal = Signal.High;
        else if (share > (usual ?? 0))
            signal = Signal.Medium;
        // Without ledger coverage the usual share is unknown: recurrence never goes above medium.
        if (signal != Signal.None) reasons.Add(new LinkReason("recurrence", K: k, N: n, UsualShare: usual));
        return signal;
    }
}
