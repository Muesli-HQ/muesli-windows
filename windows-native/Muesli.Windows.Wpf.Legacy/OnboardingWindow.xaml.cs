using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Muesli.Windows.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Muesli.Windows;

/// <summary>Lifecycle state deliberately contains no model path, transcript, or secret material.</summary>
public sealed record OnboardingModelSnapshot(string StatusText, bool IsReady, bool IsBusy, bool CanPrepare, bool CanCancel, bool CanRetry, bool CanVerify, double? ProgressPercent = null);

public sealed record OnboardingContext(
    IReadOnlyList<string> Microphones, IReadOnlyList<string> Hotkeys, IReadOnlyList<TranscriptionModelDefinition> Models,
    IReadOnlyList<LiveModelChoice> LiveModels, OnboardingDraft Draft, string OllamaEndpoint,
    Func<string?, Task<MicrophoneProbeResult>> ProbeMicrophoneAsync,
    Func<string, IProgress<ModelDownloadProgress>, CancellationToken, Task> PrepareOfflineModelAsync,
    Func<string, IProgress<ModelDownloadProgress>, CancellationToken, Task> VerifyOfflineModelAsync,
    Action<string> CancelOfflineModel, Func<string, IProgress<ModelDownloadProgress>, CancellationToken, Task> RetryOfflineModelAsync,
    Func<string, OnboardingModelSnapshot> OfflineModelSnapshot,
    Func<string, IProgress<ModelDownloadProgress>, CancellationToken, Task> PrepareLiveModelAsync,
    Func<string, IProgress<ModelDownloadProgress>, CancellationToken, Task> VerifyLiveModelAsync,
    Action<string> CancelLiveModel, Func<string, IProgress<ModelDownloadProgress>, CancellationToken, Task> RetryLiveModelAsync,
    Func<string, OnboardingModelSnapshot> LiveModelSnapshot,
    Func<CancellationToken, Task<string>> RunPipelineTestAsync, Func<string, bool> IsHotkeyUsable,
    bool OpenAiKeyConfigured, bool OpenRouterKeyConfigured, IReadOnlyList<string> IndicatorPositions,
    Action PreviewIndicator, Action ResetIndicatorPosition, Action OpenSummaryProviderSettings,
    Action<OnboardingDraft> SaveSettings, Action<OnboardingProgress> SaveProgress, Action Complete,
    bool IsPreview = false, string? PreviewDescription = null);

public sealed record OnboardingDraft(
    string UserName, string? Microphone, string Hotkey, string DictationModelId, string FinalModelId, string? LiveModelId,
    bool StartAtLogin, bool ShowIndicator, string IndicatorPosition, string SummaryProvider);

public partial class OnboardingWindow : Window
{
    private readonly OnboardingContext _context;
    private OnboardingProgress _progress;
    private OnboardingDraft _draft;
    private WpfComboBox? _primary;
    private WpfComboBox? _secondary;
    private CancellationTokenSource? _modelCancellation;
    private CancellationTokenSource? _pipelineCancellation;
    private (string Id, bool Live)? _activeModelOperation;
    private bool _isRendering;

    public OnboardingWindow(OnboardingContext context, OnboardingProgress progress)
    {
        InitializeComponent();
        _context = context;
        _draft = context.Draft;
        _progress = progress;
        RenderStep();
    }

