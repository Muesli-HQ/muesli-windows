using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.ViewModels;

// The meeting-summaries step shows what each provider needs right under the picker, as macOS
// setup does: a sign-in button for ChatGPT, Connect OpenRouter or a pasted key, an API key for
// OpenAI and Anthropic, the sign-in state for Claude Code, and a note for Ollama. Keys go straight
// to Windows Credential Manager; nothing here is written to the settings file.
public partial class OnboardingViewModel
{
    private CancellationTokenSource? _chatGptSignIn;
    private CancellationTokenSource? _openRouterSignIn;
    private bool _loadingSetupKey;

    [NotifyPropertyChangedFor(nameof(IsChatGptSignedOut))]
    [ObservableProperty] public partial bool IsChatGptSignedIn { get; private set; }
    [ObservableProperty] public partial bool IsChatGptSigningIn { get; private set; }
    [ObservableProperty] public partial string ChatGptSetupStatus { get; private set; } = "";

    [NotifyPropertyChangedFor(nameof(IsOpenRouterDisconnected))]
    [ObservableProperty] public partial bool IsOpenRouterConnected { get; private set; }
    [ObservableProperty] public partial bool IsOpenRouterConnecting { get; private set; }
    [ObservableProperty] public partial string OpenRouterSetupStatus { get; private set; } = "";

    [ObservableProperty] public partial string ClaudeCodeSetupStatus { get; private set; } = "";

    /// <summary>The API key of the selected provider (OpenAI, Anthropic or OpenRouter).</summary>
    [ObservableProperty] public partial string SetupApiKey { get; set; } = "";

    public bool IsChatGptSignedOut => !IsChatGptSignedIn;
    public bool IsOpenRouterDisconnected => !IsOpenRouterConnected;

    private string SetupProviderId => SelectedSummaryProvider?.Id ?? SummaryProviderDisclosure.Local;

    public bool ShowChatGptSetup => SetupProviderId == SummaryProviderDisclosure.ChatGpt;
    public bool ShowOpenRouterSetup => SetupProviderId == SummaryProviderDisclosure.OpenRouter;
    public bool ShowClaudeCodeSetup => SetupProviderId == SummaryProviderDisclosure.ClaudeCode;
    public bool ShowOllamaSetup => SetupProviderId == SummaryProviderDisclosure.Ollama;
    public bool ShowApiKeySetup => SetupKeySecret is not null;

    /// <summary>LM Studio and a custom endpoint need a server address and model, which live in Settings.</summary>
    public bool ShowSummarySettingsLink =>
        SetupProviderId is SummaryProviderDisclosure.LmStudio or SummaryProviderDisclosure.CustomLlm;

    public string SetupApiKeyHeader => SetupProviderId switch
    {
        SummaryProviderDisclosure.Anthropic => "Anthropic API key",
        SummaryProviderDisclosure.OpenRouter => "Or paste an OpenRouter API key",
        _ => "OpenAI API key"
    };

    public string SetupApiKeyPlaceholder => SetupProviderId switch
    {
        SummaryProviderDisclosure.Anthropic => "sk-ant-api...",
        SummaryProviderDisclosure.OpenRouter => "sk-or-...",
        _ => "sk-..."
    };

    public string SetupApiKeyHelp => SetupProviderId == SummaryProviderDisclosure.Anthropic
        ? "Use an API key from the Claude Console. This is separate from Claude Code sign-in. Stored in Windows Credential Manager."
        : "Stored in Windows Credential Manager.";

    private string? SetupKeySecret => SetupProviderId switch
    {
        SummaryProviderDisclosure.OpenAI => SettingsStore.OpenAISecretKey,
        SummaryProviderDisclosure.Anthropic => SettingsStore.AnthropicSecretKey,
        SummaryProviderDisclosure.OpenRouter => SettingsStore.OpenRouterSecretKey,
        _ => null
    };

    private string SavedSetupKey(MuesliSettings settings) => SetupProviderId switch
    {
        SummaryProviderDisclosure.OpenAI => settings.ResolvedOpenAIApiKey,
        SummaryProviderDisclosure.Anthropic => settings.ResolvedAnthropicApiKey,
        SummaryProviderDisclosure.OpenRouter => settings.ResolvedOpenRouterApiKey,
        _ => ""
    };

    /// <summary>Shows the controls of the selected provider with what is already saved for it.</summary>
    private void RefreshProviderSetup()
    {
        var current = _settings.Load();
        _loadingSetupKey = true;
        try
        {
            SetupApiKey = SavedSetupKey(current);
        }
        finally
        {
            _loadingSetupKey = false;
        }

        RefreshChatGptSetup();
        RefreshOpenRouterSetup();
        foreach (var name in new[]
                 {
                     nameof(ShowChatGptSetup), nameof(ShowOpenRouterSetup), nameof(ShowClaudeCodeSetup),
                     nameof(ShowOllamaSetup), nameof(ShowApiKeySetup), nameof(ShowSummarySettingsLink),
                     nameof(SetupApiKeyHeader), nameof(SetupApiKeyPlaceholder), nameof(SetupApiKeyHelp)
                 })
        {
            OnPropertyChanged(name);
        }

        if (ShowClaudeCodeSetup) _ = CheckClaudeCodeSetupAsync();
    }

