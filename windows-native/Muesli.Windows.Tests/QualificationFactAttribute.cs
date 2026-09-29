namespace Muesli.Windows.Tests;

/// <summary>
/// Marks a real-fixture qualification test as skipped when its required directory is not
/// configured. Unlike an early return inside the test, the missing prerequisite is visible in
/// test results and cannot be mistaken for a passing qualification run.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class QualificationFactAttribute : FactAttribute
{
    public QualificationFactAttribute(string environmentVariable)
    {
        var directory = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Skip = $"Set {environmentVariable} to an existing qualification-fixture directory.";
        }
    }
}
