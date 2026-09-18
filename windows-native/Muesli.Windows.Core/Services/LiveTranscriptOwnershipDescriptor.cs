namespace Muesli.Windows.Services;

/// <summary>
/// Pure projection of the live-model selection and ownership mode onto the three owners the
/// product must show the user: live preview, final transcript, and measured-gap recovery.
/// Settings, the Models page, the recording coordinator, and the tests share this one
/// definition so no surface can advertise an owner the pipeline does not actually use.
/// </summary>
public sealed record LiveTranscriptOwnershipDescriptor(
    string LivePreviewOwner,
    string FinalTranscriptOwner,
    string GapRecoveryOwner,
    LiveTranscriptOwnershipMode Mode,
    bool IsLiveEnabled)
{
    public const string UnifiedDisplayName = "Unified live + final";
    public const string PreviewOnlyDisplayName = "Preview-only";
    public const string UnifiedSettingValue = "unified-live-final";
    public const string PreviewOnlySettingValue = "preview-only";
    public const string OffOwnerLabel = "Off";
    public const string GapRecoveryUnusedLabel = "Not used (final model owns full transcript)";

    public static IReadOnlyList<string> DisplayNames { get; } = [PreviewOnlyDisplayName, UnifiedDisplayName];

    public static LiveTranscriptOwnershipMode ModeFromDisplayName(string? displayName) =>
        string.Equals(displayName, UnifiedDisplayName, StringComparison.Ordinal)
            ? LiveTranscriptOwnershipMode.UnifiedLiveAndFinal
            : LiveTranscriptOwnershipMode.PreviewOnly;

    public static LiveTranscriptOwnershipMode ModeFromSettingValue(string? value) =>
        string.Equals(value, UnifiedSettingValue, StringComparison.OrdinalIgnoreCase)
            ? LiveTranscriptOwnershipMode.UnifiedLiveAndFinal
            : LiveTranscriptOwnershipMode.PreviewOnly;

    public static string DisplayNameFor(LiveTranscriptOwnershipMode mode) =>
        mode == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal ? UnifiedDisplayName : PreviewOnlyDisplayName;

    public static string SettingValueFor(LiveTranscriptOwnershipMode mode) =>
        mode == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal ? UnifiedSettingValue : PreviewOnlySettingValue;

    /// <summary>
    /// Resolves the displayed owners. A null <paramref name="liveModelId"/> means live
    /// transcription is off, which is the default: the configured final meeting model owns
    /// the whole transcript and no gap recovery exists to attribute.
    /// </summary>
    public static LiveTranscriptOwnershipDescriptor Create(
        string? liveModelId,
        string liveModelLabel,
        LiveTranscriptOwnershipMode mode,
        string finalModelDisplayName)
    {
        if (string.IsNullOrWhiteSpace(liveModelId))
        {
            return new(
                OffOwnerLabel,
                finalModelDisplayName,
                GapRecoveryUnusedLabel,
                LiveTranscriptOwnershipMode.PreviewOnly,
                false);
        }

        return mode == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal
            ? new(liveModelLabel, liveModelLabel, finalModelDisplayName, mode, true)
            : new(liveModelLabel, finalModelDisplayName, GapRecoveryUnusedLabel, mode, true);
    }
}
