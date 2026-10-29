namespace PerfSentinelHub.Recaps;

/// <summary>The fixed plain-language sentence per finding type that the management view carries.</summary>
public static class RecapSentences
{
    private const string Fallback = "The application does work on each action that could be avoided.";

    public static string For(string findingType)
    {
        return findingType switch
        {
            "n_plus_one_sql" =>
                "For each item on a page, the application fetches the same kind of information again, instead of fetching it all at once.",
            "n_plus_one_http" =>
                "For each item, the application asks another part of the system again, instead of asking once for the whole list.",
            "n_plus_one_messaging" =>
                "The application sends one small message per item instead of grouping them into one.",
            "redundant_sql" =>
                "The application fetches the same information several times for one action, when once would do.",
            "redundant_http" =>
                "The application asks another part of the system the same question several times for one action, when once would do.",
            "slow_sql" => "Fetching some information takes long enough to make people wait.",
            "slow_http" => "Waiting on another part of the system takes long enough to make people wait.",
            "slow_messaging" => "Sending or receiving some messages takes long enough to make people wait.",
            "excessive_fanout" => "A single action sets a large number of other parts of the system to work at once.",
            "chatty_service" =>
                "Two parts of the system exchange many small messages for one action, where a few larger ones would do.",
            "pool_saturation" =>
                "Actions wait in line because the application can only reach its data a few at a time.",
            "serialized_calls" =>
                "The application does several steps one after another that could run at the same time, so people wait longer than needed.",
            _ => Fallback
        };
    }
}
