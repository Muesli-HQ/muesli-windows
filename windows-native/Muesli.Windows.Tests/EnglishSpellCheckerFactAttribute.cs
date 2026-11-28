using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Skips a test that needs an English spell checker registered with Windows. The API reports
/// support per language and does not promise English on every installation; without one,
/// <see cref="WindowsSpellChecker"/> follows its documented fallback (every word unknown).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EnglishSpellCheckerFactAttribute : FactAttribute
{
    public EnglishSpellCheckerFactAttribute()
    {
        if (!new WindowsSpellChecker().IsAvailable)
        {
            Skip = "No English spell checker (en-US, en-GB or en) is registered with Windows on this machine.";
        }
    }
}
