using System.Text.Json.Serialization;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

// Pinned on the context so a payload written through an explicit JsonTypeInfo
// gets the same casing as one written through the ASP.NET options.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(DetectionKnob))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(IReadOnlyList<SourceResponse>))]
[JsonSerializable(typeof(IReadOnlyList<StoredEpisode>))]
[JsonSerializable(typeof(SubmittedAnalysis))]
[JsonSerializable(typeof(AnalysisProblem))]
[JsonSerializable(typeof(IReadOnlyList<SourceAckResponse>))]
[JsonSerializable(typeof(AccessResponse))]
[JsonSerializable(typeof(OAuthError))]
[JsonSerializable(typeof(DeviceAuthorizationResponse))]
[JsonSerializable(typeof(DeviceTokenResponse))]
[JsonSerializable(typeof(DeviceVerifyResponse))]
[JsonSerializable(typeof(IReadOnlyList<TerminalTokenResponse>))]
[JsonSerializable(typeof(string[]))]
internal partial class HubJsonContext : JsonSerializerContext;
