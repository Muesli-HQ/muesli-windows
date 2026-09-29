using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

public sealed record TranscriptionResult(
    string Text,
    string? Diagnostic = null,
    int DurationMs = 0,
    List<TranscriptSegment>? Segments = null);

public sealed record TranscriptSegment(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("speaker")]
    string Speaker,
    [property: JsonPropertyName("startMs")]
    int StartMs,
    [property: JsonPropertyName("endMs")]
    int EndMs,
    [property: JsonPropertyName("text")]
    string Text);

public sealed record DiarizationResult(
    [property: JsonPropertyName("transcriptText")]
    string? TranscriptText,
    [property: JsonPropertyName("detectedLanguage")]
    string? DetectedLanguage,
    [property: JsonPropertyName("durationMs")]
    int DurationMs,
    [property: JsonPropertyName("segments")]
    List<DiarizedSegment> Segments,
    [property: JsonPropertyName("warnings")]
    List<string> Warnings)
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("modelLoadMs")]
    public long ModelLoadMs { get; init; }

    [JsonPropertyName("modelReused")]
    public bool ModelReused { get; init; }

    [JsonPropertyName("processingMs")]
    public long ProcessingMs { get; init; }
}

public sealed record DiarizedSegment(
    [property: JsonPropertyName("speakerId")]
    string SpeakerId,
    [property: JsonPropertyName("startMs")]
    int StartMs,
    [property: JsonPropertyName("endMs")]
    int EndMs);

public sealed record ModelOperationResult(string Text, string? Diagnostic = null);

public sealed record ModelDownloadProgress(
    string Stage,
    long BytesReceived,
    long? TotalBytes)
{
    public int? Percent => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes.Value, 0, 100)
        : null;

    public string DisplayText => Percent is int percent
        ? $"{Stage} {percent}%"
        : Stage;
}
