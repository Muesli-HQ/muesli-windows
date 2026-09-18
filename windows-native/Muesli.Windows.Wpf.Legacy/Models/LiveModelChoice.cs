using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed record LiveModelChoice(string? Id, string Label)
{
    public static LiveModelChoice Off { get; } = new(null, LiveTranscriptOwnershipDescriptor.OffOwnerLabel);

    /// <summary>
    /// The shared ComboBox template renders SelectionBoxItem without a SelectionBoxItemTemplate,
    /// so the closed picker falls back to ToString().
    /// </summary>
    public override string ToString() => Label;
}
