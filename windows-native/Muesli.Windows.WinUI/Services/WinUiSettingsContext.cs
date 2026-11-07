using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// The app's single source of settings: an immutable in-memory snapshot, read from disk once and
/// replaced atomically on every save.
/// </summary>
/// <remarks>
/// <see cref="Load"/> used to read the JSON file and three Windows Credential Manager entries on every
/// call, and it was called on hotkey events, indicator updates, live-transcript ticks and per model
/// snapshot, often on the UI thread. Reads are now a field access and safe from any thread, because
/// <see cref="MuesliSettings"/> is an immutable record that callers change with <c>with</c>.
/// The app owns the settings file; another process editing it while the app runs is not picked up
/// until restart.
/// </remarks>
public sealed class WinUiSettingsContext
{
    private readonly SettingsStore _store;
    private readonly ISecretStore _secrets;
    private readonly object _gate = new();
    private MuesliSettings? _snapshot;

    public WinUiSettingsContext(WinUiLibraryContext library, ISecretStore? secrets = null)
    {
        _secrets = secrets ?? new WindowsCredentialSecretStore(library.Profile.RootDirectory);
        _store = new SettingsStore(library.Profile.SettingsPath, _secrets);
    }

    public event EventHandler<MuesliSettings>? Changed;

    /// <summary>The current settings snapshot. No I/O after the first call.</summary>
    public MuesliSettings Load() => Volatile.Read(ref _snapshot) ?? LoadFromDisk();

    public void Save(MuesliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MuesliSettings snapshot;
        lock (_gate)
        {
            var current = Load();
            if (_store.LastWarning is { } warning)
                throw new InvalidOperationException(warning);
            // Save is only suppressed together with LastWarning, which was rejected above.
            var persisted = _store.Save(settings) ?? settings;
            // Secrets never round-trip through the JSON file, so the snapshot keeps the resolved keys.
            snapshot = persisted with
            {
                ResolvedOpenAIApiKey = current.ResolvedOpenAIApiKey,
                ResolvedOpenRouterApiKey = current.ResolvedOpenRouterApiKey,
                ResolvedCustomLlmApiKey = current.ResolvedCustomLlmApiKey
            };
            Publish(snapshot);
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SaveProviderKeys(string openAI, string openRouter, string? customLlm = null)
    {
        lock (_gate)
        {
            var current = Load();
            if (_store.LastWarning is { } warning)
                throw new InvalidOperationException(warning);
            var resolvedOpenAI = openAI.Trim();
            var resolvedOpenRouter = openRouter.Trim();
            _secrets.Write(SettingsStore.OpenAISecretKey, resolvedOpenAI);
            _secrets.Write(SettingsStore.OpenRouterSecretKey, resolvedOpenRouter);
            var resolvedCustom = current.ResolvedCustomLlmApiKey;
            if (customLlm is not null)
            {
                resolvedCustom = customLlm.Trim();
                if (resolvedCustom.Length == 0)
                {
                    _secrets.Delete(SettingsStore.CustomLlmSecretKey);
                }
                else
                {
                    _secrets.Write(SettingsStore.CustomLlmSecretKey, resolvedCustom);
                }
            }

            Publish(current with
            {
                ResolvedOpenAIApiKey = resolvedOpenAI,
                ResolvedOpenRouterApiKey = resolvedOpenRouter,
                ResolvedCustomLlmApiKey = resolvedCustom
            });
        }
    }

    private MuesliSettings LoadFromDisk()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _snapshot) is { } loaded) return loaded;
            var settings = _store.Load();
            Publish(settings);
            return settings;
        }
    }

    private void Publish(MuesliSettings snapshot)
    {
        // Keep the native recognizer language selection in step with the saved choices.
        TranscriptionLanguageSelection.Apply(snapshot.ModelLanguages);
        Volatile.Write(ref _snapshot, snapshot);
    }
}