    partial void OnSetupApiKeyChanged(string value)
    {
        if (_loadingSetupKey || SetupKeySecret is not { } secret) return;
        try
        {
            _settings.SaveProviderKey(secret, value ?? "");
            RefreshOpenRouterSetup();
        }
        catch (Exception exception)
        {
            ApplyStatus($"The key could not be saved: {exception.Message}");
        }
    }

    private void RefreshChatGptSetup(string? error = null)
    {
        IsChatGptSignedIn = ChatGptAuthManager.Shared?.IsSignedIn == true;
        ChatGptSetupStatus = error ?? (IsChatGptSigningIn ? "Signing in… finish in your browser."
            : IsChatGptSignedIn ? "Signed in to ChatGPT"
            : "Use your ChatGPT Plus or Pro subscription. No API key needed.");
    }

    [RelayCommand]
    private async Task SignInChatGptAsync()
    {
        if (ChatGptAuthManager.Shared is not { } auth || IsChatGptSigningIn) return;
        _chatGptSignIn = new CancellationTokenSource();
        IsChatGptSigningIn = true;
        RefreshChatGptSetup();
        string? error = null;
        try
        {
            await auth.SignInAsync(uri => _ = global::Windows.System.Launcher.LaunchUriAsync(uri), _chatGptSignIn.Token);
        }
        catch (OperationCanceledException) { }
        catch (ChatGptAuthException exception) { error = exception.Message; }
        catch (Exception exception) { error = $"Sign-in failed: {exception.GetType().Name}"; }
        finally
        {
            IsChatGptSigningIn = false;
            _chatGptSignIn = null;
            RefreshChatGptSetup(error);
        }
    }

    private void RefreshOpenRouterSetup(string? error = null)
    {
        IsOpenRouterConnected = !string.IsNullOrWhiteSpace(_settings.Load().ResolvedOpenRouterApiKey);
        OpenRouterSetupStatus = error ?? (IsOpenRouterConnecting ? "Connecting… approve access in your browser."
            : IsOpenRouterConnected ? "OpenRouter connected"
            : "Connect OpenRouter in your browser. muesli receives its own API key after you approve access.");
    }

    [RelayCommand]
    private async Task ConnectOpenRouterAsync()
    {
        if (IsOpenRouterConnecting) return;
        _openRouterSignIn = new CancellationTokenSource();
        IsOpenRouterConnecting = true;
        RefreshOpenRouterSetup();
        string? error = null;
        try
        {
            var key = await new OpenRouterAuthFlow().SignInAsync(
                uri => global::Windows.System.Launcher.LaunchUriAsync(uri).AsTask(), _openRouterSignIn.Token);
            _settings.SaveProviderKey(SettingsStore.OpenRouterSecretKey, key);
        }
        catch (OperationCanceledException) { }
        catch (OpenRouterAuthException exception) { error = exception.Message; }
        catch (Exception exception) { error = $"OpenRouter sign-in failed: {exception.GetType().Name}"; }
        finally
        {
            IsOpenRouterConnecting = false;
            _openRouterSignIn = null;
            if (ShowOpenRouterSetup)
            {
                _loadingSetupKey = true;
                try { SetupApiKey = _settings.Load().ResolvedOpenRouterApiKey; }
                finally { _loadingSetupKey = false; }
            }

            RefreshOpenRouterSetup(error);
        }
    }

    [RelayCommand]
    private async Task CheckClaudeCodeSetupAsync()
    {
        ClaudeCodeSetupStatus = "Checking Claude Code…";
        var path = _settings.Load().ClaudeCodeExecutablePath;
        ClaudeCodeStatus status;
        try
        {
            status = await Task.Run(() => ClaudeCodeSummarizer.CheckStatusAsync(path));
        }
        catch (Exception)
        {
            status = ClaudeCodeStatus.Unknown;
        }

        ClaudeCodeSetupStatus = status switch
        {
            ClaudeCodeStatus.SignedIn => "Connected to Claude Code",
            ClaudeCodeStatus.SignedOut => "Claude Code is installed but not signed in. Run \"claude auth login\" in a terminal, then check again.",
            ClaudeCodeStatus.Unknown => "muesli could not check Claude Code's sign-in status.",
            _ => "Claude Code was not found on this PC. Install it, then check again."
        };
    }
}
