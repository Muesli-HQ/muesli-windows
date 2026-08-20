namespace Muesli.Windows.UITests;

/// <summary>
/// xUnit fact that is skipped when no interactive desktop is available,
/// unless <see cref="UiAutomationEnvironment.EnableVariable"/> forces a run.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UiAutomationFactAttribute : FactAttribute
{
    public UiAutomationFactAttribute()
    {
        if (UiAutomationEnvironment.SkipReason is { } reason)
            Skip = reason;
    }
}
