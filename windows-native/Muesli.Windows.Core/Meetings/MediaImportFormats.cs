using System.IO;

namespace Muesli.Windows.Services;

public sealed record MediaImportFormat(string Extension, string DisplayName, string Codec);

/// <summary>
/// The media formats Muesli will accept for import.
///
/// The list is deliberately restricted to extensions whose decode path has actually been exercised
/// end-to-end on the packaged runtime, not to everything Media Foundation might theoretically open.
/// Advertising a format that then fails at import is worse than not offering it: the user has
/// already committed a recording they may not be able to reproduce.
///
/// Qualified 2026-08-02 by decoding real human-speech fixtures transcoded from the multilingual
/// reference audio and comparing decoded duration against the source WAV:
/// wav, mp3, m4a, aac, mp4, mov, mkv (Opus), webm (Opus) all decoded within 0.1 s of source.
/// ogg/Vorbis threw on open — Windows Media Foundation ships no Vorbis decoder — so it is excluded
/// and handled by <see cref="ConversionGuidanceFor"/> instead.
/// </summary>
public static class MediaImportFormats
{
    public static IReadOnlyList<MediaImportFormat> Supported { get; } =
    [
        new(".wav", "WAV", "PCM"),
        new(".mp3", "MP3", "MPEG Layer III"),
        new(".m4a", "M4A", "AAC"),
        new(".aac", "AAC", "AAC"),
        new(".mp4", "MP4", "AAC audio track"),
        new(".mov", "MOV", "AAC audio track"),
        new(".mkv", "MKV", "Opus audio track"),
        new(".webm", "WebM", "Opus audio track")
    ];

    /// <summary>
    /// Formats a user is likely to try that this machine cannot decode, with the reason. Kept
    /// separate from <see cref="Supported"/> so the guidance stays specific instead of generic.
    /// </summary>
    private static readonly Dictionary<string, string> KnownUnsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ogg"] = "Windows has no built-in Vorbis decoder, so .ogg cannot be decoded on this machine.",
        [".oga"] = "Windows has no built-in Vorbis decoder, so .oga cannot be decoded on this machine.",
        [".opus"] = "Bare .opus streams are not decodable here; the same audio in a .webm or .mkv container works.",
        [".flac"] = "FLAC is not decodable through the packaged media path.",
        [".wma"] = "WMA is not qualified for import.",
        [".amr"] = "AMR is not qualified for import.",
        [".aiff"] = "AIFF is not qualified for import.",
        [".aif"] = "AIFF is not qualified for import.",
        [".avi"] = "AVI containers vary too much to qualify; extract the audio track first.",
        [".wmv"] = "WMV is not qualified for import."
    };

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        Supported.Select(format => format.Extension).ToList();

    /// <summary>OpenFileDialog filter listing only formats that genuinely decode.</summary>
    public static string DialogFilter =>
        $"Supported media ({string.Join(", ", Supported.Select(f => "*" + f.Extension))})|" +
        $"{string.Join(";", Supported.Select(f => "*" + f.Extension))}";

    public static bool IsSupported(string? path)
    {
        var extension = ExtensionOf(path);
        return extension is not null &&
               SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Actionable guidance for a file Muesli will not import. Always names a concrete next step,
    /// because "unsupported format" alone leaves the user with no way forward.
    /// </summary>
    public static string ConversionGuidanceFor(string? path)
    {
        var extension = ExtensionOf(path);
        if (extension is null)
        {
            return "That file has no extension Muesli recognises. Convert it to WAV or MP3 and import again.";
        }
        if (IsSupported(path))
        {
            return "";
        }

        var reason = KnownUnsupported.TryGetValue(extension, out var known)
            ? known
            : $"{extension} is not a qualified import format on Windows.";
        return $"{reason} Convert it to WAV (best quality) or MP3, then import the converted file. " +
               $"Muesli imports: {string.Join(", ", Supported.Select(format => format.Extension))}. " +
               "Your original file is never modified or deleted.";
    }

    private static string? ExtensionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var extension = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
