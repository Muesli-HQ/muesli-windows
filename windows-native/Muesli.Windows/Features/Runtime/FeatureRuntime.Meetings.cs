using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Muesli.Windows.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private void CopyMeeting_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        CopyMeetingToClipboard(item);
    }
}
private void OpenMeetingAudio_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingItem item })
    {
        return;
    }
    OpenMeetingAudio(item);
}
private void CopyMeetingToClipboard(MeetingItem item)
{
    var text = string.IsNullOrWhiteSpace(item.Summary)
        ? item.Transcript
        : $"{item.Summary}{Environment.NewLine}{Environment.NewLine}## Transcript{Environment.NewLine}{item.Transcript}";
    System.Windows.Clipboard.SetText(text);
    DictationStatus = "Copied meeting";
    _toastNotificationService.Show("Copied", item.Title, ToastState.Success);
}
private void OpenMeetingDetail(MeetingItem item)
{
    _appServices.Navigation.OpenMeetingDetail(item.Id);
    _selectedMeeting = item;
    _transcriptEditSession = null;
    IsEditingTranscript = false;
    TranscriptEditDraft = item.Transcript;
    PendingRetranscriptionCandidate = _transcriptEditService.GetPendingCandidate(item.Id);
    RetranscriptionStatus = PendingRetranscriptionCandidate is null
        ? ""
        : "A retranscription candidate is ready. Accept it to replace the current transcript, or reject it to keep the original.";
    _selectedMeetingTemplate = NormalizeSummaryTemplateName(string.IsNullOrWhiteSpace(item.TemplateName) ? SelectedSummaryTemplate : item.TemplateName);
    _activeSpeakerAliases = new Dictionary<string, string>(item.SpeakerAliases ?? new Dictionary<string, string>());
    BuildSpeakerAliasPanel();
    BuildMeetingWarningsPanel(item);
    BuildMeetingNotesContent();
    RefreshMeetingPlaybackTracks(item);
    OnPropertyChanged(nameof(SelectedMeetingTitle));
    OnPropertyChanged(nameof(SelectedMeetingTitleOwnership));
    OnPropertyChanged(nameof(SelectedMeetingManualNotes));
    OnPropertyChanged(nameof(SelectedMeetingMetadata));
    OnPropertyChanged(nameof(SelectedMeetingNotes));
    OnPropertyChanged(nameof(SelectedMeetingTemplate));
    OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
    OnPropertyChanged(nameof(SelectedMeetingTranscript));
    MeetingsBrowserView.Visibility = Visibility.Collapsed;
    MeetingDetailView.Visibility = Visibility.Visible;
    var showTranscript = string.IsNullOrWhiteSpace(item.Summary) && !string.IsNullOrWhiteSpace(item.Transcript)
        ? true
        : _lastMeetingDetailShowTranscript;
    ShowMeetingDetailTab(showTranscript);
}
private void OpenMeetingAudio(MeetingItem item)
{
    OpenMeetingDetail(item);
    if (!HasMeetingPlayback)
    {
        DictationStatus = "Meeting audio file not found";
        _toastNotificationService.Show("Audio not found", item.Title, ToastState.Error);
        return;
    }
    ShowPage(AppPage.MeetingDetail);
    DictationStatus = "Meeting recording ready to play";
}
private void DeleteMeeting(MeetingItem item)
{
    var audioPaths = item.SourcePath
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(path => _captureStorageService.IsOwnedMeetingAudioPath(item.Id, path))
        .ToList();
    if (audioPaths.Count > 0)
    {
        var choice = System.Windows.MessageBox.Show(
            "Delete the audio files saved for this meeting too?\n\nYes deletes this meeting's owned recordings. No keeps the audio files. Imported media is never deleted.",
            "Delete meeting",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel)
        {
            return;
        }
        if (choice == MessageBoxResult.Yes)
        {
            var cleanup = _captureStorageService.DeleteOwnedMeetingAudio(item.Id, audioPaths);
            if (cleanup.FailedPaths.Count > 0)
            {
                _logService.Info($"Meeting removed, but {cleanup.FailedPaths.Count} owned audio file(s) could not be deleted.");
            }
        }
    }

    _transcriptEditService.DeleteMeetingScratch(item.Id);
    Meetings.Remove(item);
    SaveMeetings(afterExplicitDeletion: true);
    RefreshMeetingViews();
    RefreshSearchResults();
    DictationStatus = "Deleted meeting";
}
private void DeleteMeeting_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        DeleteMeeting(item);
    }
}
private void OpenMeetingDetail_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        OpenMeetingDetail(item);
        ShowPage(AppPage.MeetingDetail);
    }
}
private void BackToMeetings_Click(object sender, RoutedEventArgs e)
{
    _appServices.Navigation.ReturnToMeetings();
    SaveActiveSpeakerAliases();
    _meetingPlaybackTimer.Stop();
    _meetingPlaybackService.Close();
    _selectedMeeting = null;
    _transcriptEditSession = null;
    IsEditingTranscript = false;
    PendingRetranscriptionCandidate = null;
    TranscriptEditDraft = "";
    RetranscriptionStatus = "";
    _selectedMeetingTemplate = NormalizeSummaryTemplateName(SelectedSummaryTemplate);
    MeetingsBrowserView.Visibility = Visibility.Visible;
    MeetingDetailView.Visibility = Visibility.Collapsed;
    MeetingWarningsPanel.Visibility = Visibility.Collapsed;
    MeetingWarningsItems.ItemsSource = null;
    ShowPage(AppPage.Meetings);
}
private void ShowMeetingNotesTab_Click(object sender, MouseButtonEventArgs e)
{
    _lastMeetingDetailShowTranscript = false;
    ShowMeetingDetailTab(showTranscript: false);
}
private void ShowMeetingTranscriptTab_Click(object sender, MouseButtonEventArgs e)
{
    _lastMeetingDetailShowTranscript = true;
    ShowMeetingDetailTab(showTranscript: true);
}
private void ShowMeetingDetailTab(bool showTranscript)
{
    MeetingNotesPanel.Visibility = showTranscript ? Visibility.Collapsed : Visibility.Visible;
    MeetingTranscriptPanel.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
    MeetingNotesTab.Background = showTranscript
        ? System.Windows.Media.Brushes.Transparent
        : (System.Windows.Media.Brush)FindResource("SurfaceSelectedBrush");
    MeetingTranscriptTab.Background = showTranscript
        ? (System.Windows.Media.Brush)FindResource("SurfaceSelectedBrush")
        : System.Windows.Media.Brushes.Transparent;
    MeetingNotesTabLabel.Foreground = showTranscript
        ? (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
        : (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
    MeetingTranscriptTabLabel.Foreground = showTranscript
        ? (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
        : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
}
private void BuildSpeakerAliasPanel()
{
    SpeakerAliasPanel.Children.Clear();
    if (_selectedMeeting is null || string.IsNullOrWhiteSpace(_selectedMeeting.Transcript))
        return;

    var labels = DetectSpeakerLabels(_selectedMeeting.Transcript);
    if (labels.Count == 0)
        return;

    var header = new TextBlock
    {
        Text = "Speakers",
        Style = (Style)FindResource("SectionLabel"),
        Margin = new Thickness(0, 0, 0, 8)
    };
    SpeakerAliasPanel.Children.Add(header);

    var rows = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
    foreach (var label in labels)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Width = 80
        };

        var aliasBox = new System.Windows.Controls.TextBox
        {
            Text = _activeSpeakerAliases.TryGetValue(label, out var alias) ? alias : "",
            FontSize = 13,
            Padding = new Thickness(8, 4, 8, 4),
            Background = (System.Windows.Media.Brush)FindResource("BackgroundHoverBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 200,
            Tag = label
        };
        aliasBox.TextChanged += (_, _) =>
        {
            _activeSpeakerAliases[(string)aliasBox.Tag] = aliasBox.Text;
            OnPropertyChanged(nameof(SelectedMeetingTranscript));
            OnPropertyChanged(nameof(SelectedMeetingNotes));
            BuildMeetingNotesContent();
            _aliasSaveDebounceTimer.Stop();
            _aliasSaveDebounceTimer.Start();
        };

        row.Children.Add(labelText);
        row.Children.Add(aliasBox);
        rows.Children.Add(row);
    }

    SpeakerAliasPanel.Children.Add(rows);
}

private void SaveActiveSpeakerAliases()
{
    if (_selectedMeeting is null)
        return;

    var filtered = _activeSpeakerAliases
        .Where(p => !string.IsNullOrWhiteSpace(p.Value) && p.Key != p.Value.Trim())
        .ToDictionary(p => p.Key, p => p.Value.Trim());

    if (filtered.Count == 0 && (_selectedMeeting.SpeakerAliases is null || _selectedMeeting.SpeakerAliases.Count == 0))
        return;

    var index = Meetings.IndexOf(_selectedMeeting);
    if (index < 0)
        return;

    var updated = _selectedMeeting with { SpeakerAliases = filtered };
    Meetings[index] = updated;
    _selectedMeeting = updated;
    SaveMeetings();
}

private void BuildMeetingWarningsPanel(MeetingItem item)
{
    var warnings = MeetingRecordingCoordinator.CleanupHealthWarnings(item.HealthWarnings, item.Transcript);
    if (warnings.Count == 0)
    {
        MeetingWarningsPanel.Visibility = Visibility.Collapsed;
        MeetingWarningsItems.ItemsSource = null;
        return;
    }

    MeetingWarningsItems.ItemsSource = warnings;
    MeetingWarningsPanel.Visibility = Visibility.Visible;
}

private static System.Windows.Controls.Grid CreateWrappedNoteRow(UIElement leading, TextBlock content)
{
    var row = new System.Windows.Controls.Grid
    {
        Margin = new Thickness(0, 2, 0, 2),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
    };
    row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
    {
        Width = System.Windows.GridLength.Auto
    });
    row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
    {
        Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
    });

    content.TextWrapping = TextWrapping.Wrap;
    content.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;

    System.Windows.Controls.Grid.SetColumn(leading, 0);
    System.Windows.Controls.Grid.SetColumn(content, 1);
    row.Children.Add(leading);
    row.Children.Add(content);
    return row;
}

