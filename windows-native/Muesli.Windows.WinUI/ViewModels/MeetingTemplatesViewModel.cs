using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class MeetingTemplatesViewModel(
    WinUiLibraryContext library,
    IAppDialogService dialogs) : ObservableObject
{
    [ObservableProperty] public partial IReadOnlyList<PersistedMeetingTemplate> Templates { get; private set; } = [];
    [ObservableProperty] public partial PersistedMeetingTemplate? SelectedTemplate { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Prompt { get; set; } = "";
    [ObservableProperty] public partial string NameError { get; private set; } = "";
    [ObservableProperty] public partial string PromptError { get; private set; } = "";
    [ObservableProperty] public partial string StatusTitle { get; private set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }

    public bool HasItems => Templates.Count > 0;
    public bool HasNameError => NameError.Length > 0;
    public bool HasPromptError => PromptError.Length > 0;
    public bool IsEditingExisting => SelectedTemplate is not null;
    public string TemplateCountLabel => HasItems
        ? $"{Templates.Count} template{(Templates.Count == 1 ? "" : "s")}"
        : "No templates yet";
    public string EditorTitle => IsEditingExisting ? "Edit template" : "New template";
    public string EditorSubtitle => IsEditingExisting
        ? "Changes apply the next time you generate notes. Existing meeting notes stay as they are."
        : "These instructions are used the next time you generate notes for a meeting on this PC.";
    public string EmptyStateTitle => "No custom templates yet";
    public string EmptyStateInstruction => "Create a named set of summary instructions, then pick it when you generate meeting notes.";

    public void Load(bool selectFirstIfNone = false)
    {
        var previousId = SelectedTemplate?.Id;
        Templates = library.LoadMeetingTemplates()
            .OrderBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(previousId))
        {
            SelectedTemplate = Templates.FirstOrDefault(template =>
                string.Equals(template.Id, previousId, StringComparison.Ordinal));
            return;
        }

        if (selectFirstIfNone && Templates.Count > 0)
        {
            SelectedTemplate = Templates[0];
        }
    }

    partial void OnTemplatesChanged(IReadOnlyList<PersistedMeetingTemplate> value)
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(TemplateCountLabel));
    }

    partial void OnSelectedTemplateChanged(PersistedMeetingTemplate? value)
    {
        Name = value?.Name ?? "";
        Prompt = value?.Prompt ?? "";
        ClearFieldErrors();
        OnPropertyChanged(nameof(IsEditingExisting));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorSubtitle));
        DeleteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnNameChanged(string value)
    {
        if (NameError.Length > 0) NameError = "";
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnPromptChanged(string value)
    {
        if (PromptError.Length > 0) PromptError = "";
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnNameErrorChanged(string value) => OnPropertyChanged(nameof(HasNameError));
    partial void OnPromptErrorChanged(string value) => OnPropertyChanged(nameof(HasPromptError));

    [RelayCommand]
    private void NewTemplate()
    {
        SelectedTemplate = null;
        Name = "";
        Prompt = "";
        ClearFieldErrors();
        IsStatusOpen = false;
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => NewTemplate();

    private bool CanCancel() =>
        SelectedTemplate is not null || Name.Length > 0 || Prompt.Length > 0;

    [RelayCommand]
    private void Save()
    {
        var name = Name.Trim();
        var prompt = Prompt.Trim();
        var nameError = "";
        var promptError = "";
        if (name.Length == 0)
        {
            nameError = "Enter a template name.";
        }
        else if (MeetingSummaryService.IsBuiltInTemplate(name))
        {
            nameError = "Built-in template names are reserved. Choose a distinct name.";
        }

        if (prompt.Length == 0)
        {
            promptError = "Enter summary instructions.";
        }

        if (nameError.Length == 0)
        {
            var templates = library.LoadMeetingTemplates();
            var duplicate = templates.FirstOrDefault(template =>
                string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(template.Id, SelectedTemplate?.Id, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                nameError = "A template with that name already exists.";
            }
        }

        if (nameError.Length > 0 || promptError.Length > 0)
        {
            NameError = nameError;
            PromptError = promptError;
            ShowStatus(
                nameError.Length > 0 && promptError.Length > 0
                    ? "A template needs both a name and instructions."
                    : nameError.Length > 0 ? nameError : promptError,
                true,
                "Can't save template");
            return;
        }

        var templatesToSave = library.LoadMeetingTemplates().ToList();
        var saved = new PersistedMeetingTemplate
        {
            Id = SelectedTemplate?.Id ?? $"template_{Guid.NewGuid():N}",
            Name = name,
            Prompt = prompt,
            Icon = SelectedTemplate?.Icon ?? "square.and.pencil"
        };
        var index = templatesToSave.FindIndex(template => template.Id == saved.Id);
        if (index >= 0) templatesToSave[index] = saved;
        else templatesToSave.Add(saved);
        library.SaveMeetingTemplates(templatesToSave);
        SelectedTemplate = saved;
        Load();
        SelectedTemplate = Templates.First(template => template.Id == saved.Id);
        ClearFieldErrors();
        ShowStatus("Template saved.");
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (SelectedTemplate is null) return;
        var choice = await dialogs.ConfirmAsync(
            $"Delete the template “{SelectedTemplate.Name}”? Existing meeting notes will not be changed.",
            "Delete template",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        var templates = library.LoadMeetingTemplates()
            .Where(template => template.Id != SelectedTemplate.Id)
            .ToList();
        library.SaveMeetingTemplates(templates);
        NewTemplate();
        Load();
        ShowStatus("Template deleted.");
    }

    private bool CanDelete() => SelectedTemplate is not null;

    private void ClearFieldErrors()
    {
        NameError = "";
        PromptError = "";
    }

    private void ShowStatus(string message, bool error = false, string title = "")
    {
        StatusTitle = title;
        StatusMessage = message;
        IsStatusError = error;
        IsStatusOpen = true;
    }
}
