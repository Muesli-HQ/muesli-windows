using System.Windows;
using System.Windows.Controls;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private string? PromptForText(string title, string label, string initialValue)
{
    var dialog = new Window
    {
        Owner = _shell.Window,
        Title = title,
        Width = 360,
        Height = 170,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBaseBrush")
    };
    var input = new System.Windows.Controls.TextBox
    {
        Text = initialValue,
        Height = 36,
        Padding = new Thickness(10, 7, 10, 7),
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        Background = (System.Windows.Media.Brush)FindResource("BackgroundHoverBrush"),
        BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft")
    };
    var save = new System.Windows.Controls.Button
    {
        Content = "Save",
        Width = 90,
        Height = 32,
        Margin = new Thickness(8, 0, 0, 0),
        Style = (Style)FindResource("PrimaryButton")
    };
    var cancel = new System.Windows.Controls.Button
    {
        Content = "Cancel",
        Width = 90,
        Height = 32,
        Style = (Style)FindResource("GhostButton")
    };
    save.Click += (_, _) => dialog.DialogResult = true;
    cancel.Click += (_, _) => dialog.DialogResult = false;
    var buttons = new StackPanel
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        Margin = new Thickness(0, 16, 0, 0)
    };
    buttons.Children.Add(cancel);
    buttons.Children.Add(save);
    var content = new StackPanel
    {
        Margin = new Thickness(18)
    };
    content.Children.Add(new TextBlock
    {
        Text = label,
        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
        Margin = new Thickness(0, 0, 0, 8)
    });
    content.Children.Add(input);
    content.Children.Add(buttons);
    dialog.Content = content;
    input.SelectAll();
    input.Focus();
    return dialog.ShowDialog() == true ? input.Text : null;
}
}