private static void SetMarkdownInlineText(TextBlock target, string text)
{
    target.Inlines.Clear();
    var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\*\*(.+?)\*\*");
    if (matches.Count == 0)
    {
        target.Text = text;
        return;
    }

    target.Text = "";
    var index = 0;
    foreach (System.Text.RegularExpressions.Match match in matches)
    {
        if (match.Index > index)
        {
            target.Inlines.Add(new System.Windows.Documents.Run(text[index..match.Index]));
        }

        target.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run(match.Groups[1].Value)));
        index = match.Index + match.Length;
    }

    if (index < text.Length)
    {
        target.Inlines.Add(new System.Windows.Documents.Run(text[index..]));
    }
}

private void BuildMeetingNotesContent()
{
    MeetingNotesContent.Children.Clear();

    var text = SelectedMeetingNotes;
    if (string.IsNullOrWhiteSpace(text))
    {
        var emptyState = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 4)
        };
        emptyState.Children.Add(new TextBlock
        {
            Text = "\uE70F",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = (System.Windows.Media.Brush)FindResource("TextTertiaryBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        emptyState.Children.Add(new TextBlock
        {
            Text = "No notes yet",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        emptyState.Children.Add(new TextBlock
        {
            Text = "Generate structured notes from this meeting transcript using the selected template.",
            FontSize = 14,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var generateButton = new WpfButton
        {
            Content = "Generate Notes",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        generateButton.Click += GenerateSelectedMeetingNotes_Click;
        emptyState.Children.Add(generateButton);
        MeetingNotesContent.Children.Add(emptyState);
        return;
    }

    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
    foreach (var rawLine in lines)
    {
        var line = rawLine.TrimEnd();
        if (string.IsNullOrWhiteSpace(line))
        {
            MeetingNotesContent.Children.Add(new System.Windows.Controls.Grid { Height = 8 });
            continue;
        }

        var headingMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,3})\s+(.+)$");
        if (headingMatch.Success)
        {
            var level = headingMatch.Groups[1].Value.Length;
            var headingText = headingMatch.Groups[2].Value.Trim();
            MeetingNotesContent.Children.Add(new TextBlock
            {
                Text = headingText,
                FontWeight = FontWeights.Bold,
                FontSize = level == 1 ? 22 : (level == 2 ? 17 : 14),
                Foreground = (System.Windows.Media.Brush)FindResource(level <= 2 ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, level == 1 ? 4 : 18, 0, level == 3 ? 6 : 10)
            });
            continue;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^---+\s*$"))
        {
            MeetingNotesContent.Children.Add(new Border
            {
                Height = 1,
                Background = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
                Margin = new Thickness(0, 8, 0, 8)
            });
            continue;
        }

        var checkboxMatch = System.Text.RegularExpressions.Regex.Match(line, @"^-\s+\[([ xX])\]\s*(.*)$");
        if (checkboxMatch.Success)
        {
            var isChecked = checkboxMatch.Groups[1].Value.Trim().Equals("x", StringComparison.OrdinalIgnoreCase);
            var itemText = checkboxMatch.Groups[2].Value.Trim();
            var icon = new TextBlock
            {
                Text = isChecked ? "\uE73D" : "\uE739",
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource(isChecked ? "AccentBlueBrush" : "TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 8, 0)
            };
            var content = new TextBlock
            {
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap
            };
            SetMarkdownInlineText(content, itemText);
            MeetingNotesContent.Children.Add(CreateWrappedNoteRow(icon, content));
            continue;
        }

        var bulletMatch = System.Text.RegularExpressions.Regex.Match(line, @"^[-\u2022]\s+(.+)$");
        if (bulletMatch.Success)
        {
            var bulletText = bulletMatch.Groups[1].Value.Trim();
            var bullet = new TextBlock
            {
                Text = "\u2022",
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var content = new TextBlock
            {
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            SetMarkdownInlineText(content, bulletText);
            MeetingNotesContent.Children.Add(CreateWrappedNoteRow(bullet, content));
            continue;
        }

        var numberedMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)\.\s+(.+)$");
        if (numberedMatch.Success)
        {
            var number = numberedMatch.Groups[1].Value.Trim();
            var numText = numberedMatch.Groups[2].Value.Trim();
            var index = new TextBlock
            {
                Text = number + ".",
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0),
                Width = 20
            };
            var content = new TextBlock
            {
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            SetMarkdownInlineText(content, numText);
            MeetingNotesContent.Children.Add(CreateWrappedNoteRow(index, content));
            continue;
        }

        var paragraph = new TextBlock
        {
            FontSize = 14,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
            LineHeight = 22
        };
        SetMarkdownInlineText(paragraph, line);
        MeetingNotesContent.Children.Add(paragraph);
    }
}
private void GenerateSelectedMeetingNotes_Click(object sender, RoutedEventArgs e) =>
    _ = GenerateSelectedMeetingNotesAsync();

private async Task GenerateSelectedMeetingNotesAsync()
{
    if (_selectedMeeting is null || string.IsNullOrWhiteSpace(_selectedMeeting.Transcript))
    {
        return;
    }

    if (!string.IsNullOrWhiteSpace(_selectedMeeting.Summary))
    {
        var result = System.Windows.MessageBox.Show(
            $"Regenerate notes for \"{_selectedMeeting.Title}\" using the \"{SelectedMeetingTemplate}\" template?",
            "Regenerate notes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
    }

    _summaryCancellation?.Dispose();
    _summaryCancellation = new CancellationTokenSource();
    var summaryToken = _summaryCancellation.Token;
    IsSummarizing = true;
    try
    {
        DictationStatus = "Generating meeting notes";
        _toastNotificationService.Show("Generating notes", SelectedMeetingTemplate, ToastState.Transcribing, 0);
        var summary = await CreateMeetingSummaryWithSettingsAsync(
            _selectedMeeting.Transcript,
            _selectedMeeting.Title,
            CurrentSettingsSnapshot() with
            {
                MeetingSummaryTemplate = SelectedMeetingTemplate,
                MeetingSummaryPromptOverride = CustomMeetingTemplates.FirstOrDefault(template =>
                    template.Name.Equals(SelectedMeetingTemplate, StringComparison.OrdinalIgnoreCase))?.Prompt ?? ""
            },
            summaryToken);

        var index = Meetings.IndexOf(_selectedMeeting);
        if (index < 0)
        {
            return;
        }

        // Regeneration replaces generated notes only. Manual notes and a manually chosen title are
        // the user's own writing and are carried across untouched.
        var updated = _selectedMeeting with
        {
            Summary = summary,
            TemplateName = SelectedMeetingTemplate,
            ManualNotes = _selectedMeeting.ManualNotes,
            TitleIsManual = _selectedMeeting.TitleIsManual
        };
        Meetings[index] = updated;
        _selectedMeeting = updated;
        SaveMeetings();
        _transcriptEditService.ClearGeneratedNotesStale(updated.Id);
        RefreshSearchResults();
        BuildMeetingWarningsPanel(updated);
        BuildMeetingNotesContent();
        OnPropertyChanged(nameof(SelectedMeetingNotes));
        OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
        if (!_lastSummaryUsedLocalFallback)
        {
            DictationStatus = "Meeting notes ready";
            _toastNotificationService.Show("Notes ready", updated.Title, ToastState.Success, 2800);
        }
    }
    catch (OperationCanceledException)
    {
        // Cancelling must leave the existing notes exactly as they were.
        DictationStatus = "Notes generation cancelled; existing notes are unchanged";
        _toastNotificationService.Show("Cancelled", "Existing notes were left unchanged", ToastState.Idle, 2600);
    }
    catch (Exception exception)
    {
        _summaryRetryAvailable = true;
        DictationStatus = $"Notes generation failed: {exception.Message}";
        _toastNotificationService.Show("Notes generation failed", $"{exception.Message} · use Retry", ToastState.Error, 4200);
        OnPropertyChanged(nameof(CanRetrySummary));
    }
    finally
    {
        IsSummarizing = false;
    }
}

private void MoreMeetingActions_Click(object sender, RoutedEventArgs e)
{
    if (sender is System.Windows.Controls.Button button && button.ContextMenu is not null)
    {
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }
}
private void ExportMeetingNotes_Click(object sender, RoutedEventArgs e) => RunExport(MeetingExportMode.Notes);
private void ExportMeetingTranscript_Click(object sender, RoutedEventArgs e) => RunExport(MeetingExportMode.Transcript);
private void ExportFullMeeting_Click(object sender, RoutedEventArgs e) => RunExport(MeetingExportMode.FullMeeting);

private void ShowMeetingAutomationDiagnostics_Click(object sender, RoutedEventArgs e)
{
    var result = _selectedMeeting?.AutomationResult;
    if (result is null)
    {
        System.Windows.MessageBox.Show(
            _shell.Window,
            "No post-meeting automation result is recorded. Existing meetings are never run retroactively.",
            "Automation diagnostics",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return;
    }

    var lines = new List<string>
    {
        $"Status: {result.Status}",
        $"Started: {result.StartedAtUtc:u}",
        $"Finished: {result.CompletedAtUtc:u}",
        $"Attempts: {result.Attempts}",
        $"Exit code: {result.ExitCode?.ToString() ?? "none"}",
        $"Markdown export: {(result.Export.Completed ? "completed" : result.Export.Requested ? "failed" : "not requested")}",
        $"Destination ownership: {result.Export.DestinationOwnership}"
    };
    if (!string.IsNullOrWhiteSpace(result.Export.DestinationPath)) lines.Add($"Export path: {result.Export.DestinationPath}");
    if (!string.IsNullOrWhiteSpace(result.Error)) lines.Add($"Result: {result.Error}");
    if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        lines.Add($"\nstdout{(result.StandardOutputTruncated ? " (truncated)" : "")}\n{result.StandardOutput}");
    if (!string.IsNullOrWhiteSpace(result.StandardError))
        lines.Add($"\nstderr{(result.StandardErrorTruncated ? " (truncated)" : "")}\n{result.StandardError}");

    System.Windows.MessageBox.Show(
        _shell.Window,
        string.Join(Environment.NewLine, lines),
        "Automation diagnostics",
        MessageBoxButton.OK,
        result.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
}

/// <summary>
/// Exports and reports the outcome. Export only reads the saved meeting, so a failure cannot
/// corrupt it, but it must never fail silently either.
/// </summary>
private void RunExport(MeetingExportMode mode)
{
    if (_selectedMeeting is null) return;
    var result = MeetingExporter.Export(_selectedMeeting, mode, _activeSpeakerAliases);
    if (result is { Completed: false, Error: null })
    {
        return;
    }
    if (!result.Completed)
    {
        DictationStatus = $"Export failed: {result.Error}";
        _logService.Info($"Meeting export failed. mode={mode}; transcriptLogged=false");
        _toastNotificationService.Show("Export failed", result.Error ?? "Unknown error", ToastState.Error, 4600);
        System.Windows.MessageBox.Show(
            $"The export could not be written.\n\n{result.Error}\n\nYour saved meeting is unchanged.",
            "Export failed",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return;
    }
    DictationStatus = result.Error is null ? "Export ready" : result.Error;
    _toastNotificationService.Show(
        result.Error is null ? "Exported" : "Exported with a warning",
        System.IO.Path.GetFileName(result.Path) ?? "",
        result.Error is null ? ToastState.Success : ToastState.Error,
        3200);
}
private void CopySelectedMeetingNotes_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null)
    {
        return;
    }
    if (string.IsNullOrWhiteSpace(SelectedMeetingNotes))
    {
        DictationStatus = "No notes to copy";
        _toastNotificationService.Show("No notes yet", "Generate notes first", ToastState.Error, 2600);
        return;
    }
    System.Windows.Clipboard.SetText(SelectedMeetingNotes);
    DictationStatus = "Copied meeting notes";
    _toastNotificationService.Show("Copied notes", _selectedMeeting.Title, ToastState.Success);
}
private void CopySelectedMeetingTranscript_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null)
    {
        return;
    }
    System.Windows.Clipboard.SetText(SelectedMeetingTranscript);
    DictationStatus = "Copied transcript";
    _toastNotificationService.Show("Copied transcript", _selectedMeeting.Title, ToastState.Success);
}
private void OpenSelectedMeetingAudio_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is not null)
    {
        OpenMeetingAudio(_selectedMeeting);
    }
}
private void DeleteSelectedMeeting_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null)
    {
        return;
    }
    var item = _selectedMeeting;
    BackToMeetings_Click(sender, e);
    DeleteMeeting(item);
}
private void MoveMeeting_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingItem item } element)
    {
        return;
    }
    OpenMoveMeetingMenu(element, item);
}
private void MoveSelectedMeeting_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null || sender is not FrameworkElement element)
    {
        return;
    }
    OpenMoveMeetingMenu(element, _selectedMeeting);
}
private void OpenMoveMeetingMenu(FrameworkElement placementTarget, MeetingItem meeting)
{
    var menu = new ContextMenu();
    AddMoveMenuItem(menu, "All Meetings", meeting, null);
    if (MeetingFolders.Count > 0)
    {
        menu.Items.Add(new Separator());
    }
    foreach (var folder in MeetingFolders)
    {
        AddMoveMenuItem(menu, folder.Name, meeting, folder.Id);
    }
    menu.PlacementTarget = placementTarget;
    menu.IsOpen = true;
}
private void AddMoveMenuItem(ContextMenu menu, string header, MeetingItem meeting, string? folderId)
{
    var item = new MenuItem
    {
        Header = header,
        Tag = new MoveMeetingRequest(meeting.Id, folderId)
    };
    item.Click += MoveMeetingToFolder_Click;
    menu.Items.Add(item);
}
private void MoveMeetingToFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not MenuItem { Tag: MoveMeetingRequest request })
    {
        return;
    }
    var index = Meetings.ToList().FindIndex(meeting => meeting.Id == request.MeetingId);
    if (index < 0)
    {
        return;
    }
    var updated = Meetings[index] with { FolderId = request.FolderId };
    Meetings[index] = updated;
    if (_selectedMeeting?.Id == updated.Id)
    {
        _selectedMeeting = updated;
        OnPropertyChanged(nameof(SelectedMeetingMetadata));
    }
    SaveMeetings();
    RefreshMeetingViews();
    DictationStatus = "Moved meeting";
}
private void ImportMeeting_Click(object sender, RoutedEventArgs e) => _ = ImportMeetingAsync();

