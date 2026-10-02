using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Controls;

/// <summary>Formatting commands keep notes as portable Markdown, including existing whitespace.</summary>
public sealed class NotesFormattingBar : UserControl
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(nameof(Editor), typeof(MarkdownNotesEditor),
        typeof(NotesFormattingBar), new PropertyMetadata(null, EditorChanged));
    public MarkdownNotesEditor? Editor { get => (MarkdownNotesEditor?)GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    public NotesFormattingBar()
    {
        Content = _buttons;
        HorizontalAlignment = HorizontalAlignment.Left;
        Add("Bold", "\uE8DD", "**", VirtualKey.B);
        Add("Italic", "\uE8DB", "_", VirtualKey.I);
        Add("Heading", "\uE8D2", "# ");
        Add("Bullets", "\uE8FD", "- ");
        Add("Checklist", "\uE73A", "- [ ] ");
    }

    private void Add(string label, string glyph, string marker, VirtualKey? key = null)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Height = 32, Width = 32,
            MinHeight = 32, MinWidth = 32, Padding = new Thickness(0), Style = (Style)Application.Current.Resources["MuesliGhostButtonStyle"] };
        AutomationProperties.SetAutomationId(button, $"NotesFormat{label}");
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => Format(marker);
        if (key is { } shortcut)
        {
            var accelerator = new KeyboardAccelerator { Key = shortcut, Modifiers = VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) => { Format(marker); args.Handled = true; };
            button.KeyboardAccelerators.Add(accelerator);
        }
        _buttons.Children.Add(button);
    }

    private static void EditorChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var bar = (NotesFormattingBar)sender;
        bar.IsEnabled = bar.Editor?.IsEnabled == true;
        if (args.OldValue is MarkdownNotesEditor oldEditor) oldEditor.IsEnabledChanged -= bar.EditorEnabledChanged;
        if (args.NewValue is MarkdownNotesEditor newEditor) newEditor.IsEnabledChanged += bar.EditorEnabledChanged;
        foreach (var button in bar._buttons.Children.OfType<Button>())
            foreach (var accelerator in button.KeyboardAccelerators) accelerator.ScopeOwner = bar.Editor;
    }

    private void EditorEnabledChanged(object sender, DependencyPropertyChangedEventArgs args) => IsEnabled = Editor?.IsEnabled == true;

    private void Format(string marker)
    {
        if (Editor is not { IsEnabled: true } editor || editor.IsReadOnly) return;
        editor.Format(marker);
    }
}
