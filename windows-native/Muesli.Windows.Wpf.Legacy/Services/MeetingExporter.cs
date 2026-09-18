using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;





namespace Muesli.Windows.Services;

/// <summary>Outcome of an export so the caller can report failure instead of it vanishing.</summary>
public sealed record MeetingExportResult(bool Completed, string? Path, string? Error)
{
    public static MeetingExportResult Cancelled { get; } = new(false, null, null);
    public static MeetingExportResult Success(string path) => new(true, path, null);
    public static MeetingExportResult Failed(string error) => new(false, null, error);
}

public static class MeetingExporter
{
    public static MeetingExportResult Export(MeetingItem meeting, MeetingExportMode mode, Dictionary<string, string>? aliases = null)
    {
        var markdown = BuildMarkdown(meeting, mode, aliases);
        var suggestedName = SuggestFilename(meeting, mode);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            DefaultExt = ".pdf",
            Filter = "PDF document (*.pdf)|*.pdf|Markdown file (*.md)|*.md",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() != true)
            return MeetingExportResult.Cancelled;

        var path = dialog.FileName;

        try
        {
            Write(markdown, path);
        }
        catch (Exception ex)
        {
            // Exporting is a read-only operation over the saved meeting, so a failure here can
            // never corrupt it. Surfacing the reason is what the user actually needs.
            return MeetingExportResult.Failed(ex.Message);
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // The file exported correctly; only the shell hand-off failed.
            return new MeetingExportResult(true, path, $"Exported, but the file could not be opened automatically: {ex.Message}");
        }

        return MeetingExportResult.Success(path);
    }

    internal static void Write(string markdown, string path) => MeetingDocumentWriter.Write(markdown, path);
    internal static string BuildMarkdown(MeetingItem meeting, MeetingExportMode mode, Dictionary<string, string>? aliases)
        => MeetingExportFormatter.BuildMarkdown(meeting, mode, aliases);
    internal static string SuggestFilename(MeetingItem meeting, MeetingExportMode mode)
        => MeetingExportFormatter.SuggestFilename(meeting, mode);
}