    private void RenderStep()
    {
        _isRendering = true;
        try
        {
            ContentPanel.Children.Clear();
            PreviewModeText.Visibility = _context.IsPreview ? Visibility.Visible : Visibility.Collapsed;
            PreviewModeText.Text = _context.IsPreview
                ? "VISUAL VERIFICATION MODE · in-memory presentation only · no user data or services opened"
                : "";
            StepLabel.Text = $"SETUP · {_progress.Step + 1} OF {OnboardingProgress.LastStep + 1}";
            RenderStepDots();
            (TitleText.Text, DescriptionText.Text) = _progress.Step switch
            {
                0 => ("Welcome to Muesli", "Choose a name and review the local-first setup. You can pause safely at any time."),
                1 => ("Microphone and permissions", "Windows policy is a hint. A short non-retained WASAPI capture is the real microphone check."),
                2 => ("Separate model roles", "Choosing a model never starts a download. Prepare or verify each selected role when you are ready."),
                3 => ("Dictation shortcut", "This advisory check detects common conflicts. The actual low-level dictation hook remains decisive."),
                4 => ("Try live dictation", "Record a short local test. It does not paste or add a history item; setup needs a non-empty transcript."),
                5 => ("Optional product choices", "Local summaries work without a key. Cloud providers disclose where transcript text may be sent."),
                _ => ("You are ready", "Finish once all required checks are green, or pause and resume from the dashboard or tray.")
            };
            if (!string.IsNullOrWhiteSpace(_context.PreviewDescription))
                DescriptionText.Text = _context.PreviewDescription;
            _primary = _secondary = null;
            StatusText.Text = _progress.LastStatus;
            switch (_progress.Step)
            {
                case 0: AddTextBox("Your name", _draft.UserName); break;
                case 1: AddMicrophone(); break;
                case 2: AddModels(); break;
                case 3: AddHotkey(); break;
                case 4: AddPipelineTest(); break;
                case 5: AddOptionalChoices(); break;
                default: AddCompletion(); break;
            }
            BackButton.IsEnabled = _progress.Step > 0;
            FinishButton.Visibility = _progress.Step == OnboardingProgress.LastStep ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Visibility = _progress.Step == OnboardingProgress.LastStep ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _isRendering = false; }
    }

