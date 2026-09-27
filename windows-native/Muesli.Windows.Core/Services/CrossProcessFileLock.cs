using System.Diagnostics;
using System.IO;

namespace Muesli.Windows.Services;

internal static class CrossProcessFileLock
{
    public static async Task<FileStream> AcquireAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose | FileOptions.WriteThrough);
            }
            catch (IOException) when (waiting.Elapsed < timeout)
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (IOException exception)
            {
                throw new TimeoutException($"Timed out waiting for the model setup lock '{Path.GetFileName(path)}'.", exception);
            }
        }
    }
}
