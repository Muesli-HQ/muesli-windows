using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.System;

namespace Muesli.Windows.WinUI;

/// <summary>Asks the user a question a Computer Use run needs answered ("Which Mom?"), with the
/// planner's suggested answers as buttons and a box for any other answer.</summary>
public sealed partial class ComputerUseQuestionWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public ComputerUseQuestionWindow(string question, IReadOnlyList<string> options)
    {
        InitializeComponent();
        QuestionText.Text = question;
        foreach (var option in options.Where(option => !string.IsNullOrWhiteSpace(option)))
        {
            var button = new Button { Content = option, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => Finish(option);
            OptionsPanel.Children.Add(button);
        }

        var scale = Math.Max(1d, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d);
        var size = new SizeInt32((int)(520 * scale), (int)((330 + 44 * OptionsPanel.Children.Count) * scale));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)?.WorkArea;
        if (area is { Width: > 0, Height: > 0 } bounds)
            AppWindow.MoveAndResize(new RectInt32(bounds.X + Math.Max(0, (bounds.Width - size.Width) / 2),
                bounds.Y + Math.Max(0, (bounds.Height - size.Height) / 2), Math.Min(size.Width, bounds.Width), Math.Min(size.Height, bounds.Height)));
        else
            AppWindow.Resize(size);
        RootBorder.Loaded += (_, _) => (OptionsPanel.Children.FirstOrDefault() as Control ?? AnswerBox).Focus(FocusState.Keyboard);
    }

    /// <summary>The answer, or null when the user stopped the run.</summary>
    public string? Answer { get; private set; }

    private void Finish(string? answer)
    {
        Answer = string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
        Close();
    }

    private void Answer_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AnswerBox.Text)) Finish(AnswerBox.Text);
    }

    private void AnswerBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(AnswerBox.Text)) return;
        e.Handled = true;
        Finish(AnswerBox.Text);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(null);

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Finish(null);
    }
}
