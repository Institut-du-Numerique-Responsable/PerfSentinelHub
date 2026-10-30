namespace PerfSentinelHub.Recaps;

public static class ActionConfirmation
{
    /// <summary>
    ///     Whether a fix marked done held. Only days after the done day count, since the fix
    ///     may have shipped late that day: a clean sealed day with no detection confirms,
    ///     a detection after a clean day means it came back, a detection before one
    ///     contradicts it, and nothing sealed yet leaves it awaiting.
    /// </summary>
    public static string Judge(int doneDay, IEnumerable<int> detectionDays, IEnumerable<int> cleanSealedDays)
    {
        var detected = detectionDays.Where(day => day > doneDay).Order().Select(day => (int?)day).FirstOrDefault();
        var clean = cleanSealedDays.Where(day => day > doneDay).ToList();

        if (detected is not { } first) return clean.Count > 0 ? "confirmed" : "awaiting";
        return clean.Any(day => day < first) ? "came_back" : "contradicted";
    }
}
