using System.IO;

namespace Muesli.Windows.Services;

internal static class MuesliPathService
{
    public static string UserProfileDirectory
    {
        get
        {
            var profileOverride = Environment.GetEnvironmentVariable("MUESLI_PROFILE_ROOT");
            if (!string.IsNullOrWhiteSpace(profileOverride) && Path.IsPathRooted(profileOverride))
            {
                return Path.GetFullPath(profileOverride);
            }

            var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Environment.GetEnvironmentVariable("USERPROFILE");
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                var drive = Environment.GetEnvironmentVariable("HOMEDRIVE");
                var homePath = Environment.GetEnvironmentVariable("HOMEPATH");
                if (!string.IsNullOrWhiteSpace(drive) && !string.IsNullOrWhiteSpace(homePath))
                {
                    path = drive + homePath;
                }
            }

            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            {
                throw new InvalidOperationException("Windows user profile directory is unavailable; model cache paths cannot be resolved safely.");
            }

            return Path.GetFullPath(path);
        }
    }
}
