using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

public sealed class WinUiSettingsContext
{
    private readonly SettingsStore _store;
    private readonly ISecretStore _secrets;

    public WinUiSettingsContext(WinUiLibraryContext library, ISecretStore? secrets = null)
    {
        _secrets = secrets ?? new WindowsCredentialSecretStore(library.Profile.RootDirectory);
        _store = new SettingsStore(library.Profile.SettingsPath, _secrets);
    }

    public event EventHandler<MuesliSettings>? Changed;

    public MuesliSettings Load() => _store.Load();

    public void Save(MuesliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_store.LastWarning is { } warning)
            throw new InvalidOperationException(warning);
        _store.Save(settings);
        Changed?.Invoke(this, settings);
    }

    public void SaveProviderKeys(string openAI, string openRouter)
    {
        if (_store.LastWarning is { } warning)
            throw new InvalidOperationException(warning);
        _secrets.Write(SettingsStore.OpenAISecretKey, openAI.Trim());
        _secrets.Write(SettingsStore.OpenRouterSecretKey, openRouter.Trim());
    }
}