    private void RenderStepDots()
    {
        StepDots.Children.Clear();
        var active = TryFindResource("AccentBlueBrush") as System.Windows.Media.Brush;
        var idle = TryFindResource("TextTertiaryBrush") as System.Windows.Media.Brush;
        for (var index = 0; index <= OnboardingProgress.LastStep; index++)
        {
            StepDots.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = index == _progress.Step ? active : idle,
                Opacity = index == _progress.Step ? 1 : 0.45
            });
        }
    }

    private void AddTextBox(string label, string value)
    {
        AddLabel(label);
        var input = new WpfTextBox { Text = value, Style = (Style)FindResource("MuesliTextBox") };
        AutomationProperties.SetName(input, label);
        ContentPanel.Children.Add(input);
        input.LostFocus += (_, _) => SaveDraft(_draft with { UserName = input.Text.Trim() });
    }

    private void AddMicrophone()
    {
        AddCombo("Microphone", _context.Microphones, _draft.Microphone);
        ContentPanel.Children.Add(ActionButton("Test microphone", async () =>
        {
            SaveCurrentSelections();
            var result = await _context.ProbeMicrophoneAsync(_draft.Microphone);
            _progress = _progress with { MicrophoneVerified = result.IsUsable, VerifiedMicrophone = result.IsUsable ? _draft.Microphone ?? "" : "", LastStatus = result.IsUsable ? "Microphone capture verified" : $"Microphone test failed: {result.Failure}" };
            Persist();
            StatusText.Text = $"{result.PolicyHint} {result.Message}";
        }));
        ContentPanel.Children.Add(ActionButton("Open Windows microphone privacy", () =>
        {
            if (_context.IsPreview)
            {
                StatusText.Text = "Preview mode never opens Windows Settings. This control is shown to verify the permission-denied guidance.";
                return Task.CompletedTask;
            }
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
            return Task.CompletedTask;
        }));
    }

    private void AddModels()
    {
        AddCombo("Dictation offline model", _context.Models.Select(x => x.Id), _draft.DictationModelId);
        AddCombo("Final meeting and import offline model", _context.Models.Select(x => x.Id), _draft.FinalModelId, secondary: true);
        AddLabel("Live preview model", 14);
        var liveCombo = new WpfComboBox { ItemsSource = _context.LiveModels.ToList(), SelectedItem = _context.LiveModels.FirstOrDefault(x => x.Id == _draft.LiveModelId) ?? LiveModelChoice.Off, DisplayMemberPath = nameof(LiveModelChoice.Label), Style = (Style)FindResource("MuesliComboBox") };
        AutomationProperties.SetName(liveCombo, "Live preview model");
        liveCombo.SelectionChanged += (_, _) =>
        {
            if (_isRendering) return;
            if (liveCombo.SelectedItem is LiveModelChoice selected)
                ChangeModelSelection(_draft with { LiveModelId = selected.Id });
        };
        ContentPanel.Children.Add(liveCombo);
        foreach (var role in SelectedModelRoles()) AddModelOperationRow(role);
    }

    private IEnumerable<(string Id, bool Live, string Role)> SelectedModelRoles()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids.Add(_draft.DictationModelId)) yield return (_draft.DictationModelId, false, "Dictation");
        if (ids.Add(_draft.FinalModelId)) yield return (_draft.FinalModelId, false, "Final meeting/import");
        if (!string.IsNullOrWhiteSpace(_draft.LiveModelId) && ids.Add($"live:{_draft.LiveModelId}")) yield return (_draft.LiveModelId!, true, "Live preview");
    }

    private void AddModelOperationRow((string Id, bool Live, string Role) role)
    {
        var snapshot = role.Live ? _context.LiveModelSnapshot(role.Id) : _context.OfflineModelSnapshot(role.Id);
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = $"{role.Role}: {role.Id}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { Text = snapshot.StatusText, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(status, $"{role.Role} model status");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        panel.Children.Add(status);
        var progress = new System.Windows.Controls.ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 5,
            Margin = new Thickness(0, 7, 0, 0),
            Visibility = snapshot.IsBusy ? Visibility.Visible : Visibility.Collapsed,
            IsIndeterminate = snapshot.IsBusy && !snapshot.ProgressPercent.HasValue,
            Value = snapshot.ProgressPercent is double percent ? Math.Clamp(percent, 0, 100) : 0
        };
        AutomationProperties.SetName(progress, $"{role.Role} model operation progress");
        panel.Children.Add(progress);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (snapshot.CanPrepare) actions.Children.Add(ModelButton("Prepare", role, (p, token) => role.Live ? _context.PrepareLiveModelAsync(role.Id, p, token) : _context.PrepareOfflineModelAsync(role.Id, p, token), status, progress, actions));
        if (snapshot.CanVerify) actions.Children.Add(ModelButton("Verify", role, (p, token) => role.Live ? _context.VerifyLiveModelAsync(role.Id, p, token) : _context.VerifyOfflineModelAsync(role.Id, p, token), status, progress, actions));
        if (snapshot.IsBusy || snapshot.CanCancel) actions.Children.Add(CancelModelButton(role, status));
        if (snapshot.CanRetry) actions.Children.Add(ModelButton("Retry", role, (p, token) => role.Live ? _context.RetryLiveModelAsync(role.Id, p, token) : _context.RetryOfflineModelAsync(role.Id, p, token), status, progress, actions));
        panel.Children.Add(actions);
        ContentPanel.Children.Add(panel);
    }

    private WpfButton ModelButton(string action, (string Id, bool Live, string Role) role, Func<IProgress<ModelDownloadProgress>, CancellationToken, Task> operation, TextBlock status, System.Windows.Controls.ProgressBar progress, WrapPanel actions)
    {
        return ActionButton($"{action} {role.Role} model", async () =>
        {
            SaveCurrentSelections();
            if (_modelCancellation is not null)
            {
                status.Text = "Another model operation is already in progress. Cancel it or wait for it to finish.";
                return;
            }
            _modelCancellation = new CancellationTokenSource();
            _activeModelOperation = (role.Id, role.Live);
            progress.Visibility = Visibility.Visible;
            foreach (var control in actions.Children.OfType<UIElement>()) control.IsEnabled = false;
            var cancel = CancelModelButton(role, status);
            actions.Children.Add(cancel);
            var callback = new Progress<ModelDownloadProgress>(value =>
            {
                status.Text = value.DisplayText;
                progress.IsIndeterminate = !value.Percent.HasValue;
                progress.Value = value.Percent is int percent ? Math.Clamp(percent, 0, 100) : 0;
            });
            try
            {
                await operation(callback, _modelCancellation.Token);
                RefreshCurrentModelGate();
                status.Text = (role.Live ? _context.LiveModelSnapshot(role.Id) : _context.OfflineModelSnapshot(role.Id)).StatusText;
            }
            catch (OperationCanceledException) { status.Text = "Cancelled. You can retry safely."; RefreshCurrentModelGate(); }
            catch (Exception exception) { status.Text = exception.Message; RefreshCurrentModelGate(); }
            finally
            {
                _modelCancellation?.Dispose();
                _modelCancellation = null;
                _activeModelOperation = null;
                progress.IsIndeterminate = false;
                progress.Visibility = Visibility.Collapsed;
                Persist();
                RenderStep();
            }
        });
    }

    private WpfButton CancelModelButton((string Id, bool Live, string Role) role, TextBlock status) =>
        ActionButton($"Cancel {role.Role} model operation", () =>
        {
            if (_activeModelOperation is { } active && active.Id.Equals(role.Id, StringComparison.OrdinalIgnoreCase) && active.Live == role.Live)
            {
                if (role.Live) _context.CancelLiveModel(role.Id); else _context.CancelOfflineModel(role.Id);
                _modelCancellation?.Cancel();
                status.Text = "Cancelling model operation…";
            }
            return Task.CompletedTask;
        });

    private void ChangeModelSelection(OnboardingDraft draft)
    {
        ApplySelectionChange(draft, render: true);
    }

    private void RefreshCurrentModelGate()
    {
        var ready = SelectedModelRoles().All(role => (role.Live ? _context.LiveModelSnapshot(role.Id) : _context.OfflineModelSnapshot(role.Id)).IsReady);
        _progress = _progress with { ModelsVerified = ready, VerifiedDictationModelId = ready ? _draft.DictationModelId : "", VerifiedFinalModelId = ready ? _draft.FinalModelId : "", VerifiedLiveModelId = ready ? _draft.LiveModelId : null, LastStatus = ready ? "Selected model roles verified" : "A selected model role is not ready. Prepare, verify, or retry it." };
    }

    private void AddHotkey()
    {
        AddCombo("Hold-to-talk shortcut", _context.Hotkeys, _draft.Hotkey);
        ContentPanel.Children.Add(ActionButton("Check shortcut", () =>
        {
            SaveCurrentSelections();
            var usable = _context.IsHotkeyUsable(_draft.Hotkey);
            _progress = _progress with { HotkeyVerified = usable, VerifiedHotkey = usable ? _draft.Hotkey : "", LastStatus = usable ? "Shortcut registered by the existing dictation hook." : "Windows reports this shortcut is currently in use. Choose another preset and retry." };
            Persist(); RenderStep(); return Task.CompletedTask;
        }));
    }

    private void AddPipelineTest()
    {
        var actions = new WrapPanel();
        WpfButton? start = null;
        start = ActionButton("Record 3-second dictation test", async () =>
        {
            if (_pipelineCancellation is not null)
            {
                StatusText.Text = "A local setup test is already running. Cancel it or wait for it to finish.";
                return;
            }
            start!.IsEnabled = false;
            _pipelineCancellation = new CancellationTokenSource();
            var cancel = ActionButton("Cancel dictation test", () =>
            {
                _pipelineCancellation?.Cancel();
                StatusText.Text = "Cancelling the local setup test…";
                return Task.CompletedTask;
            });
            AutomationProperties.SetName(cancel, "Cancel dictation test");
            actions.Children.Add(cancel);
            cancel.Focus();
            try
            {
                StatusText.Text = "Recording and transcribing locally…";
                var text = await _context.RunPipelineTestAsync(_pipelineCancellation.Token);
                var passed = !string.IsNullOrWhiteSpace(text);
                _progress = _progress with { PipelineVerified = passed, LastStatus = passed ? "Pipeline test passed" : "Pipeline test needs another attempt" };
                Persist();
                StatusText.Text = passed ? $"Test transcript (not saved): {text}" : "No speech was detected. Try again.";
            }
            catch (OperationCanceledException)
            {
                _progress = _progress with { PipelineVerified = false, LastStatus = "Pipeline test cancelled. Record another non-retained test when ready." };
                Persist();
                StatusText.Text = _progress.LastStatus;
            }
            finally
            {
                _pipelineCancellation?.Dispose();
                _pipelineCancellation = null;
                actions.Children.Remove(cancel);
                start!.IsEnabled = true;
            }
        });
        actions.Children.Add(start);
        ContentPanel.Children.Add(actions);
    }

    private void AddOptionalChoices()
    {
        AddCombo("Meeting summary provider", SummaryProviderDisclosure.AvailableIds, _draft.SummaryProvider);
        var disclosure = new TextBlock { Text = SummaryProviderDisclosure.DisclosureFor(_draft.SummaryProvider, _context.OllamaEndpoint), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetName(disclosure, "Summary provider transcript disclosure");
        ContentPanel.Children.Add(disclosure);
        var keys = new TextBlock { Text = $"OpenAI key: {(_context.OpenAiKeyConfigured ? "configured securely" : "not configured")}. OpenRouter key: {(_context.OpenRouterKeyConfigured ? "configured securely" : "not configured")}. Keys can only be added in Settings.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(keys, "Cloud provider key configuration status");
        ContentPanel.Children.Add(keys);
        ContentPanel.Children.Add(ActionButton("Configure summary provider in Settings", () =>
        {
            SaveCurrentSelections();
            _context.OpenSummaryProviderSettings();
            return Task.CompletedTask;
        }));
        AddCheckBox("Launch at login", _draft.StartAtLogin, value => SaveDraft(_draft with { StartAtLogin = value }));
        AddCheckBox("Show floating indicator", _draft.ShowIndicator, value => SaveDraft(_draft with { ShowIndicator = value }));
        AddCombo("Floating indicator position", _context.IndicatorPositions, _draft.IndicatorPosition, secondary: true);
        ContentPanel.Children.Add(ActionButton("Preview floating indicator", () => { SaveCurrentSelections(); _context.PreviewIndicator(); return Task.CompletedTask; }));
        ContentPanel.Children.Add(ActionButton("Reset custom indicator position", () => { _context.ResetIndicatorPosition(); ChangeModelFreeDraft(_draft with { IndicatorPosition = "Middle Right" }); return Task.CompletedTask; }));
    }

    private void ChangeModelFreeDraft(OnboardingDraft draft) { SaveDraft(draft); RenderStep(); }
    private void AddCompletion() => ContentPanel.Children.Add(new TextBlock { Text = $"Microphone: {(_progress.MicrophoneVerified ? "ready" : "needs test")}\nModels: {(_progress.ModelsVerified ? "ready" : "needs preparation")}\nShortcut: {(_progress.HotkeyVerified ? "ready" : "needs check")}\nDictation test: {(_progress.PipelineVerified ? "passed" : "needs successful transcript")}", TextWrapping = TextWrapping.Wrap, FontSize = 15 });

    private WpfButton ActionButton(string text, Func<Task> action, System.Windows.Controls.Panel? parent = null)
    {
        var button = new WpfButton { Content = text, Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 8, 8, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception ex) { StatusText.Text = ex.Message; } finally { button.IsEnabled = true; } };
        return button;
    }

    private void AddLabel(string text, double top = 0) => ContentPanel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 6), TextWrapping = TextWrapping.Wrap });
    private void AddCombo(string label, IEnumerable<string> choices, string? selected, bool secondary = false)
    {
        AddLabel(label, secondary ? 14 : 0);
        var combo = new WpfComboBox { ItemsSource = choices.ToList(), SelectedItem = selected, Style = (Style)FindResource("MuesliComboBox") };
        AutomationProperties.SetName(combo, label);
        combo.SelectionChanged += (_, _) =>
        {
            if (_isRendering) return;
            if (_progress.Step is 1 or 2 or 3) SaveCurrentSelections(render: true);
            else if (_progress.Step == 5) { SaveCurrentSelections(); RenderStep(); }
        };
        if (secondary) _secondary = combo; else _primary = combo;
        ContentPanel.Children.Add(combo);
    }
    private void AddCheckBox(string label, bool selected, Action<bool> changed)
    {
        var check = new WpfCheckBox { Content = label, IsChecked = selected, Style = (Style)FindResource("MuesliCheckBox"), Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetName(check, label);
        check.Checked += (_, _) => changed(true); check.Unchecked += (_, _) => changed(false); ContentPanel.Children.Add(check);
    }
    private void SaveCurrentSelections(bool render = false)
    {
        var draft = _draft;
        if (_progress.Step == 1) draft = draft with { Microphone = _primary?.SelectedItem as string };
        if (_progress.Step == 2) draft = draft with { DictationModelId = _primary?.SelectedItem as string ?? draft.DictationModelId, FinalModelId = _secondary?.SelectedItem as string ?? draft.FinalModelId };
        if (_progress.Step == 3) draft = draft with { Hotkey = _primary?.SelectedItem as string ?? draft.Hotkey };
        if (_progress.Step == 5) draft = draft with { SummaryProvider = _primary?.SelectedItem as string ?? draft.SummaryProvider, IndicatorPosition = _secondary?.SelectedItem as string ?? draft.IndicatorPosition };
        ApplySelectionChange(draft, render);
    }
    private void ApplySelectionChange(OnboardingDraft draft, bool render)
    {
        var before = ToSelection(_draft);
        var after = ToSelection(draft);
        if (before == after) return;
        SaveDraft(draft);
        _progress = OnboardingProgressReconciler.InvalidateForSelectionChange(_progress, before, after);
        Persist();
        if (render) RenderStep();
    }
    private static OnboardingSelection ToSelection(OnboardingDraft draft) => new(draft.Microphone, draft.DictationModelId, draft.FinalModelId, draft.LiveModelId, draft.Hotkey);
    private void SaveDraft(OnboardingDraft draft) { _draft = draft; _context.SaveSettings(draft); }
    private void Persist() => _context.SaveProgress(_progress);
    private void Back_Click(object sender, RoutedEventArgs e) { SaveCurrentSelections(); _progress = _progress with { Step = Math.Max(0, _progress.Step - 1) }; Persist(); RenderStep(); }
    private void Next_Click(object sender, RoutedEventArgs e) { SaveCurrentSelections(); _progress = _progress with { Step = Math.Min(OnboardingProgress.LastStep, _progress.Step + 1) }; Persist(); RenderStep(); }
    private void Skip_Click(object sender, RoutedEventArgs e) { SaveCurrentSelections(); _progress = _progress with { Deferred = true, LastStatus = "Setup paused. Resume setup from the dashboard, tray, Settings, or About." }; Persist(); Close(); }
    private void Finish_Click(object sender, RoutedEventArgs e) { if (!(_progress.MicrophoneVerified && _progress.ModelsVerified && _progress.HotkeyVerified && _progress.PipelineVerified)) { StatusText.Text = "Complete each required check before finishing setup."; return; } _context.Complete(); Close(); }
    private void Window_Activated(object? sender, EventArgs e) { if (!_context.IsPreview && _progress.Step == 1) StatusText.Text = "Microphone permissions may have changed; run the non-retained capture test to refresh this gate."; }
    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e) { if (e.Key == Key.Escape) { Skip_Click(sender, e); e.Handled = true; } }
    protected override void OnClosed(EventArgs e) { _modelCancellation?.Cancel(); _modelCancellation?.Dispose(); _pipelineCancellation?.Cancel(); _pipelineCancellation?.Dispose(); base.OnClosed(e); }
}
