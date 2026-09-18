using System.IO;
using System.Text.RegularExpressions;
using Sentry;

namespace Muesli.Windows.Services;

internal static class SentryScrubber
{
    private static readonly string[] SensitiveBreadcrumbTokens =
    {
        "transcript", "dictation", "meeting", "capture", "audio",
        "OpenAIApiKey", "OpenRouterApiKey", "HF_TOKEN"
    };

    private static readonly string[] SensitiveExtraKeys =
    {
        "transcript", "dictation", "audio", "apikey", "api_key", "token", "secret"
    };

    private static readonly Regex CapturesPathRegex = BuildPathRegex("captures");
    private static readonly Regex CacheMuesliPathRegex = BuildCachePathRegex();
    private static readonly Regex UserNameRegex = new(
        @"(?<![A-Za-z0-9_])" + Regex.Escape(Environment.UserName) + @"(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static SentryEvent? Scrub(SentryEvent evt, SentryHint hint)
    {
        evt.User = new SentryUser();

        if (!string.IsNullOrEmpty(evt.Message?.Message))
        {
            evt.Message.Message = ScrubText(evt.Message.Message);
        }

        if (!string.IsNullOrEmpty(evt.Message?.Formatted))
        {
            evt.Message.Formatted = ScrubText(evt.Message.Formatted);
        }

        if (evt.SentryExceptions is not null)
        {
            foreach (var ex in evt.SentryExceptions)
            {
                if (!string.IsNullOrEmpty(ex.Value))
                {
                    ex.Value = ScrubText(ex.Value);
                }
            }
        }

        foreach (var key in evt.Extra.Keys.ToList())
        {
            if (LooksSensitive(key))
            {
                evt.SetExtra(key, "<scrubbed>");
                continue;
            }

            if (evt.Extra[key] is string strValue)
            {
                evt.SetExtra(key, ScrubText(strValue));
            }
        }

        return evt;
    }

    public static Breadcrumb? ScrubBreadcrumb(Breadcrumb breadcrumb, SentryHint hint)
    {
        if (!string.IsNullOrEmpty(breadcrumb.Message) &&
            ContainsAny(breadcrumb.Message, SensitiveBreadcrumbTokens))
        {
            return null;
        }

        if (breadcrumb.Data is null || breadcrumb.Data.Count == 0)
        {
            return breadcrumb;
        }

        foreach (var key in breadcrumb.Data.Keys.ToList())
        {
            if (LooksSensitive(key))
            {
                return null;
            }
        }

        return breadcrumb;
    }

    private static Regex BuildPathRegex(string subdir)
    {
        var appData = Regex.Escape(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        var pattern = $@"{appData}[\\/]+muesli[\\/]+{subdir}[\\/]+[^\s""']*";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private static Regex BuildCachePathRegex()
    {
        var profile = Regex.Escape(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var pattern = $@"{profile}[\\/]+\.cache[\\/]+muesli[\\/]+[^\s""']*";
        return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    private static string ScrubText(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = CapturesPathRegex.Replace(input, "<captures>");
        result = CacheMuesliPathRegex.Replace(result, "<cache>");
        if (!string.IsNullOrEmpty(Environment.UserName))
        {
            result = UserNameRegex.Replace(result, "<user>");
        }
        return result;
    }

    private static bool LooksSensitive(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var lower = key.ToLowerInvariant();
        foreach (var token in SensitiveExtraKeys)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsAny(string source, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (source.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