private async Task ImportMeetingAsync()
{
    // The button is disabled while an import runs; this guards the keyboard and automation paths.
    if (IsImportingMeeting)
    {
        return;
    }
    var dialog = new Microsoft.Win32.OpenFileDialog
    {
        Title = "Import meeting audio or video",
        // Only formats whose decode path is actually qualified; "All files" is kept so a user can
        // still pick anything, and then gets explicit conversion guidance instead of a decode crash.
        Filter = $"{MediaImportFormats.DialogFilter}|All files|*.*"
    };
    if (dialog.ShowDialog(_shell.Window) != true)
    {
        return;
    }
    if (!MediaImportFormats.IsSupported(dialog.FileName))
    {
        var guidance = MediaImportFormats.ConversionGuidanceFor(dialog.FileName);
        DictationStatus = "Unsupported media format";
        _logService.Info($"Import rejected before decode. extension={System.IO.Path.GetExtension(dialog.FileName)}");
        System.Windows.MessageBox.Show(guidance, "Cannot import this file", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }
    var displayName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
    _importCancellation?.Dispose();
    _importCancellation = new CancellationTokenSource();
    var importToken = _importCancellation.Token;
    IsImportingMeeting = true;
    ReportImportProgress(MeetingImportStage.Preparing, null);

    // Set once the transcript exists. Cancelling after that point keeps the transcript rather than
    // throwing away inference the user already waited for.
    string? recoveredTranscript = null;
    TranscriptionResult? recoveredResult = null;
    try
    {
        DictationStatus = "Transcribing meeting";
        _toastNotificationService.Show("Transcribing meeting", System.IO.Path.GetFileName(dialog.FileName), ToastState.Transcribing, 0);
        // Progress created on the UI thread, so reports marshal back here off the worker.
        var progress = new Progress<TranscriptionProgress>(report =>
            ReportImportProgress(MeetingImportProgressMapper.From(report.Stage), report.Fraction));
        var result = await _meetingTranscriptionClient.TranscribeFileAsync(
            displayName,
            dialog.FileName,
            progress,
            importToken);
        _transcriptionPipelineService.LogTranscriptionResult(
            "imported media", result, _meetingTranscriptionClient.EngineId, _meetingTranscriptionClient.ModelId);
        ReportImportProgress(MeetingImportStage.CleaningUp, null);
        var transcript = await _transcriptionPipelineService.PrepareImportedTranscriptAsync(
            result.Text,
            EnableLocalCleanup,
            DictionaryEntries.Select(entry => entry.Record),
            importToken);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            DictationStatus = "No speech detected in imported meeting";
            _toastNotificationService.Show("No speech detected", System.IO.Path.GetFileName(dialog.FileName), ToastState.Error, 3600);
            return;
        }
        recoveredTranscript = transcript;
        recoveredResult = result;
        ReportImportProgress(MeetingImportStage.GeneratingNotes, null);
        var summary = await CreateMeetingSummaryAsync(transcript, displayName, importToken);
        SaveImportedMeeting(dialog.FileName, displayName, transcript, summary, result.DurationMs);
        if (!_lastSummaryUsedLocalFallback)
        {
            DictationStatus = "Meeting transcribed";
            _toastNotificationService.Show("Meeting ready", displayName, ToastState.Success);
        }
        ShowPage(MeetingsPage, MeetingsNav);
    }
    catch (OperationCanceledException)
    {
        // Cancelling before a transcript exists leaves nothing behind. Cancelling once one exists
        // keeps it: discarding a finished transcript because notes were interrupted would destroy
        // the expensive half of the work. The source file is never touched either way.
        if (recoveredTranscript is not null)
        {
            SaveImportedMeeting(
                dialog.FileName,
                displayName,
                recoveredTranscript,
                summary: "",
                recoveredResult?.DurationMs ?? 0);
            DictationStatus = "Import cancelled during notes; the transcript was saved";
            _logService.Info("Import cancelled after transcription; transcript retained without notes.");
            _toastNotificationService.Show(
                "Transcript saved",
                "Notes were cancelled — use Generate Notes when ready",
                ToastState.Idle,
                4200);
            ShowPage(MeetingsPage, MeetingsNav);
        }
        else
        {
            DictationStatus = "Import cancelled; nothing was saved";
            _logService.Info("Import cancelled before a transcript existed; no meeting created.");
            _toastNotificationService.Show(
                "Import cancelled",
                "Your original file is unchanged",
                ToastState.Idle,
                2600);
        }
    }
    catch (Exception exception)
    {
        DictationStatus = $"Meeting import failed: {exception.Message}";
        _toastNotificationService.Show("Meeting import failed", exception.Message, ToastState.Error, 4200);
    }
    finally
    {
        IsImportingMeeting = false;
        ImportProgressPercent = 0;
        ImportProgressLabel = "";
    }
}

