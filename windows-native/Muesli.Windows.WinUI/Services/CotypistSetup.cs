using System.Runtime.InteropServices;
using Microsoft.Win32;
using Muesli.Windows.Cotypist;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Port of macOS CotypistSetup (2f10b23d): turning Cotypist on and off, and its models. "On" means
/// the Cotypist input method is in the user's keyboard list and selected as the default.
///
/// macOS can't add an input source from code, so it opens Keyboard settings with a guide and retries.
/// Windows lets an app add a registered text service to the user's list (InstallLayoutOrTip), so
/// turning on is one call and there is no guide state. Registration itself is the elevated
/// scripts/cotypist-register.ps1; without it Settings doesn't show Cotypist at all.
/// </summary>
public sealed class CotypistSetup
{
    private const string Tip = "0409:{847EA321-420D-4289-8CA1-6C2623C50551}{39722C55-F0B2-47FB-81F5-D6BCBCAB2B1A}";
    private const uint IlotUninstall = 0x1;

    public enum ModelState { Ready, Missing, Unavailable, Downloading, Failed }

    public sealed record ModelStatus(ModelState State, double Fraction = 0, string? Error = null);

    private readonly CotypistService _service;
    private readonly Func<MuesliSettings> _settings;
    private readonly Action<string> _log;
    private readonly Dictionary<string, ModelStatus> _models = [];
    private readonly Dictionary<string, CancellationTokenSource> _downloads = [];
    private readonly Lock _gate = new();

    /// <summary>Raised on any thread when the state or a model changes.</summary>
    public event Action? Changed;

    public CotypistSetup(CotypistService service, Func<MuesliSettings> settings, Action<string> log)
    {
        _service = service;
        _settings = settings;
        _log = log;
    }

    /// <summary>The input method is in the user's keyboard list (Settings › Time &amp; language).</summary>
    public bool IsOn
    {
        get
        {
            using var profiles = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
            if (profiles is null) return false;
            foreach (var language in profiles.GetSubKeyNames())
            {
                using var key = profiles.OpenSubKey(language);
                if (key?.GetValueNames().Any(name => name.Equals(Tip, StringComparison.OrdinalIgnoreCase)) == true) return true;
            }
            return false;
        }
    }

    /// <summary>The Settings toggle. The model is loaded and freed here, not when the keyboard is
    /// switched: a quick switch away and back keeps suggestions instant.</summary>
    public void SetOn(bool on)
    {
        if (on)
        {
            DownloadIfNeeded();
            if (!InstallLayoutOrTip(Tip, 0) || !SetDefaultLayoutOrTip(Tip, 0))
                _log($"Cotypist could not be added to the keyboard list. error={Marshal.GetLastWin32Error()}");
            _service.ModelChanged();
        }
        else
        {
            if (!InstallLayoutOrTip(Tip, IlotUninstall)) _log($"Cotypist could not be removed from the keyboard list. error={Marshal.GetLastWin32Error()}");
            _service.Unload();
        }
        Changed?.Invoke();
    }

    /// <summary>After "Use for Cotypist": load the new model now, not on the next keystroke.</summary>
    public void ModelChanged()
    {
        DownloadIfNeeded();
        _service.ModelChanged();
    }

    public CotypistModel SelectedModel => CotypistModels.Get(_settings().CotypistModel);

    public ModelStatus StatusOf(CotypistModel model)
    {
        lock (_gate)
            if (_models.TryGetValue(model.Id, out var status)) return status;
        return DiskStatus(model);
    }

    private static ModelStatus DiskStatus(CotypistModel model) =>
        new(model.IsDownloaded ? ModelState.Ready : model.Release is null ? ModelState.Unavailable : ModelState.Missing);

    /// <summary>Downloads the selected model when it's missing.</summary>
    public void DownloadIfNeeded() => Download(SelectedModel);

    /// <summary>Downloads through the shared, resumable, SHA-256-verified model installer.</summary>
    public void Download(CotypistModel model)
    {
        if (!CotypistAvailability.IsHelperBundled || model.Release is not { } release || model.IsDownloaded) return;
        var cancel = new CancellationTokenSource();
        lock (_gate)
        {
            if (_downloads.ContainsKey(model.Id)) return;
            _downloads[model.Id] = cancel;
            _models[model.Id] = new(ModelState.Downloading);
        }
        Changed?.Invoke();
        var progress = new Progress<ModelDownloadProgress>(update =>
        {
            lock (_gate)
            {
                if (_models.GetValueOrDefault(model.Id)?.State != ModelState.Downloading) return;
                _models[model.Id] = new(ModelState.Downloading, update.Percent is int percent ? percent / 100.0 : 0);
            }
            Changed?.Invoke();
        });
        _ = Task.Run(async () =>
        {
            ModelStatus result;
            try
            {
                await new CleanupModelInstaller(release, model.Path).PrepareAsync(progress, cancel.Token);
                result = model.IsDownloaded ? DiskStatus(model) : new(ModelState.Failed, Error: "The download didn't complete.");
                if (result.State == ModelState.Ready && model.Id == SelectedModel.Id && IsOn) _service.ModelChanged();
            }
            catch (OperationCanceledException)
            {
                result = DiskStatus(model);
            }
            catch (Exception error)
            {
                _log($"Cotypist model download failed. model={model.Id} category={error.GetType().Name}");
                result = new(ModelState.Failed, Error: error.Message);
            }
            lock (_gate)
            {
                _models[model.Id] = result;
                _downloads.Remove(model.Id);
            }
            cancel.Dispose();
            Changed?.Invoke();
        });
    }

    public void CancelDownload(CotypistModel model)
    {
        lock (_gate)
            if (_downloads.TryGetValue(model.Id, out var cancel)) cancel.Cancel();
    }

    /// <summary>Removes a model's files. The selected model stays: Cotypist would have nothing to run.</summary>
    public async Task DeleteAsync(CotypistModel model)
    {
        lock (_gate)
            if (model.Id == SelectedModel.Id || _downloads.ContainsKey(model.Id) || model.Release is null) return;
        await new CleanupModelInstaller(model.Release!, model.Path).DeleteAsync();
        lock (_gate) _models.Remove(model.Id);
        Changed?.Invoke();
    }

    [DllImport("input.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InstallLayoutOrTip(string profile, uint flags);

    [DllImport("input.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultLayoutOrTip(string profile, uint flags);
}
