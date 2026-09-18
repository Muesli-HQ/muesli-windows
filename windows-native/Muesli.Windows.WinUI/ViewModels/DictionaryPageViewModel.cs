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
    IAppDialogService dialogs) : ObservableObject
{
    private const int PageSize = 25;
    private readonly DictionaryExchangeService _exchange = new();
    private IReadOnlyList<DictionaryListItem> _allItems = [];

    [ObservableProperty] public partial IReadOnlyList<DictionaryListItem> Items { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DictionarySuggestion> Suggestions { get; private set; } = [];
    [ObservableProperty] public partial bool SuggestionsEnabled { get; set; } = true;
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string EditingId { get; private set; } = "";
    [ObservableProperty] public partial string Phrase { get; set; } = "";
    [ObservableProperty] public partial string Replacement { get; set; } = "";
    [ObservableProperty] public partial double MatchingThreshold { get; set; } = .85;
    [ObservableProperty] public partial string ValidationMessage { get; private set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }
    [ObservableProperty] public partial int PageIndex { get; private set; }
    [ObservableProperty] public partial string PageLabel { get; private set; } = "Page 1 of 1";
    [ObservableProperty] public partial bool HasPreviousPage { get; private set; }
    [ObservableProperty] public partial bool HasNextPage { get; private set; }

    public bool HasItems => Items.Count > 0;
    /// <summary>
    /// Whether the editor should show its inline validation line. The page-level InfoBar still
    /// announces the same text, but at 720 DIP the editor sits below the list and off-screen from
    /// that bar, so a rejected Save had no visible cause next to the field that caused it.
    /// </summary>
    public bool HasValidationMessage => ValidationMessage.Length > 0;
    public bool HasSuggestions => SuggestionsEnabled && Suggestions.Count > 0;
    public bool IsEditing => !string.IsNullOrWhiteSpace(EditingId);
    public bool ShowEmptyAddAction => !HasItems && string.IsNullOrWhiteSpace(SearchText);
    public string EditorTitle => IsEditing ? "Edit dictionary entry" : "Add dictionary entry";
    public string ThresholdLabel => $"{MatchingThreshold:P0} match";

    // P7-04. The slider's bounds are the matcher's own clamp, not a presentation choice:
    // DictionaryCorrectionService.ClampThreshold pins every stored value into this range, so the
    // scale is read from there rather than restated in XAML. Showing the two endpoints is what
    // makes the default (the minimum) legible instead of looking like a thumb stuck at the left.
    public double ThresholdMinimum => DictionaryCorrectionService.MinimumThreshold;
    public double ThresholdMaximum => DictionaryCorrectionService.MaximumThreshold;
    public string ThresholdMinimumLabel => $"{ThresholdMinimum:P0} · broadest";
    public string ThresholdMaximumLabel => $"{ThresholdMaximum:P0} · strictest";
    public string ThresholdHelp =>
        $"How close a spoken word must score against this phrase before Muesli replaces it. " +
        $"Muesli matches between {ThresholdMinimum:P0} and {ThresholdMaximum:P0}; words of six " +
        "characters or fewer are held to a stricter score than this setting, and words of three " +
        "characters or fewer must match exactly.";
    public string EmptyStateTitle => string.IsNullOrWhiteSpace(SearchText)
        ? "No custom dictionary entries yet"
        : "No dictionary entries match your search";
    public string EmptyStateInstruction => string.IsNullOrWhiteSpace(SearchText)
        ? "Add a word or import a versioned JSON dictionary."
        : "Try a different phrase or clear the search box.";

    public void Load() => Reload();

    public void Select(DictionaryListItem item)
    {
        ValidationMessage = "";
        EditingId = item.Id;
        Phrase = item.Phrase;
        Replacement = item.Replacement;
        MatchingThreshold = item.MatchingThreshold;
    }

    partial void OnSearchTextChanged(string value)
    {
        PageIndex = 0;
        ApplyFilter();
        NotifyEmptyState();
    }
    partial void OnSuggestionsEnabledChanged(bool value) => OnPropertyChanged(nameof(HasSuggestions));
    partial void OnItemsChanged(IReadOnlyList<DictionaryListItem> value)
    {
        OnPropertyChanged(nameof(HasItems));
        NotifyEmptyState();
    }
    partial void OnMatchingThresholdChanged(double value) => OnPropertyChanged(nameof(ThresholdLabel));
    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasValidationMessage));
    partial void OnPhraseChanged(string value) => ValidationMessage = "";
    partial void OnEditingIdChanged(string value)
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(IsEditing));
        DeleteEntryCommand.NotifyCanExecuteChanged();
    }

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
        PageIndex--;
        ApplyFilter();
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!HasNextPage) return;
        PageIndex++;
        ApplyFilter();
    }

    [RelayCommand]
    private void AcceptSuggestion(DictionarySuggestion? suggestion)
    {
        if (suggestion is null) return;
        var entries = library.LoadDictionary().ToList();
        if (!entries.Any(entry =>
                string.Equals(entry.Phrase, suggestion.Observed, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Replacement, suggestion.Replacement, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(new DictionaryEntryRecord
            {
                Id = $"dict_{Guid.NewGuid():N}",
                Phrase = suggestion.Observed,
                Replacement = suggestion.Replacement,
                MatchingThreshold = .90
            });
            library.SaveDictionary(entries);
        }
        RemoveSuggestion(suggestion);
        Reload();
        ShowStatus("Suggested correction added to the dictionary.");
    }

    [RelayCommand]
    private void DismissSuggestion(DictionarySuggestion? suggestion)
    {
        if (suggestion is null) return;
        RemoveSuggestion(suggestion);
        ShowStatus("Suggestion dismissed.");
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
        ShowStatus("Dictionary entry saved.");
    }

    [RelayCommand(CanExecute = nameof(CanDeleteEntry))]
    private async Task DeleteEntryAsync(CancellationToken cancellationToken)
    {
        if (!CanDeleteEntry()) return;
        var choice = await dialogs.ConfirmAsync(
            $"Delete '{Phrase.Trim()}' from the dictionary?",
            "Delete dictionary entry",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        var entries = library.LoadDictionary()
            .Where(entry => !string.Equals(entry.Id, EditingId, StringComparison.Ordinal))
            .ToList();
        library.SaveDictionary(entries);
        NewEntry();
        Reload();
        ShowStatus("Dictionary entry deleted.");
    }

    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var picked = await filePickers.PickSaveFileAsync(
                new FilePickerRequest("Export dictionary", [".json"], "muesli-dictionary.json"),
                cancellationToken);
            if (picked is null) return;
            _exchange.Export(picked.Path, library.LoadDictionary());
            ShowStatus($"Dictionary exported to {picked.DisplayName}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not export the dictionary: {exception.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var picked = await filePickers.PickOpenFileAsync(
                new FilePickerRequest("Import dictionary", [".json"]),
                cancellationToken);
            if (picked is null) return;
            var existing = library.LoadDictionary();
            var preview = _exchange.PreviewImport(picked.Path, existing);
            if (!preview.IsValid)
            {
                ShowStatus(string.Join(" ", preview.Errors), isError: true);
                return;
            }

            var message = $"Import {preview.Additions.Count:N0} new entries" +
                          (preview.Conflicts.Count == 0
                              ? "?"
                              : $" and replace {preview.Conflicts.Count:N0} conflicting entries?");
            var choice = await dialogs.ConfirmAsync(message, "Import dictionary preview", cancellationToken);
            if (choice != AppDialogChoice.Primary) return;
            var merged = DictionaryExchangeService.Merge(existing, preview, replaceConflicts: true);
            library.SaveDictionary(merged);
            NewEntry();
            Reload();
            ShowStatus($"Imported {preview.Additions.Count + preview.Conflicts.Count:N0} dictionary entries.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not import the dictionary: {exception.Message}", isError: true);
        }
    }

    private void Reload()
    {
        _allItems = library.LoadDictionary()
            .OrderBy(entry => entry.Phrase, StringComparer.CurrentCultureIgnoreCase)
            .Select(DictionaryListItem.From)
            .ToList();
        // A settings file that carries an explicit "dictionarySuggestions": null defeats the
        // record initialiser on MuesliSettings.DictionarySuggestions (SettingsStore.cs:326), so
        // this used to throw ArgumentNullException out of Load() and take the whole shell down
        // with an unhandled exception the moment Dictionary was opened. Reproduced on a profile
        // whose settings file had that key nulled.
        Suggestions = settings.Load().DictionarySuggestions?.Take(50).ToList() ?? [];
        OnPropertyChanged(nameof(HasSuggestions));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var filtered = search.Length == 0
            ? _allItems
            : _allItems.Where(item =>
                    item.Phrase.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    item.Replacement.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        var pageCount = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)PageSize));
        PageIndex = Math.Clamp(PageIndex, 0, pageCount - 1);
        Items = filtered.Skip(PageIndex * PageSize).Take(PageSize).ToList();
        PageLabel = $"Page {PageIndex + 1:N0} of {pageCount:N0} · {filtered.Count:N0} entries";
        HasPreviousPage = PageIndex > 0;
        HasNextPage = PageIndex + 1 < pageCount;
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private void RemoveSuggestion(DictionarySuggestion suggestion)
    {
        var current = settings.Load();
        var remaining = (current.DictionarySuggestions ?? []).Where(item =>
            !(string.Equals(item.Observed, suggestion.Observed, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(item.Replacement, suggestion.Replacement, StringComparison.OrdinalIgnoreCase))).ToList();
        settings.Save(current with { DictionarySuggestions = remaining });
        Suggestions = remaining;
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private bool CanDeleteEntry() => !string.IsNullOrWhiteSpace(EditingId);

    private void NotifyEmptyState()
    {
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateInstruction));
        OnPropertyChanged(nameof(ShowEmptyAddAction));
    }

    /// <summary>
    /// Refuses a save: the same text is shown inline beside the field and announced on the page's
    /// status bar, so the reason is visible whether or not the bar is on screen.
    /// </summary>
    private void Reject(string message)
    {
        ValidationMessage = message;
        ShowStatus(message, isError: true);
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = true;
    }
}

public sealed record DictionaryListItem(
    string Id,
    string Phrase,
    string Replacement,
    double MatchingThreshold)
{
    public string ReplacementLabel => string.IsNullOrWhiteSpace(Replacement)
        ? "Recognize as spoken"
        : $"Replace with {Replacement}";
    public string ThresholdLabel => $"{MatchingThreshold:P0} match";

    public static DictionaryListItem From(DictionaryEntryRecord entry) => new(
        entry.Id,
        entry.Phrase,
        entry.Replacement,
        entry.MatchingThreshold);
}