/// <summary>
/// Persists an imported meeting. The imported file is recorded as the source path only; it is
/// never copied into the meeting directory and never becomes owned audio.
/// </summary>
private void SaveImportedMeeting(
    string sourcePath,
    string title,
    string transcript,
    string summary,
    int durationMs)
{
    var meeting = new MeetingItem(
        $"meet_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        title,
        DateTime.Now,
        transcript,
        summary,
        sourcePath,
        _meetingTranscriptionClient.ModelId,
        durationMs,
        _selectedMeetingFolderId,
        CountWords(transcript),
        SelectedSummaryTemplate,
        FinalTranscriptOwnerModelId: _meetingTranscriptionClient.ModelId);
    Meetings.Insert(0, meeting);
    try
    {
        SaveMeetings();
    }
    catch
    {
        Meetings.Remove(meeting);
        throw;
    }
    RefreshMeetingViews();
    RefreshSearchResults();
}
private void ToggleMeetingRecording_Click(object sender, RoutedEventArgs e) =>
    _ = ToggleMeetingRecordingAsync(null);
private async Task ToggleMeetingRecordingAsync(DetectedMeeting? detectedMeeting)
{
    if (!WorkflowEntryPoints.CanToggleMeeting(_meetingRecordingCoordinator.IsBusy))
    {
        return;
    }
    if (!_isMeetingRecording)
    {
        try
        {
            _meetingOperationCancellation?.Dispose();
            _meetingOperationCancellation = new CancellationTokenSource();
            DictationStatus = "Preparing meeting capture";
            _toastNotificationService.Show("Preparing meeting", "Starting microphone and meeting audio", ToastState.Transcribing, 0);
            var start = await _meetingRecordingCoordinator.StartAsync(
                SelectedMicrophone,
                detectedMeeting?.Title,
                SaveMeetingRecordings,
                detectedMeeting?.ProcessId,
                _meetingOperationCancellation.Token,
                SelectedLiveMeetingModel.Id is { } liveModelId
                    ? new LiveTranscriptionConfiguration(liveModelId, SelectedOwnershipMode, ShowLiveWaveformOnHover)
                    : null);
            _currentMeetingTitle = detectedMeeting?.Title;
            _isMeetingRecording = start.State is MeetingSessionState.Recording or MeetingSessionState.DegradedRecording;
            _meetingAutoStopTracker = new MeetingAutoStopTracker(
                detectedMeeting is null
                    ? MeetingRecordingStartOrigin.Manual
                    : MeetingRecordingStartOrigin.DetectedMeeting,
                detectedMeeting?.Key);
            if (detectedMeeting is not null)
            {
                _meetingAutoStopTracker.Observe(detectedMeeting.Key, DateTimeOffset.UtcNow);
            }
            StartMeetingAutoStopMonitor();
            DictationStatus = start.State == MeetingSessionState.DegradedRecording
                ? "Meeting recording started with a missing channel"
                : "Recording meeting";
            _toastNotificationService.Show(
                start.State == MeetingSessionState.DegradedRecording ? "Recording degraded" : "Recording meeting",
                start.Warning ?? (start.SystemCaptureMode == SystemAudioCaptureMode.ProcessTreeLoopback
                    ? "Capturing microphone and the meeting process"
                    : "Capturing microphone and default Windows output"),
                start.State == MeetingSessionState.DegradedRecording ? ToastState.Error : ToastState.Recording,
                start.State == MeetingSessionState.DegradedRecording ? 4200 : 0);
            OnPropertyChanged(nameof(MeetingRecordingButtonText));
            ShowPage(MeetingsPage, MeetingsNav);
        }
        catch (OperationCanceledException)
        {
            ResetMeetingRecordingUi("Meeting recording cancelled");
        }
        catch (Exception exception)
        {
            DictationStatus = $"Meeting recording failed: {exception.Message}";
            _toastNotificationService.Show("Meeting recording failed", exception.Message, ToastState.Error);
            RefreshRecoverableMeetingSessions();
        }
        return;
    }
    try
    {
        StopMeetingAutoStopMonitor();
        DictationStatus = "Transcribing meeting";
        _toastNotificationService.Show("Transcribing meeting", "Processing local meeting audio", ToastState.Transcribing, 0);
        _isMeetingRecording = false;
        OnPropertyChanged(nameof(MeetingRecordingButtonText));
        var title = string.IsNullOrWhiteSpace(_currentMeetingTitle)
            ? $"Meeting {DateTime.Now:yyyy-MM-dd HH-mm}"
            : _currentMeetingTitle;
        _meetingOperationCancellation?.Dispose();
        _meetingOperationCancellation = new CancellationTokenSource();
        var result = await _meetingRecordingCoordinator.StopAsync(
            title,
            SaveMeetingRecordings,
            _meetingOperationCancellation.Token);
        _liveTranscriptWindow?.Hide();
        _currentMeetingTitle = null;
        var meeting = await PersistRecordedMeetingAsync(result);
        DictationStatus = result.SessionState == MeetingSessionState.Completed
            ? "Meeting ready"
            : "Meeting audio saved; transcript needs recovery";
        _toastNotificationService.Show(
            result.SessionState == MeetingSessionState.Completed ? "Meeting ready" : "Meeting needs attention",
            result.SessionState == MeetingSessionState.Completed ? meeting.Title : "Audio was retained without a final transcript",
            result.SessionState == MeetingSessionState.Completed ? ToastState.Success : ToastState.Error,
            result.SessionState == MeetingSessionState.Completed ? 2200 : 4200);
        ShowPage(MeetingsPage, MeetingsNav);
    }
    catch (OperationCanceledException)
    {
        RefreshRecoverableMeetingSessions();
        ResetMeetingRecordingUi("Meeting finalization cancelled; audio retained for recovery");
    }
    catch (MeetingSessionRecoverableException)
    {
        RefreshRecoverableMeetingSessions();
        ResetMeetingRecordingUi("Meeting finalization failed; audio retained for recovery");
        _toastNotificationService.Show(
            "Meeting retained for recovery",
            "Use Recover interrupted in Meetings to retry",
            ToastState.Error,
            5200);
    }
    catch (Exception exception)
    {
        StopMeetingAutoStopMonitor();
        _currentMeetingTitle = null;
        _isMeetingRecording = false;
        OnPropertyChanged(nameof(MeetingRecordingButtonText));
        DictationStatus = $"Meeting recording failed: {exception.Message}";
        _toastNotificationService.Show("Meeting recording failed", exception.Message, ToastState.Error, 4200);
    }
}
private void StartMeetingAutoStopMonitor()
{
    _meetingAutoStopTimer.Stop();
    if (_meetingAutoStopTracker?.IsArmed == true)
    {
        _meetingAutoStopTimer.Start();
    }
}
private void StopMeetingAutoStopMonitor()
{
    _meetingAutoStopTimer.Stop();
}
private void MeetingAutoStopTimer_Tick(object? sender, EventArgs e) =>
    _ = HandleMeetingAutoStopTimerTickAsync();

