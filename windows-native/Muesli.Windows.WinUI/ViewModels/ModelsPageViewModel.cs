using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

/// <summary>What the Models status bar is reporting, independent of any UI type.</summary>
public enum ModelStatusTone
{
    Informational,
    Success,
    Error
}

/// <summary>
/// P6-01. The page used to emit one card per catalog entry, so the six Whisper entries produced
/// six sibling cards all titled "Whisper" and the three Parakeet entries three titled
/// "Parakeet Family". macOS (<c>docs/ui-reference/macos-current-2026-08-27/04-models.png</c>,
/// source <c>ModelsView.swift:143-161, 654-742</c>) renders one card per family with a variant
/// popup. Families here are derived from the real catalog's
/// <see cref="NativeAsrModelKind"/> — no family, provider, or variant is invented.
/// </summary>
public partial class ModelsPageViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly WinUiModelsContext _models;
    private readonly WinUiSettingsContext _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppDialogService _dialogs;
    private readonly Dictionary<string, ModelFamilyCardItem> _families = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _progressByModel = new(StringComparer.OrdinalIgnoreCase);
    private bool _resetVariantSelection = true;

    public ModelsPageViewModel(
        WinUiModelsContext models,
        WinUiSettingsContext settings,
        IUiDispatcher dispatcher,
        IAppDialogService dialogs)
    {
        _models = models;
        _settings = settings;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _models.ModelChanged += OnModelChanged;
        Reload();
    }

    /// <summary>
    /// Persists a per-model language choice and updates the live selection. Pooled native clients
    /// observe the change and rebuild their recognizer before the next transcription.
    /// </summary>
    public void SetLanguage(ModelVariantItem? item, string? code)
    {
        if (item is null || !TranscriptionModelCatalog.TryGet(item.Id, out var model)) return;
        var normalized = ModelLanguageSupport.Normalize(model, code);
        var settings = _settings.Load();
        var map = new Dictionary<string, string>(settings.ModelLanguages, StringComparer.OrdinalIgnoreCase)
        {
            [model.Id] = normalized
        };
        _settings.Save(settings with { ModelLanguages = map });
        item.ApplyLanguage(normalized);
        ShowStatus($"{model.DisplayName} language set to {ModelLanguageSupport.LabelFor(model, normalized)}.");
    }

    [ObservableProperty] public partial IReadOnlyList<ModelFamilyCardItem> Items { get; private set; } = [];
    [ObservableProperty] public partial int RoleIndex { get; set; }
    [ObservableProperty] public partial int CategoryIndex { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    /// <summary>
    /// What the status bar is actually reporting. A running operation and a cancellation used to
    /// render with the Success severity's green tick, which claims an outcome that has not
    /// happened; the page maps this to the InfoBar severity instead.
    /// </summary>
    [ObservableProperty] public partial ModelStatusTone StatusTone { get; private set; } = ModelStatusTone.Informational;

    public bool IsCleanupCategory => CategoryIndex == 2;
    public bool HasItems => Items.Count > 0;

    public string CategoryDescription => CategoryIndex switch
    {
        1 => "Choose the model used for live meetings and imported recordings.",
        2 => "Optional cleanup is applied locally after transcription when enabled in Settings.",
        _ => "Choose the model used for push-to-talk dictation."
    };

    public string RoleDescription => RoleIndex == 0
        ? "Choose the model used for push-to-talk dictation."
        : "Choose the final model used for recordings and imported meetings.";

    private string RoleLabel => RoleIndex == 1 ? "meetings" : "dictation";

    partial void OnRoleIndexChanged(int value)
    {
        if (value is 0 or 1 && CategoryIndex != value)
        {
            CategoryIndex = value;
        }
        OnPropertyChanged(nameof(RoleDescription));
        _resetVariantSelection = true;
        Reload();
    }

    partial void OnCategoryIndexChanged(int value)
    {
        if (value is 0 or 1 && RoleIndex != value)
        {
            RoleIndex = value;
        }
        OnPropertyChanged(nameof(IsCleanupCategory));
        OnPropertyChanged(nameof(CategoryDescription));
        OnPropertyChanged(nameof(RoleDescription));
        _resetVariantSelection = true;
        Reload();
    }

    partial void OnItemsChanged(IReadOnlyList<ModelFamilyCardItem> value) =>
        OnPropertyChanged(nameof(HasItems));

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void SetActive(ModelVariantItem? item)
    {
        if (item is null || !item.IsReady) return;
        _models.SetActive(item.Id, forMeetings: RoleIndex == 1);
        Reload();
        ShowStatus($"{item.DisplayName} is now active for {RoleLabel}.");
    }

    [RelayCommand]
    private async Task PrepareAsync(ModelVariantItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        await RunOperationAsync(item, (progress, token) => _models.PrepareAsync(item.Id, progress, token), cancellationToken);
    }

    [RelayCommand]
    private async Task VerifyAsync(ModelVariantItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        await RunOperationAsync(item, (progress, token) => _models.VerifyAsync(item.Id, progress, token), cancellationToken);
    }

    [RelayCommand]
    private async Task RetryAsync(ModelVariantItem? item, CancellationToken cancellationToken)
    {
        if (item is null) return;
        await RunOperationAsync(item, (_, token) => _models.RetryAsync(item.Id, token), cancellationToken);
    }

    [RelayCommand]
    private void Cancel(ModelVariantItem? item)
    {
        if (item is not null) _models.Cancel(item.Id);
    }

    [RelayCommand]
    private async Task DeleteAsync(ModelVariantItem? item, CancellationToken cancellationToken)
    {
        if (item is null || item.IsSelectedForAnyRole) return;
        var choice = await _dialogs.ConfirmAsync(
            $"Delete the local files for {item.DisplayName}? You can download them again later.",
            "Delete transcription model",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        try
        {
            await _models.DeleteAsync(item.Id, cancellationToken);
            ShowStatus($"{item.DisplayName} was deleted.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not delete {item.DisplayName}: {exception.Message}", ModelStatusTone.Error);
        }
        Reload();
    }

    private async Task RunOperationAsync(
        ModelVariantItem item,
        Func<IProgress<ModelDownloadProgress>, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                _progressByModel[item.Id] = value.Percent;
                StatusMessage = $"{item.DisplayName}: {value.DisplayText}";
                StatusTone = ModelStatusTone.Informational;
                IsStatusOpen = true;
                Reload();
            });
            await operation(progress, cancellationToken);
            ShowStatus($"{item.DisplayName} is ready and verified.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus($"{item.DisplayName} operation cancelled.", ModelStatusTone.Informational);
        }
        catch (Exception exception)
        {
            ShowStatus($"{item.DisplayName} failed: {exception.Message}", ModelStatusTone.Error);
        }
        // A stale percentage outlives the operation that produced it and would keep reading as
        // live progress on the next visit to the card, so the entry is dropped once it ends.
        _progressByModel.Remove(item.Id);
        Reload();
    }

    /// <summary>
    /// Rebuilds card state in place. The <see cref="ModelFamilyCardItem"/> and
    /// <see cref="ModelVariantItem"/> instances are reused across reloads so the variant popup
    /// keeps its selection while a download reports progress (progress calls this on every tick).
    /// </summary>
    private void Reload()
    {
        if (_disposed) return;
        RefreshAcceleration();
        if (CategoryIndex == 2)
        {
            ReloadCleanup();
            if (Items.Count > 0) Items = [];
            return;
        }

        var settings = _models.Settings;
        var activeId = RoleIndex == 1 ? settings.FinalMeetingModelId : settings.DictationModelId;
        var reset = _resetVariantSelection;
        _resetVariantSelection = false;

        var ordered = new List<ModelFamilyCardItem>();
        foreach (var group in _models.Snapshots().GroupBy(snapshot => ModelFamilyCatalog.KeyFor(snapshot.Model)))
        {
            if (!_families.TryGetValue(group.Key, out var family))
            {
                family = new ModelFamilyCardItem(ModelFamilyCatalog.Describe(group.Key, group.First().Model));
                _families[group.Key] = family;
            }

            family.Update(
                group.ToList(),
                activeId,
                settings.DictationModelId,
                settings.FinalMeetingModelId,
                _progressByModel,
                reset);
            ordered.Add(family);
        }

        if (!Items.SequenceEqual(ordered)) Items = ordered;
    }

    private void ShowStatus(string message, ModelStatusTone tone = ModelStatusTone.Success)
    {
        if (_disposed) return;
        StatusMessage = message;
        StatusTone = tone;
        IsStatusOpen = true;
    }

    private void OnModelChanged(object? sender, string modelId) => _dispatcher.TryEnqueue(Reload);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _models.ModelChanged -= OnModelChanged;
    }
}

