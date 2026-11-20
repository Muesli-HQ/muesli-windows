using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class DictionaryPageViewModel(
    WinUiLibraryContext library,
    WinUiSettingsContext settings,
    IFilePickerService filePickers,
    IAppDialogService dialogs,
    WinUiDictionarySuggestionService suggestionService) : ObservableObject
{
    /// <summary>macOS DictionaryRowMetrics.suggestionPageSize.</summary>
    private const int SuggestionPageSize = 10;
    private readonly DictionaryExchangeService _exchange = new();
    private IReadOnlyList<DictionarySuggestion> _allSuggestions = [];
    private bool _loading;

    [ObservableProperty] public partial IReadOnlyList<DictionaryListItem> Items { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DictionarySuggestion> Suggestions { get; private set; } = [];
    [ObservableProperty] public partial bool SuggestionsEnabled { get; set; }
    [ObservableProperty] public partial string EditingId { get; private set; } = "";
    [ObservableProperty] public partial string Phrase { get; set; } = "";
    [ObservableProperty] public partial string Replacement { get; set; } = "";
    [ObservableProperty] public partial double MatchingThreshold { get; set; } = .85;
    [ObservableProperty] public partial string ValidationMessage { get; private set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial int SuggestionPage { get; private set; }
    [ObservableProperty] public partial string SuggestionRangeText { get; private set; } = "";
    [ObservableProperty] public partial bool HasPreviousPage { get; private set; }
    [ObservableProperty] public partial bool HasNextPage { get; private set; }

    public bool HasItems => Items.Count > 0;
    /// <summary>
    /// Whether the editor should show its inline validation line. The page-level InfoBar still
    /// announces the same text, but at 720 DIP the editor sits below the list and off-screen from
    /// that bar, so a rejected Save had no visible cause next to the field that caused it.
    /// </summary>
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public bool HasSuggestions => _allSuggestions.Count > 0;
    public bool HasSuggestionPages => _allSuggestions.Count > SuggestionPageSize;

    // P7-04. The slider's bounds are the matcher's own clamp, not a presentation choice:
    // DictionaryCorrectionService.ClampThreshold pins every stored value into this range.
    public double ThresholdMinimum => DictionaryCorrectionService.MinimumThreshold;
    public double ThresholdMaximum => DictionaryCorrectionService.MaximumThreshold;
    public string ThresholdHelp =>
        $"How close a spoken word must score against this phrase before Muesli replaces it. " +
        $"Muesli matches between {ThresholdMinimum:P0} and {ThresholdMaximum:P0}; words of six " +
        "characters or fewer are held to a stricter score than this setting, and words of three " +
        "characters or fewer must match exactly.";

    public void Load() => Reload();

    public void Select(DictionaryListItem item)
    {
        ValidationMessage = "";
        EditingId = item.Id;
        Phrase = item.Phrase;
        Replacement = item.Replacement;
        MatchingThreshold = item.MatchingThreshold;
    }

    /// <summary>macOS saves the switch at once; turning it off stops any watch in progress.</summary>
    partial void OnSuggestionsEnabledChanged(bool value)
    {
        if (!_loading) suggestionService.SetEnabled(value);
    }
    partial void OnItemsChanged(IReadOnlyList<DictionaryListItem> value) => OnPropertyChanged(nameof(HasItems));
    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));
    partial void OnPhraseChanged(string value) => ValidationMessage = "";
    partial void OnEditingIdChanged(string value) => DeleteEntryCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void NewEntry()
    {
        ValidationMessage = "";
        EditingId = "";
        Phrase = "";
        Replacement = "";
        MatchingThreshold = .85;
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (!HasPreviousPage) return;
        SuggestionPage--;
        ApplySuggestionPage();
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!HasNextPage) return;
        SuggestionPage++;
        ApplySuggestionPage();
    }

    [RelayCommand]
    private void AcceptSuggestion(DictionarySuggestion? suggestion)
    {
        if (suggestion is null) return;
        suggestionService.Accept(suggestion);
        Reload();
    }

    [RelayCommand]
    private void DismissSuggestion(DictionarySuggestion? suggestion)
    {
        if (suggestion is null) return;
        suggestionService.Ignore(suggestion);
        Reload();
    }

    [RelayCommand]
    private void SaveEntry()
    {
        var phrase = Phrase.Trim();
        if (phrase.Length == 0)
        {
            Reject("Enter the word or phrase Muesli should recognize.");
            return;
        }
        var entries = library.LoadDictionary().ToList();
        if (entries.Any(entry =>
                !string.Equals(entry.Id, EditingId, StringComparison.Ordinal) &&
                string.Equals(entry.Phrase.Trim(), phrase, StringComparison.OrdinalIgnoreCase)))
        {
            Reject("That phrase already exists in the dictionary.");
            return;
        }

        ValidationMessage = "";
        IsStatusOpen = false;

        var record = new DictionaryEntryRecord
        {
            Id = string.IsNullOrWhiteSpace(EditingId) ? Guid.NewGuid().ToString("N") : EditingId,
            Phrase = phrase,
            Replacement = Replacement.Trim(),
            MatchingThreshold = Math.Clamp(MatchingThreshold, 0, 1)
        };
        var index = entries.FindIndex(entry => string.Equals(entry.Id, record.Id, StringComparison.Ordinal));
        if (index >= 0) entries[index] = record;
        else entries.Add(record);
        library.SaveDictionary(entries);
        EditingId = record.Id;
        Reload();
        Select(DictionaryListItem.From(record));
    }

    /// <summary>macOS removes the word straight away, without a confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteEntry))]
    private void DeleteEntry()
    {
        if (!CanDeleteEntry()) return;
        library.SaveDictionary(library.LoadDictionary()
            .Where(entry => !string.Equals(entry.Id, EditingId, StringComparison.Ordinal))
            .ToList());
        NewEntry();
        Reload();
    }

    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        string message;
        try
        {
            var picked = await filePickers.PickSaveFileAsync(
                new FilePickerRequest("Export Muesli Dictionary", [".json"], "muesli-dictionary.json"),
                cancellationToken);
            if (picked is null) return;
            var entries = library.LoadDictionary();
            _exchange.Export(picked.Path, entries);
            message = $"Exported {entries.Count} dictionary entries.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            message = $"Could not export the dictionary. {exception.Message}";
        }
        await dialogs.ShowInfoAsync(message, "Dictionary", CancellationToken.None);
    }

    /// <summary>macOS merges the file straight in (new words added, matching words updated) and reports the counts.</summary>
    [RelayCommand]
    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        string message;
        try
        {
            var picked = await filePickers.PickOpenFileAsync(
                new FilePickerRequest("Import Muesli Dictionary", [".json"]),
                cancellationToken);
            if (picked is null) return;
            var existing = library.LoadDictionary();
            var preview = _exchange.PreviewImport(picked.Path, existing);
            if (!preview.IsValid)
            {
                message = "Could not import the dictionary. " + string.Join(" ", preview.Errors);
            }
            else if (preview.Additions.Count + preview.Conflicts.Count == 0)
            {
                message = "All dictionary entries were already present.";
            }
            else
            {
                library.SaveDictionary(DictionaryExchangeService.Merge(existing, preview, replaceConflicts: true));
                NewEntry();
                Reload();
                message = $"Imported {preview.Additions.Count} new, updated {preview.Conflicts.Count} dictionary entries.";
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            message = $"Could not import the dictionary. {exception.Message}";
        }
        await dialogs.ShowInfoAsync(message, "Dictionary", CancellationToken.None);
    }

    /// <summary>Re-reads entries and suggestions, for example after a prompt added one elsewhere.</summary>
    public void Reload()
    {
        // macOS lists every word, in the order they were added.
        Items = library.LoadDictionary().Select(DictionaryListItem.From).ToList();
        var current = settings.Load();
        _loading = true;
        SuggestionsEnabled = current.EnableDictionaryCorrectionPrompts;
        _loading = false;
        _allSuggestions = current.DictionarySuggestions ?? [];
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(HasSuggestionPages));
        ApplySuggestionPage();
    }

    private void ApplySuggestionPage()
    {
        var count = _allSuggestions.Count;
        var pages = Math.Max(1, (count + SuggestionPageSize - 1) / SuggestionPageSize);
        SuggestionPage = Math.Clamp(SuggestionPage, 0, pages - 1);
        var start = SuggestionPage * SuggestionPageSize;
        Suggestions = _allSuggestions.Skip(start).Take(SuggestionPageSize).ToList();
        SuggestionRangeText = count == 0 ? "" : $"{start + 1}-{Math.Min(start + SuggestionPageSize, count)} of {count}";
        HasPreviousPage = SuggestionPage > 0;
        HasNextPage = SuggestionPage + 1 < pages;
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanDeleteEntry() => !string.IsNullOrWhiteSpace(EditingId);

    /// <summary>
    /// Refuses a save: the same text is shown inline beside the field and announced on the page's
    /// status bar, so the reason is visible whether or not the bar is on screen.
    /// </summary>
    private void Reject(string message)
    {
        ValidationMessage = message;
        StatusMessage = message;
        IsStatusOpen = true;
    }
}

public sealed record DictionaryListItem(
    string Id,
    string Phrase,
    string Replacement,
    double MatchingThreshold)
{
    public string ReplacementLabel => string.IsNullOrWhiteSpace(Replacement) ? "Recognize as spoken" : $"Replace with {Replacement}";
    public string ThresholdLabel => $"{MatchingThreshold:P0} match";

    public static DictionaryListItem From(DictionaryEntryRecord entry) => new(
        entry.Id,
        entry.Phrase,
        entry.Replacement,
        entry.MatchingThreshold);
}
