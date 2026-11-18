using System.IO;
using System.Text;

namespace Muesli.Windows.Services.Persistence;

/// <summary>No library payload: survives a lost database so retained JSON cannot become live again.</summary>
internal static class SqliteAuthorityMarker
{
    public const string FileName = "sqlite-authority";
    private const string Contents = "Muesli SQLite authority v1\n";
    public static string PathFor(string directory) => Path.Combine(directory, FileName);

    public static bool Exists(string directory)
    {
        var path = PathFor(directory);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            File.ReadAllText(path) != Contents)
            throw new IOException("The SQLite authority marker cannot be read safely. Recovery is required.");
        return true;
    }

    public static void Record(string directory)
    {
        if (Exists(directory)) return;
        var pending = PathFor(directory) + ".pending";
        try
        {
            if (File.Exists(pending) && (File.GetAttributes(pending) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The pending authority marker is unsafe.");
            using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(Encoding.UTF8.GetBytes(Contents));
                stream.Flush(true);
            }
            File.Move(pending, PathFor(directory));
        }
        finally
        {
            if (File.Exists(pending) && (File.GetAttributes(pending) & FileAttributes.ReparsePoint) == 0)
                File.Delete(pending);
        }
    }
}
