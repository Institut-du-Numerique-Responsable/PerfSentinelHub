namespace PerfSentinelHub.Api;

/// <summary>
///     Why an import was refused. A closed set: these become Prometheus label
///     values, and a label fed from anything a caller controls is how a /metrics
///     endpoint dies of cardinality.
/// </summary>
public enum ImportRejection
{
    /// <summary>Malformed query string, unparseable body, or an empty batch.</summary>
    BadRequest,

    /// <summary>Unknown source id, or a key that does not match the configured one.</summary>
    Unauthorized,

    /// <summary>Too many imports already in flight. Raise the uploader's spacing.</summary>
    GateFull,

    /// <summary>The write lock did not come free in five seconds.</summary>
    WriteTimeout,

    /// <summary>Body over the size limit, or more findings than one batch accepts.</summary>
    TooLarge
}
