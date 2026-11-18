using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Muesli.Windows.Services;
using Windows.System;

namespace Muesli.Windows.WinUI.Controls;

/// <summary>Compact presentation only; validation, conflict resolution and persistence stay in Core.</summary>
public sealed partial class DictationCorrectionEditor : UserControl
{
    private readonly Func<string, bool, IReadOnlyList<DictionaryEntryRecord>?, DictationCorrectionResult> _save;
    private IReadOnlyList<DictionaryEntryRecord>? _conflicts;
    private bool _saving;

    public event Action<bool>? CloseRequested;

    public DictationCorrectionEditor(string original,
        Func<string, bool, IReadOnlyList<DictionaryEntryRecord>?, DictationCorrectionResult> save)
    {
        _save = save;
        InitializeComponent();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(ReplacementBox, $"Replacing {original}. Press Enter to save.");
        ReplacementBox.Text = original;
    }

    public void FocusReplacement()
    {
        ReplacementBox.Focus(FocusState.Programmatic);
        ReplacementBox.SelectAll();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(false);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Save();
        }
    }

    private void Save()
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var result = _save(ReplacementBox.Text, RememberBox.IsChecked == true,
                ReplaceConflictBox.IsChecked == true ? _conflicts : null);
            if (result.Succeeded || result.Outcome == DictationCorrectionOutcome.PartialFailure)
            {
                CloseRequested?.Invoke(true);
                return;
            }
            FeedbackText.Text = result.Message;
            FeedbackText.Visibility = Visibility.Visible;
            if (result.Outcome == DictationCorrectionOutcome.Conflict)
            {
                _conflicts = result.Conflicts;
                ReplaceConflictBox.IsChecked = false;
                ReplaceConflictBox.Visibility = Visibility.Visible;
                FeedbackText.Text += "\nCurrent replacement: " + string.Join("; ", _conflicts!.Select(entry => entry.Replacement));
            }
        }
        finally { _saving = false; }
    }

    private void Replacement_Changed(object sender, TextChangedEventArgs e) => ClearFeedback();

    private void ClearFeedback()
    {
        // TextChanged can fire while the XAML tree is still being constructed.
        if (ReplaceConflictBox is null || FeedbackText is null) return;
        _conflicts = null;
        ReplaceConflictBox.IsChecked = false;
        ReplaceConflictBox.Visibility = Visibility.Collapsed;
        FeedbackText.Visibility = Visibility.Collapsed;
    }

    private void Remember_Unchecked(object sender, RoutedEventArgs e) => ClearFeedback();
}