private async Task HandleMeetingAutoStopTimerTickAsync()
{
    if (!_isMeetingRecording)
    {
        StopMeetingAutoStopMonitor();
        return;
    }
    if (!_meetingRecordingCoordinator.IsRecording && !_meetingRecordingCoordinator.IsBusy)
    {
        ResetMeetingRecordingUi("Meeting recording ended");
        return;
    }
    var scan = _meetingDetectionService.CheckNow(publish: false);
    var shouldStop = _meetingAutoStopTracker?.Observe(
        scan.DetectedMeeting?.Key,
        DateTimeOffset.UtcNow) == true;
    if (!shouldStop || _meetingRecordingCoordinator.IsBusy)
    {
        return;
    }
    _logService.Info("Qualified detected-meeting signal disappeared after the auto-stop grace period; stopping recording.");
    await ToggleMeetingRecordingAsync(null);
}
private void AliasSaveDebounceTimer_Tick(object? sender, EventArgs e)
{
    _aliasSaveDebounceTimer.Stop();
    SaveActiveSpeakerAliases();
}
private void ResetMeetingRecordingUi(string status)
{
    StopMeetingAutoStopMonitor();
    _currentMeetingTitle = null;
    _isMeetingRecording = false;
    _liveTranscriptWindow?.Hide();
    OnPropertyChanged(nameof(MeetingRecordingButtonText));
    DictationStatus = status;
    _toastNotificationService.ShowIdle(SelectedHotkey);
}

private async Task<MeetingItem> PersistRecordedMeetingAsync(RecordedMeetingResult result)
{
    var transcript = string.IsNullOrWhiteSpace(result.Transcript)
        ? ""
        : await _transcriptionPipelineService.PrepareMeetingTranscriptAsync(
            result.Transcript,
            EnableLocalCleanup,
            DictionaryEntries.Select(entry => entry.Record));
    var summary = string.IsNullOrWhiteSpace(transcript)
        ? ""
        : await CreateMeetingSummaryAsync(transcript, result.Title);
    var sourceAudioPath = string.Join(
        "; ",
        new[] { result.MicAudioPath, result.SystemAudioPath }
            .Where(path => !string.IsNullOrWhiteSpace(path)));
    var meeting = new MeetingItem(
        result.MeetingId,
        result.Title,
        result.StartedAt,
        transcript,
        summary,
        sourceAudioPath,
        _meetingTranscriptionClient.ModelId,
        result.DurationMs,
        _selectedMeetingFolderId,
        CountWords(transcript),
        SelectedSummaryTemplate,
        HealthWarnings: result.HealthWarnings ?? [],
        SessionState: result.SessionState,
        MicrophoneAudioPath: result.MicAudioPath,
        SystemAudioPath: result.SystemAudioPath,
        SystemCaptureMode: result.SystemCaptureMode,
        RecoveredFromInterruption: result.RecoveredFromInterruption,
        LivePreviewModelId: result.LivePreviewModelId,
        LiveTranscriptOwnership: result.LiveTranscriptOwnership,
        FinalTranscriptOwnerModelId: result.FinalTranscriptOwnerModelId,
        GapRecoveryModelId: result.GapRecoveryModelId);
    Meetings.Insert(0, meeting);
    try
    {
        SaveMeetings();
    }
    catch
    {
        Meetings.Remove(meeting);
        throw;
    }
    _meetingRecordingCoordinator.AcknowledgePersisted(result.MeetingId);
    RefreshMeetingViews();
    RefreshSearchResults();
    RefreshRecoverableMeetingSessions();
    if (result.SessionState == MeetingSessionState.Completed)
    {
        meeting = await RunPostMeetingAutomationAsync(
            meeting,
            result.RecoveredFromInterruption
                ? PostMeetingCompletionEvent.RecoveryCompleted
                : PostMeetingCompletionEvent.RecordingCompleted);
    }
    return meeting;
}

