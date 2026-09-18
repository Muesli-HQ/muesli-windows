using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Muesli.Windows.Features;

public sealed class FeatureActionEventArgs(
    RoutedEvent routedEvent,
    object source,
    string actionName,
    object sender,
    RoutedEventArgs eventArgs) : RoutedEventArgs(routedEvent, source)
{
    public string ActionName { get; } = actionName;
    public object Sender { get; } = sender;
    public RoutedEventArgs EventArgs { get; } = eventArgs;
}

public static class FeatureAction
{
    public static readonly DependencyProperty NameProperty = DependencyProperty.RegisterAttached(
        "Name",
        typeof(string),
        typeof(FeatureAction),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty MouseUpNameProperty = DependencyProperty.RegisterAttached(
        "MouseUpName",
        typeof(string),
        typeof(FeatureAction),
        new FrameworkPropertyMetadata(string.Empty));

    public static void SetName(DependencyObject element, string value) =>
        element.SetValue(NameProperty, value);

    public static string GetName(DependencyObject element) =>
        (string?)element.GetValue(NameProperty) ?? string.Empty;

    public static void SetMouseUpName(DependencyObject element, string value) =>
        element.SetValue(MouseUpNameProperty, value);

    public static string GetMouseUpName(DependencyObject element) =>
        (string?)element.GetValue(MouseUpNameProperty) ?? string.Empty;
}

public abstract class FeatureViewBase : System.Windows.Controls.UserControl
{
    public static readonly RoutedEvent ActionRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(ActionRequested),
        RoutingStrategy.Bubble,
        typeof(EventHandler<FeatureActionEventArgs>),
        typeof(FeatureViewBase));

    public event EventHandler<FeatureActionEventArgs> ActionRequested
    {
        add => AddHandler(ActionRequestedEvent, value);
        remove => RemoveHandler(ActionRequestedEvent, value);
    }

    protected void ForwardClick(object sender, RoutedEventArgs e) => RaiseAction(sender, e);

    protected void ForwardMouseButton(object sender, MouseButtonEventArgs e) => RaiseAction(sender, e);

    protected void ForwardPasswordChanged(object sender, RoutedEventArgs e) => RaiseAction(sender, e);

    protected void ForwardLostFocus(object sender, RoutedEventArgs e) => RaiseAction(sender, e);

    protected void ForwardValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => RaiseAction(sender, e);

    private void RaiseAction(object sender, RoutedEventArgs e)
    {
        var dependencyObject = (DependencyObject)sender;
        var actionName = e.RoutedEvent?.Name.Contains("MouseUp", StringComparison.Ordinal) == true
            ? FeatureAction.GetMouseUpName(dependencyObject)
            : FeatureAction.GetName(dependencyObject);
        if (string.IsNullOrWhiteSpace(actionName))
            actionName = FeatureAction.GetName(dependencyObject);
        if (!string.IsNullOrWhiteSpace(actionName))
        {
            RaiseEvent(new FeatureActionEventArgs(
                ActionRequestedEvent,
                this,
                actionName,
                sender,
                e));
        }
    }
}
