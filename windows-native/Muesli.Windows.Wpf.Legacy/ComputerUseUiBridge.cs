using Muesli.Windows.Services;
using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Muesli.Windows;

internal sealed class DelegateComputerUseStatusSink(Action<ComputerUseStatus> publish) : IComputerUseStatusSink
{
    public void Publish(ComputerUseStatus status) => publish(status);
}

internal sealed class DelegateComputerUseConfirmation(
    Func<ComputerUseAction, CancellationToken, Task<bool>> confirm) : IComputerUseConfirmation
{
    public Task<bool> ConfirmAsync(ComputerUseAction action, CancellationToken cancellationToken) =>
        confirm(action, cancellationToken);
}

internal sealed class DelegateComputerUseActionVisualizer(
    Func<ComputerUseAction, ComputerUseElement?, CancellationToken, Task> show) : IComputerUseActionVisualizer
{
    public Task ShowAsync(
        ComputerUseAction action,
        ComputerUseElement? resolvedTarget,
        CancellationToken cancellationToken) => show(action, resolvedTarget, cancellationToken);
}

/// <summary>
/// A modeless, cancellable confirmation surface. It keeps the WPF dispatcher and the global Stop
/// control responsive, and previews the exact proposed target/value locally without persisting it.
/// </summary>
internal static class ComputerUseConfirmationPrompt
{
    public static Task<bool> ShowAsync(Window owner, ComputerUseAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new Window
        {
            Title = "Confirm Computer Use action",
            Width = 620,
            Height = 470,
            MinWidth = 480,
            MinHeight = 360,
            WindowStartupLocation = owner.IsVisible && owner.WindowState != WindowState.Minimized
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true,
            ResizeMode = ResizeMode.CanResize,
            Background = System.Windows.SystemColors.WindowBrush
        };
        if (owner.IsVisible && owner.WindowState != WindowState.Minimized) window.Owner = owner;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock
        {
            Text = $"Computer Use proposes: {action.Kind}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.SystemColors.WindowTextBrush
        });
        var warning = new TextBlock
        {
            Text = $"Risk: {action.Risk}. Review the exact action below. This preview stays local and is not saved.",
            Margin = new Thickness(0, 12, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.SystemColors.WindowTextBrush
        };
        Grid.SetRow(warning, 1);
        root.Children.Add(warning);
        var preview = new WpfTextBox
        {
            Text = BuildPreview(action),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Background = System.Windows.SystemColors.ControlLightLightBrush,
            Foreground = System.Windows.SystemColors.WindowTextBrush
        };
        Grid.SetRow(preview, 2);
        root.Children.Add(preview);
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var deny = new WpfButton { Content = "Deny / Stop", MinWidth = 120, Padding = new Thickness(14, 8, 14, 8), IsCancel = true };
        var allow = new WpfButton { Content = "Allow once", MinWidth = 120, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(12, 0, 0, 0), IsDefault = true };
        buttons.Children.Add(deny);
        buttons.Children.Add(allow);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        window.Content = root;

        void Resolve(bool approved)
        {
            if (!completion.TrySetResult(approved)) return;
            window.Close();
        }
        deny.Click += (_, _) => Resolve(false);
        allow.Click += (_, _) => Resolve(true);
        window.Closed += (_, _) => completion.TrySetResult(false);
        var registration = cancellationToken.Register(() => window.Dispatcher.BeginInvoke(() => Resolve(false)));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        window.Show();
        window.Activate();
        return completion.Task;
    }

    internal static string BuildPreview(ComputerUseAction action) =>
        ComputerUseConfirmationPreview.Build(action);
}