private async Task<MeetingItem> RunPostMeetingAutomationAsync(
    MeetingItem meeting,
    PostMeetingCompletionEvent completionEvent)
{
    PostMeetingAutomationResult result;
    try
    {
        PostMeetingAutomationStatusText = PostMeetingHookEnabled || AutoExportMarkdownEnabled
            ? "Running post-meeting automation…"
            : "Automation is disabled; the meeting was saved without launching a process or writing an export.";
        result = await _postMeetingAutomationService.RunAsync(
            meeting,
            CurrentPostMeetingAutomationOptions(),
            completionEvent,
            _applicationShutdownCancellation.Token);
    }
    catch (Exception exception)
    {
        // The meeting was already durably saved and acknowledged. This fallback is diagnostics
        // only; optional automation is never allowed to escape and invalidate completion.
        result = new PostMeetingAutomationResult(
            Guid.NewGuid(),
            PostMeetingAutomationStatus.Failed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0,
            null,
            "",
            "",
            false,
            false,
            $"Automation failed ({exception.GetType().Name}).",
            PostMeetingExportDiagnostic.NotRequested);
    }

    var index = Meetings.ToList().FindIndex(candidate => candidate.Id == meeting.Id);
    var updated = meeting with { AutomationResult = result };
    if (index >= 0)
    {
        Meetings[index] = updated;
        try
        {
            SaveMeetings();
        }
        catch (Exception exception)
        {
            _logService.Info($"Automation diagnostics persistence failed. category={exception.GetType().Name}");
        }
    }

    PostMeetingAutomationStatusText = DescribeAutomationResult(result);
    _logService.Info(
        $"Post-meeting automation finished. status={result.Status}; attempts={result.Attempts}; exitCode={result.ExitCode?.ToString() ?? "none"}; exportRequested={result.Export.Requested}; exportCompleted={result.Export.Completed}; contentLogged=false");
    RefreshMeetingViews();
    RefreshSearchResults();
    return updated;
}

private void RefreshRecoverableMeetingSessions()
{
    _recoverableMeetingSessions.Clear();
    _recoverableMeetingSessions.AddRange(_meetingRecordingCoordinator.DiscoverRecoverableSessions());
    OnPropertyChanged(nameof(RecoverableMeetingCount));
    OnPropertyChanged(nameof(HasRecoverableMeetings));
    OnPropertyChanged(nameof(RecoverInterruptedButtonText));
    if (_recoverableMeetingSessions.Count > 0)
    {
        MeetingSessionStatus = $"{_recoverableMeetingSessions.Count} interrupted recording(s) ready for recovery";
    }
}

private void RecoverInterruptedMeetings_Click(object sender, RoutedEventArgs e) =>
    _ = RecoverInterruptedMeetingsAsync();

private async Task RecoverInterruptedMeetingsAsync()
{
    if (_meetingRecordingCoordinator.IsBusy || _meetingRecordingCoordinator.IsRecording)
    {
        return;
    }
    var pending = _recoverableMeetingSessions.ToList();
    foreach (var recovery in pending)
    {
        _meetingOperationCancellation?.Dispose();
        _meetingOperationCancellation = new CancellationTokenSource();
        try
        {
            DictationStatus = "Recovering interrupted meeting";
            _toastNotificationService.Show("Recovering meeting", "Finalizing retained local audio", ToastState.Transcribing, 0);
            var result = await _meetingRecordingCoordinator.FinalizeRecoverableAsync(
                recovery,
                _meetingOperationCancellation.Token);
            await PersistRecordedMeetingAsync(result);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception exception)
        {
            _logService.Info($"Interrupted meeting recovery failed. category={exception.GetType().Name}");
            _toastNotificationService.Show(
                "Recovery needs attention",
                "The retained audio remains available for another retry",
                ToastState.Error,
                4600);
            break;
        }
    }
    RefreshRecoverableMeetingSessions();
    if (_recoverableMeetingSessions.Count == 0)
    {
        DictationStatus = "Interrupted meeting recovery complete";
        _toastNotificationService.Show("Meeting recovery complete", "Recovered recordings are in Meetings", ToastState.Success);
    }
}

private void OnMeetingSessionStateChanged(object? sender, MeetingSessionStateChangedEventArgs e)
{
    Dispatcher.BeginInvoke(() =>
    {
        var state = e.Transition.To;
        _isMeetingRecording = state is MeetingSessionState.Recording or MeetingSessionState.DegradedRecording;
        MeetingSessionStatus = state switch
        {
            MeetingSessionState.Idle => "Idle",
            MeetingSessionState.Preparing => "Preparing microphone and meeting audio",
            MeetingSessionState.Recording => "Recording microphone and meeting audio",
            MeetingSessionState.DegradedRecording => "Recording with an audio warning",
            MeetingSessionState.Stopping => "Stopping capture safely",
            MeetingSessionState.Finalizing => "Finalizing local transcript",
            MeetingSessionState.Completed => "Meeting completed",
            MeetingSessionState.Failed => "Meeting needs attention",
            MeetingSessionState.Cancelled => "Meeting cancelled",
            MeetingSessionState.RecoverableInterruption => "Recording retained for recovery",
            _ => state.ToString()
        };
        OnPropertyChanged(nameof(MeetingRecordingButtonText));
    });
}

private void OnMeetingAudioHealthChanged(object? sender, MeetingAudioHealthChangedEventArgs e)
{
    if (!e.Snapshot.IsDegraded)
    {
        return;
    }
    Dispatcher.BeginInvoke(() =>
    {
        MeetingSessionStatus = e.Snapshot.Warnings.FirstOrDefault() ?? "Recording with an audio warning";
    });
}

private void OnMeetingRecordingLevelChanged(object? sender, AudioLevelEventArgs e) =>
    Dispatcher.BeginInvoke(() => _toastNotificationService.UpdateRecordingLevel(e.Peak));

private void OnLiveTranscriptChanged(object? sender, LiveTranscriptSnapshot snapshot)
{
    Dispatcher.BeginInvoke(() =>
    {
        _liveTranscriptWindow ??= new MeetingLiveTranscriptWindow(ShowLiveWaveformOnHover) { Owner = _shell.Window };
        _liveTranscriptWindow.Update(snapshot);
        if (!_liveTranscriptWindow.IsVisible) _liveTranscriptWindow.Show();
    });
}

private void OnLiveTranscriptionFailed(object? sender, Exception exception)
{
    Dispatcher.BeginInvoke(() =>
    {
        MeetingSessionStatus = "Live preview stopped; retained audio is still recording";
        _toastNotificationService.Show("Live transcript stopped", "Retained audio will still be finalized locally", ToastState.Error, 4200);
        _liveTranscriptWindow?.Hide();
    });
}

private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e) =>
    _ = HandlePowerModeChangedAsync(e);

private async Task HandlePowerModeChangedAsync(PowerModeChangedEventArgs e)
{
    try
    {
        if (e.Mode == PowerModes.Suspend)
        {
            await _meetingRecordingCoordinator.SuspendAsync();
            return;
        }
        if (e.Mode == PowerModes.Resume &&
            _meetingRecordingCoordinator.State == MeetingSessionState.RecoverableInterruption)
        {
            await _meetingRecordingCoordinator.ResumeAsync();
            _ = Dispatcher.BeginInvoke(() =>
            {
                _isMeetingRecording = _meetingRecordingCoordinator.IsRecording;
                OnPropertyChanged(nameof(MeetingRecordingButtonText));
                _toastNotificationService.Show(
                    _isMeetingRecording ? "Meeting recording resumed" : "Meeting retained for recovery",
                    _isMeetingRecording ? "Microphone and meeting audio restarted" : "Capture could not restart automatically",
                    _isMeetingRecording ? ToastState.Recording : ToastState.Error,
                    _isMeetingRecording ? 0 : 4200);
            });
        }
    }
    catch (Exception exception)
    {
        _logService.Info($"Meeting power-transition handling failed. category={exception.GetType().Name}");
        RefreshRecoverableMeetingSessions();
    }
}

private void RefreshMeetingPlaybackTracks(MeetingItem item)
{
    _meetingPlaybackTimer.Stop();
    _meetingPlaybackService.Close();
    MeetingPlaybackTracks.Clear();
    foreach (var track in MeetingRecordingPlaybackService.SelectTracks(
                 item.MicrophoneAudioPath,
                 item.SystemAudioPath,
                 item.SourcePath))
    {
        MeetingPlaybackTracks.Add(track);
    }
    OnPropertyChanged(nameof(HasMeetingPlayback));
    SelectedMeetingPlaybackTrack = MeetingPlaybackTracks.FirstOrDefault();
    if (SelectedMeetingPlaybackTrack is null)
    {
        MeetingPlaybackPosition = 0;
        MeetingPlaybackDuration = 0;
        OnPropertyChanged(nameof(MeetingPlaybackTimeLabel));
    }
}