/// <summary>
/// Family identity for the cards. Derived from the shipping catalog's
/// <see cref="NativeAsrModelKind"/>, so a catalog edit cannot leave the page showing a family
/// that no longer exists. The two multi-variant summaries are the macOS product's own copy for
/// those two cards (<c>04-models.png</c>); single-variant families use the model's own summary
/// exactly as the previous per-model cards did.
/// </summary>
internal static class ModelFamilyCatalog
{
    public static string KeyFor(TranscriptionModelDefinition model) => model.Kind switch
    {
        NativeAsrModelKind.Parakeet or NativeAsrModelKind.ParakeetTransducer => "parakeet",
        NativeAsrModelKind.Whisper => "whisper",
        NativeAsrModelKind.SenseVoice => "sensevoice",
        NativeAsrModelKind.Qwen3Asr => "qwen3-asr",
        NativeAsrModelKind.CohereTranscribe => "cohere-transcribe",
        _ => model.Id.ToLowerInvariant()
    };

    public static ModelFamilyDescriptor Describe(string key, TranscriptionModelDefinition first) => key switch
    {
        "parakeet" => new ModelFamilyDescriptor(
            key,
            "Parakeet Family",
            "NVIDIA",
            "",
            "The most responsive choices for everyday dictation, with multilingual and English-only options.",
            "Recommended: Unified",
            "parakeet-unified-en-int8",
            "nvidia-logo"),
        "whisper" => new ModelFamilyDescriptor(
            key,
            "Whisper",
            "OpenAI",
            "",
            "Dependable alternatives when you prefer Whisper's transcription style or need broader multilingual coverage.",
            "Default: Small",
            "whisper-small-en",
            "openai-logo"),
        "sensevoice" => new ModelFamilyDescriptor(key, "SenseVoice", "Alibaba", "", "", "", first.Id, "qwen-logo"),
        "qwen3-asr" => new ModelFamilyDescriptor(key, "Qwen3-ASR", "Alibaba", "", "", "", first.Id, "qwen-logo"),
        "cohere-transcribe" => new ModelFamilyDescriptor(key, "Cohere Transcribe", "Cohere", "", "", "", first.Id, "cohere-logo"),
        _ => new ModelFamilyDescriptor(key, first.DisplayName, "Local model", "", "", "", first.Id, "")
    };
}

