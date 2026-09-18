using Muesli.Windows.Core.Contracts;
using Windows.ApplicationModel;

namespace Muesli.Windows.WinUI.Services;

public sealed class WinUiStartupRegistrationService : IStartupRegistrationService
{
    public const string TaskId = "MuesliWinUiStartup";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await StartupTask.GetAsync(TaskId);
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = await StartupTask.GetAsync(TaskId);
        if (!enabled)
        {
            task.Disable();
            return;
        }

        var state = await task.RequestEnableAsync();
        if (state is not (StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
        {
            throw new InvalidOperationException(state == StartupTaskState.DisabledByUser
                ? "Windows has disabled startup for Muesli. Re-enable it in Windows Settings > Apps > Startup."
                : $"Windows did not enable the startup task ({state}).");
        }
    }
}