private void ToggleMeetingPlayback_Click(object sender, RoutedEventArgs e)
{
    try
    {
        if (_meetingPlaybackService.State == MeetingPlaybackState.Playing)
        {
            _meetingPlaybackService.Pause();
            _meetingPlaybackTimer.Stop();
        }
        else
        {
            _meetingPlaybackService.Play();
            _meetingPlaybackTimer.Start();
        }
        OnPropertyChanged(nameof(MeetingPlaybackButtonText));
    }
    catch (Exception exception)
    {
        DictationStatus = $"Playback failed: {exception.Message}";
        _toastNotificationService.Show("Playback failed", "The selected track could not play", ToastState.Error, 3600);
    }
}

private void MeetingPlaybackSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
{
    if (_updatingMeetingPlaybackPosition || _meetingPlaybackService.State == MeetingPlaybackState.Empty)
    {
        return;
    }
    _meetingPlaybackService.Seek(TimeSpan.FromSeconds(Math.Max(0, e.NewValue)));
    OnPropertyChanged(nameof(MeetingPlaybackTimeLabel));
}

private void MeetingPlaybackTimer_Tick(object? sender, EventArgs e)
{
    _updatingMeetingPlaybackPosition = true;
    MeetingPlaybackPosition = _meetingPlaybackService.Position.TotalSeconds;
    MeetingPlaybackDuration = _meetingPlaybackService.Duration.TotalSeconds;
    _updatingMeetingPlaybackPosition = false;
    OnPropertyChanged(nameof(MeetingPlaybackTimeLabel));
    if (_meetingPlaybackService.State != MeetingPlaybackState.Playing)
    {
        _meetingPlaybackTimer.Stop();
    }
}

private void OnMeetingPlaybackStateChanged(object? sender, MeetingPlaybackStateChangedEventArgs e)
{
    Dispatcher.BeginInvoke(() =>
    {
        OnPropertyChanged(nameof(MeetingPlaybackButtonText));
        if (e.State == MeetingPlaybackState.Failed)
        {
            _meetingPlaybackTimer.Stop();
            _toastNotificationService.Show("Playback stopped", "Windows audio playback failed", ToastState.Error, 3600);
        }
    });
}

private static string FormatPlaybackTime(TimeSpan value) =>
    value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
private void ToggleMeetings_Click(object sender, RoutedEventArgs e)
{
    _meetingsExpanded = !_meetingsExpanded;
    _appServices.Navigation.SetMeetingsExpanded(_meetingsExpanded);
    MeetingsChildren.Visibility = _meetingsExpanded ? Visibility.Visible : Visibility.Collapsed;
    OnPropertyChanged(nameof(MeetingsChevron));
    ShowPage(MeetingsPage, MeetingsNav);
}
private void AddMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    var baseName = "New Folder";
    var index = 1;
    var name = baseName;
    while (MeetingFolders.Any(folder => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
    {
        index++;
        name = $"{baseName} {index}";
    }
    var folder = new MeetingFolderItem($"folder_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", name);
    MeetingFolders.Add(folder);
    SaveActiveSpeakerAliases();
    _selectedMeetingFolderId = folder.Id;
    _selectedMeeting = null;
    MeetingsBrowserView.Visibility = Visibility.Visible;
    MeetingDetailView.Visibility = Visibility.Collapsed;
    SaveMeetingFolders();
    RefreshMeetingViews();
    ShowPage(MeetingsPage, MeetingsNav);
}
private void ManageTemplates_Click(object sender, RoutedEventArgs e)
{
    var window = new Window
    {
        Owner = _shell.Window,
        Title = "Manage Templates",
        Width = 760,
        Height = 560,
        MinWidth = 680,
        MinHeight = 480,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBaseBrush"),
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        FontFamily = _shell.Window.FontFamily,
        Content = BuildTemplatesManagerContent()
    };
    window.ShowDialog();
}
private FrameworkElement BuildTemplatesManagerContent()
{
    var root = new DockPanel { Margin = new Thickness(24) };
    var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
    DockPanel.SetDock(header, Dock.Top);
    root.Children.Add(header);
    var done = new WpfButton
    {
        Content = "Done",
        Style = (Style)FindResource("SecondaryButton"),
        Width = 88,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right
    };
    done.Click += (_, _) => Window.GetWindow(done)?.Close();
    DockPanel.SetDock(done, Dock.Right);
    header.Children.Add(done);
    var create = new WpfButton
    {
        Content = "+ New template",
        Style = (Style)FindResource("SecondaryButton"),
        Width = 128,
        Margin = new Thickness(0, 0, 8, 0),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right
    };
    DockPanel.SetDock(create, Dock.Right);
    header.Children.Add(create);
    var titleStack = new StackPanel();
    titleStack.Children.Add(new TextBlock
    {
        Text = "Manage Templates",
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
    });
    titleStack.Children.Add(new TextBlock
    {
        Text = "Create reusable prompt-based note formats for meetings.",
        Margin = new Thickness(0, 4, 0, 0),
        FontSize = 13,
        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
    });
    header.Children.Add(titleStack);
    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
    root.Children.Add(grid);
    var templates = new WpfListBox
    {
        ItemsSource = CustomMeetingTemplates,
        Background = System.Windows.Media.Brushes.Transparent,
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        BorderThickness = new Thickness(0),
        MinHeight = 360,
        ItemContainerStyle = (Style)FindResource("MuesliListBoxItem")
    };
    templates.ItemTemplate = BuildMeetingTemplateItemTemplate();
    var listWrap = new Border
    {
        Background = (System.Windows.Media.Brush)FindResource("BackgroundRaisedBrush"),
        BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Child = templates
    };
    Grid.SetColumn(listWrap, 0);
    grid.Children.Add(listWrap);
    var form = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
    Grid.SetColumn(form, 1);
    grid.Children.Add(form);
    form.Children.Add(new TextBlock
    {
        Text = "TEMPLATE",
        Style = (Style)FindResource("SectionLabel")
    });
    var editorCard = new Border
    {
        Background = (System.Windows.Media.Brush)FindResource("BackgroundRaisedBrush"),
        BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16)
    };
    form.Children.Add(editorCard);
    var editor = new StackPanel();
    editorCard.Child = editor;
    var nameBox = new WpfTextBox
    {
        Style = (Style)FindResource("MuesliTextBox"),
        Height = 34
    };
    var promptBox = new WpfTextBox
    {
        Style = (Style)FindResource("MuesliTextBox"),
        Margin = new Thickness(0, 6, 0, 0),
        MinHeight = 180,
        Padding = new Thickness(10),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    editor.Children.Add(new TextBlock { Text = "Name", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
    editor.Children.Add(nameBox);
    editor.Children.Add(new TextBlock { Text = "Prompt", Margin = new Thickness(0, 12, 0, 0), FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
    editor.Children.Add(promptBox);
    templates.SelectionChanged += (_, _) =>
    {
        if (templates.SelectedItem is not MeetingTemplateItem template)
        {
            return;
        }
        nameBox.Text = template.Name;
        promptBox.Text = template.Prompt;
    };
    var actions = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
    editor.Children.Add(actions);
    create.Click += (_, _) =>
    {
        nameBox.Text = "";
        promptBox.Text = "";
        templates.SelectedItem = null;
        nameBox.Focus();
    };
    var cancel = new WpfButton { Content = "Cancel", Style = (Style)FindResource("GhostButton") };
    cancel.Click += (_, _) =>
    {
        nameBox.Text = "";
        promptBox.Text = "";
        templates.SelectedItem = null;
    };
    actions.Children.Add(cancel);
    var save = new WpfButton { Content = "Save changes", Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("SecondaryButton") };
    save.Click += (_, _) =>
    {
        var name = nameBox.Text.Trim();
        var prompt = promptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt))
        {
            _toastNotificationService.Show("Template needs name and prompt", "Enter both fields", ToastState.Error, 3200);
            return;
        }
        if (templates.SelectedItem is MeetingTemplateItem existing)
        {
            existing.Name = name;
            existing.Prompt = prompt;
            templates.Items.Refresh();
        }
        else
        {
            var item = new MeetingTemplateItem(name, prompt);
            CustomMeetingTemplates.Add(item);
            templates.SelectedItem = item;
        }
        SaveMeetingTemplates();
        DictationStatus = "Template saved";
    };
    actions.Children.Add(save);
    var delete = new WpfButton { Content = "Delete", Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("GhostButton"), Foreground = System.Windows.Media.Brushes.IndianRed };
    delete.Click += (_, _) =>
    {
        if (templates.SelectedItem is not MeetingTemplateItem selected)
        {
            return;
        }
        CustomMeetingTemplates.Remove(selected);
        SummaryTemplates.Remove(selected.Name);
        SaveMeetingTemplates();
        nameBox.Text = "";
        promptBox.Text = "";
    };
    actions.Children.Add(delete);
    if (CustomMeetingTemplates.Count == 0)
    {
        nameBox.Text = "";
        promptBox.Text = "";
    }
    else
    {
        templates.SelectedIndex = 0;
    }
    return root;
}
private static DataTemplate BuildMeetingTemplateItemTemplate()
{
    var template = new DataTemplate(typeof(MeetingTemplateItem));
    var border = new FrameworkElementFactory(typeof(Border));
    border.SetValue(Border.PaddingProperty, new Thickness(12));
    border.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 8));
    border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
    border.SetResourceReference(Border.BackgroundProperty, "BackgroundRaisedBrush");
    border.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
    border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
    var stack = new FrameworkElementFactory(typeof(StackPanel));
    stack.SetValue(StackPanel.OrientationProperty, WpfOrientation.Vertical);
    border.AppendChild(stack);
    var title = new FrameworkElementFactory(typeof(TextBlock));
    title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MeetingTemplateItem.Name)));
    title.SetValue(TextBlock.FontSizeProperty, 12.0);
    title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
    title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
    stack.AppendChild(title);
    var prompt = new FrameworkElementFactory(typeof(TextBlock));
    prompt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MeetingTemplateItem.Prompt)));
    prompt.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
    prompt.SetValue(TextBlock.FontSizeProperty, 12.0);
    prompt.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
    prompt.SetValue(TextBlock.MaxHeightProperty, 38.0);
    prompt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
    prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
    stack.AppendChild(prompt);
    template.VisualTree = border;
    return template;
}
private void SelectMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    _selectedMeetingFolderId = folder.Id;
    _selectedMeeting = null;
    MeetingsBrowserView.Visibility = Visibility.Visible;
    MeetingDetailView.Visibility = Visibility.Collapsed;
    RefreshMeetingViews();
    ShowPage(MeetingsPage, MeetingsNav);
}
private void RenameMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    folder.IsRenaming = true;
}
private void FolderNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    if (e.Key == Key.Enter)
    {
        CommitFolderRename(folder);
        e.Handled = true;
    }
    else if (e.Key == Key.Escape)
    {
        folder.IsRenaming = false;
        e.Handled = true;
    }
}
private void FolderNameBox_LostFocus(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingFolderItem folder } && folder.IsRenaming)
    {
        CommitFolderRename(folder);
    }
}
private void CommitFolderRename(MeetingFolderItem folder)
{
    folder.Name = string.IsNullOrWhiteSpace(folder.Name) ? "New Folder" : folder.Name.Trim();
    folder.IsRenaming = false;
    SaveMeetingFolders();
    RefreshMeetingViews();
}
private void MoveMeetingFolderUp_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        MoveMeetingFolder(folder, -1);
    }
}
private void MoveMeetingFolderDown_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        MoveMeetingFolder(folder, 1);
    }
}
private void MoveMeetingFolder(MeetingFolderItem folder, int direction)
{
    var oldIndex = MeetingFolders.IndexOf(folder);
    var newIndex = oldIndex + direction;
    if (oldIndex < 0 || newIndex < 0 || newIndex >= MeetingFolders.Count)
    {
        return;
    }
    MeetingFolders.Move(oldIndex, newIndex);
    SaveMeetingFolders();
    RefreshMeetingViews();
}
private void DeleteMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    var result = System.Windows.MessageBox.Show(
        _shell.Window,
        "Delete this folder? Meetings inside it will stay in All Meetings.",
        "Delete folder",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);
    if (result != MessageBoxResult.Yes)
    {
        return;
    }
    MeetingFolders.Remove(folder);
    for (var index = 0; index < Meetings.Count; index++)
    {
        if (string.Equals(Meetings[index].FolderId, folder.Id, StringComparison.Ordinal))
        {
            Meetings[index] = Meetings[index] with { FolderId = null };
        }
    }
    if (string.Equals(_selectedMeetingFolderId, folder.Id, StringComparison.Ordinal))
    {
        _selectedMeetingFolderId = null;
    }
    SaveMeetingFolders();
    SaveMeetings();
    RefreshMeetingViews();
}
private async Task<string> CreateMeetingSummaryAsync(
    string transcript,
    string title,
    CancellationToken cancellationToken = default)
{
    DictationStatus = SelectedSummaryProvider == "local" ? "Creating local summary" : "Creating AI summary";
    _toastNotificationService.Show("Summarizing meeting", SelectedSummaryProvider, ToastState.Transcribing, 0);
    return await CreateMeetingSummaryWithSettingsAsync(transcript, title, CurrentSettingsSnapshot(), cancellationToken);
}