internal sealed record ModelFamilyDescriptor(
    string Key,
    string Label,
    string Vendor,
    string Glyph,
    string Summary,
    string BadgeLabel,
    string DefaultModelId,
    // Repository-owned vendor mark filename (without extension), or "" for a neutral fallback.
    string LogoFile);

/// <summary>
/// One card. Owns the family's variants and the variant the card is currently showing.
/// </summary>
public sealed partial class ModelFamilyCardItem : ObservableObject
{
    private readonly ModelFamilyDescriptor _descriptor;
    private readonly Dictionary<string, ModelVariantItem> _byId = new(StringComparer.OrdinalIgnoreCase);
    private List<ModelVariantItem> _variants = [];

    internal ModelFamilyCardItem(ModelFamilyDescriptor descriptor) => _descriptor = descriptor;

    public string Key => _descriptor.Key;
    public string FamilyLabel => _descriptor.Label;
    public string VendorLabel => _descriptor.Vendor;
    public string VendorGlyph => _descriptor.Glyph;

    /// <summary>Repository-owned vendor mark filename (no extension), or "" for a neutral glyph.</summary>
    public string LogoFile => _descriptor.LogoFile;
    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoFile);
    public string BadgeLabel => _descriptor.BadgeLabel;
    public bool HasBadge => !string.IsNullOrWhiteSpace(BadgeLabel);

    public IReadOnlyList<ModelVariantItem> Variants => _variants;
    public bool HasVariantPicker => _variants.Count > 1;

    [ObservableProperty] public partial ModelVariantItem? SelectedVariant { get; set; }

    /// <summary>
    /// A single-variant family has nothing to choose between, so the card shows that model's own
    /// summary in the header and does not repeat it under the (absent) picker.
    /// </summary>
    public string FamilySummary => HasVariantPicker
        ? _descriptor.Summary
        : SelectedVariant?.Summary ?? _descriptor.Summary;

    public string VariantDescription => HasVariantPicker ? SelectedVariant?.Summary ?? "" : "";
    public bool HasVariantDescription => !string.IsNullOrWhiteSpace(VariantDescription);

    public string VariantPickerAccessibleName => $"{FamilyLabel} variant";

    /// <summary>
    /// One picker per card, so the automation id has to be per family rather than one id shared
    /// by every card in the list.
    /// </summary>
    public string VariantPickerAutomationId => $"ModelVariantPicker_{Key}";
    public string CardAutomationId => $"ModelCard_{Key}";
    public string VendorAccessibleName => $"{VendorLabel} model family";

    public string AccessibleName
    {
        get
        {
            var variant = SelectedVariant;
            if (variant is null) return FamilyLabel;
            return string.IsNullOrWhiteSpace(variant.StatusChipLabel)
                ? $"{FamilyLabel}. {variant.DisplayName}."
                : $"{FamilyLabel}. {variant.DisplayName}. {variant.StatusChipLabel}.";
        }
    }

    partial void OnSelectedVariantChanged(ModelVariantItem? value)
    {
        OnPropertyChanged(nameof(FamilySummary));
        OnPropertyChanged(nameof(VariantDescription));
        OnPropertyChanged(nameof(HasVariantDescription));
        OnPropertyChanged(nameof(AccessibleName));
    }

    internal void Update(
        IReadOnlyList<TranscriptionModelSnapshot> snapshots,
        string? activeId,
        string? dictationModelId,
        string? meetingModelId,
        IReadOnlyDictionary<string, int?> progressByModel,
        bool resetSelection)
    {
        var rebuilt = new List<ModelVariantItem>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (!_byId.TryGetValue(snapshot.Model.Id, out var variant))
            {
                variant = new ModelVariantItem(snapshot.Model);
                _byId[snapshot.Model.Id] = variant;
            }

            variant.Update(
                snapshot,
                isActive: Matches(snapshot.Model.Id, activeId) &&
                          snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected,
                isSelectedForDictation: Matches(snapshot.Model.Id, dictationModelId),
                isSelectedForMeetings: Matches(snapshot.Model.Id, meetingModelId),
                progressByModel.GetValueOrDefault(snapshot.Model.Id));
            rebuilt.Add(variant);
        }

        var membershipChanged = !_variants.SequenceEqual(rebuilt);
        _variants = rebuilt;
        if (membershipChanged)
        {
            OnPropertyChanged(nameof(Variants));
            OnPropertyChanged(nameof(HasVariantPicker));
        }

        if (resetSelection || SelectedVariant is null || !_variants.Contains(SelectedVariant))
        {
            SelectedVariant = ChooseVariant(activeId);
        }
        else
        {
            // The selected variant's own state may have changed under it (progress, a failure,
            // a new active model) without the identity changing.
            OnPropertyChanged(nameof(FamilySummary));
            OnPropertyChanged(nameof(VariantDescription));
            OnPropertyChanged(nameof(HasVariantDescription));
            OnPropertyChanged(nameof(AccessibleName));
        }
    }

    private ModelVariantItem? ChooseVariant(string? activeId)
    {
        if (_variants.Count == 0) return null;
        return _variants.FirstOrDefault(variant => Matches(variant.Id, activeId))
               ?? _variants.FirstOrDefault(variant => Matches(variant.Id, _descriptor.DefaultModelId))
               ?? _variants.FirstOrDefault(variant => variant.IsReady)
               ?? _variants[0];
    }

    private static bool Matches(string id, string? other) =>
        !string.IsNullOrWhiteSpace(other) && string.Equals(id, other, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One catalog entry as the card presents it. Mutable and observable so the card can be updated
/// in place; every value is read from the real <see cref="TranscriptionModelSnapshot"/>.
/// </summary>
public sealed partial class ModelVariantItem : ObservableObject
{
    private readonly TranscriptionModelDefinition _model;

    public ModelVariantItem(TranscriptionModelDefinition model)
    {
        _model = model;
        Id = model.Id;
        DisplayName = model.DisplayName;
        Summary = model.Summary;
        Languages = model.Languages;
        SizeLabel = model.SizeLabel;
        SelectedLanguageCode = ModelLanguageSupport.Normalize(model, TranscriptionLanguageSelection.Resolve(model));
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Summary { get; }
    public string Languages { get; }
    public string SizeLabel { get; }

    /// <summary>The languages this model's runtime genuinely accepts (English-only, automatic, or a list).</summary>
    public IReadOnlyList<ModelLanguageOption> LanguageOptions => ModelLanguageSupport.OptionsFor(_model);
    public bool IsLanguageSelectable => ModelLanguageSupport.IsSelectable(_model);
    public string LanguageAccessibleName => $"{DisplayName} language";

    [ObservableProperty] public partial string SelectedLanguageCode { get; private set; }

    public string SelectedLanguageLabel => ModelLanguageSupport.LabelFor(_model, SelectedLanguageCode);

    partial void OnSelectedLanguageCodeChanged(string value) => OnPropertyChanged(nameof(SelectedLanguageLabel));

    /// <summary>Updates the shown choice after a persisted language change.</summary>
    public void ApplyLanguage(string code) => SelectedLanguageCode = ModelLanguageSupport.Normalize(_model, code);

    public TranscriptionModelStatus Status { get; private set; }
    public string StatusText { get; private set; } = "";
    public string DiskSize { get; private set; } = "";
    public bool HasFilesOnDisk { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsSelectedForDictation { get; private set; }
    public bool IsSelectedForMeetings { get; private set; }
    public bool IsBusy { get; private set; }
    public bool CanPrepare { get; private set; }
    public bool CanCancel { get; private set; }
    public bool CanRetry { get; private set; }
    public bool CanVerify { get; private set; }
    public bool CanDelete { get; private set; }
    public bool IsReady { get; private set; }
    public bool CanSetActive { get; private set; }
    public int? ProgressPercent { get; private set; }

    public bool IsSelectedForAnyRole => IsSelectedForDictation || IsSelectedForMeetings;

    public string DownloadLabel => SizeLabel.Contains('·')
        ? SizeLabel[..SizeLabel.IndexOf('·')].Trim()
        : SizeLabel;

    public string InstalledLabel => string.IsNullOrWhiteSpace(DiskSize) || DiskSize == "0 B"
        ? string.Empty
        : $"Installed · {DiskSize}";
    public bool HasInstalledSize => !string.IsNullOrWhiteSpace(InstalledLabel);
    public bool HasLanguages => !string.IsNullOrWhiteSpace(Languages);

    public string ProgressLabel => ProgressPercent is int value ? $"{value}%" : StatusText;
    public bool IsProgressIndeterminate => IsBusy && ProgressPercent is null;
    public double ProgressValue => ProgressPercent ?? 0;

    public bool HasActions => CanSetActive || CanPrepare || CanVerify || CanRetry || CanCancel || CanDelete;

    public bool ShowActiveChip => IsActive;
    public bool ShowDownloadedChip =>
        !IsActive && !IsBusy && Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected;
    public bool ShowErrorChip =>
        !IsBusy && Status is TranscriptionModelStatus.Failed or TranscriptionModelStatus.DeletionFailed;
    public bool ShowUnavailableChip =>
        !IsActive && !IsBusy && Status is TranscriptionModelStatus.RuntimeUnavailable;

    public string StatusChipLabel => ShowActiveChip
        ? "Active"
        : ShowErrorChip
            ? (Status == TranscriptionModelStatus.DeletionFailed ? "Delete failed" : "Failed")
            : ShowUnavailableChip
                ? "Unavailable"
                : ShowDownloadedChip
                    ? "Downloaded"
                    : "";

    /// <summary>
    /// Why an action the card does not offer is missing. Every branch is derived from the real
    /// snapshot, so the card never claims an operation is possible when the service refuses it,
    /// and never leaves a disabled-looking card unexplained.
    /// </summary>
    public string StateNote => Status switch
    {
        TranscriptionModelStatus.RuntimeUnavailable =>
            "The on-device speech runtime is not available on this PC, so no model can be downloaded, verified, or deleted. " +
            StatusText,
        TranscriptionModelStatus.Failed or TranscriptionModelStatus.DeletionFailed => StatusText,
        _ when IsSelectedForAnyRole && HasFilesOnDisk && !CanDelete && !IsBusy =>
            $"These files stay on disk because this model is selected for {SelectedRoleLabel}. Pick a different model for that role first.",
        _ => ""
    };

    public bool HasStateNote => !string.IsNullOrWhiteSpace(StateNote);

    private string SelectedRoleLabel => IsSelectedForDictation && IsSelectedForMeetings
        ? "dictation and meetings"
        : IsSelectedForDictation
            ? "dictation"
            : "meetings";

    public string AccessibleName => string.IsNullOrWhiteSpace(StatusChipLabel)
        ? DisplayName
        : $"{DisplayName}. {StatusChipLabel}.";

    public override string ToString() => DisplayName;

    internal void Update(
        TranscriptionModelSnapshot snapshot,
        bool isActive,
        bool isSelectedForDictation,
        bool isSelectedForMeetings,
        int? progressPercent)
    {
        Status = snapshot.Status;
        StatusText = snapshot.StatusText;
        DiskSize = snapshot.DiskSize;
        HasFilesOnDisk = snapshot.DiskSizeBytes > 0;
        IsActive = isActive;
        IsSelectedForDictation = isSelectedForDictation;
        IsSelectedForMeetings = isSelectedForMeetings;
        IsBusy = snapshot.IsBusy;
        CanPrepare = snapshot.CanPrepare && snapshot.Status == TranscriptionModelStatus.Missing;
        CanCancel = snapshot.CanCancel;
        CanRetry = snapshot.CanRetry;
        CanVerify = snapshot.CanVerify;
        CanDelete = snapshot.CanDelete && !(isSelectedForDictation || isSelectedForMeetings);
        IsReady = snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected;
        CanSetActive = !isActive && IsReady;
        ProgressPercent = progressPercent;
        OnPropertyChanged(string.Empty);
    }
}