private async Task<string> CreateMeetingSummaryWithSettingsAsync(
    string transcript,
    string title,
    MuesliSettings settings,
    CancellationToken cancellationToken = default)
{
    if (!await _summaryGate.WaitAsync(0, cancellationToken))
    {
        throw new InvalidOperationException("A meeting summary request is already in progress.");
    }
    try
    {
        _lastSummaryUsedLocalFallback = false;
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            transcript,
            title,
            settings,
            cancellationToken);
        if (result.UsedLocalFallback)
        {
            _lastSummaryUsedLocalFallback = true;
            DictationStatus = $"{result.Provider} failed; local summary used";
            _toastNotificationService.Show(
                "Local summary used",
                $"{result.Provider} could not create notes. Your transcript was preserved.",
                ToastState.Error,
                5200);
            _logService.Info(
                $"Cloud summary fallback. provider={result.Provider}; reason={result.SafeFailureReason ?? "provider-error"}; localFallback=true");
        }
        return result.Summary;
    }
    finally
    {
        _summaryGate.Release();
    }
}

private void OnMeetingDetected(object? sender, DetectedMeeting meeting)
{
    if (!AutoMeetingDetectionEnabled)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but AutoDetection is disabled.");
        return;
    }
    if (_isMeetingRecording)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but already recording.");
        return;
    }
    if (_meetingRecordingCoordinator.IsBusy)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but coordinator is busy.");
        return;
    }
    if (_meetingPromptService.IsVisible)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but prompt is already visible — resetting.");
        _meetingPromptService.Reset();
        return;
    }
    if (_ignoredMeetingPrompts.TryGetValue(meeting.Key, out var ignoredUntil) && ignoredUntil > DateTime.Now)
    {
        var remaining = (ignoredUntil - DateTime.Now).TotalSeconds;
        _logService.Info($"Meeting detected ({meeting.Platform}) but prompt ignored for {remaining:F0}s more.");
        return;
    }

    _meetingPromptService.Show(
        meeting,
        () => Dispatcher.InvokeAsync(() => ToggleMeetingRecordingAsync(meeting)),
        () =>
        {
            _ignoredMeetingPrompts[meeting.Key] = DateTime.Now.AddMinutes(30);
            _meetingDetectionService.DismissCandidate(meeting.Key);
            DictationStatus = "Meeting prompt dismissed";
        });
}

private void OnMeetingDetectionScanCompleted(object? sender, MeetingDetectionScan scan)
{
    Dispatcher.Invoke(() =>
    {
        _lastMeetingDetectionScan = scan;
        MeetingDetectionStatus = scan.Found
            ? scan.Summary
            : $"No meeting found. {scan.Summary}";
    });
}

private void CheckMeetingDetection_Click(object sender, RoutedEventArgs e)
{
    AutoMeetingDetectionEnabled = true;
    var scan = _meetingDetectionService.CheckNow();
    _lastMeetingDetectionScan = scan;
    MeetingDetectionStatus = scan.Found
        ? scan.Summary
        : $"No meeting found. {scan.Summary}";
    DictationStatus = scan.Found ? "Meeting detected" : "No meeting detected";
}
}
