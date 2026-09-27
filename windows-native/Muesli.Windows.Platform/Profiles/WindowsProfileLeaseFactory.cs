using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Muesli.Windows.Core.Profiles;

namespace Muesli.Windows.Platform.Profiles;

public sealed class WindowsProfileLeaseFactory : IMuesliProfileLeaseFactory
{
    public static WindowsProfileLeaseFactory Instance { get; } = new();

    private WindowsProfileLeaseFactory()
    {
    }

    public IMuesliProfileLease? TryAcquire(IMuesliProfilePaths paths, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var root = Path.GetFullPath(paths.RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}\n{root}")))[..32];
        var mutex = new Mutex(initiallyOwned: false, $"Local\\Muesli.Profile.{hash}");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            return acquired ? new WindowsProfileLease(paths.RootDirectory, mutex) : null;
        }
        finally
        {
            if (!acquired)
            {
                mutex.Dispose();
            }
        }
    }

    private sealed class WindowsProfileLease : IMuesliProfileLease
    {
        private readonly Mutex _mutex;
        private readonly int _owningThreadId = Environment.CurrentManagedThreadId;
        private bool _disposed;

        public WindowsProfileLease(string rootDirectory, Mutex mutex)
        {
            RootDirectory = Path.GetFullPath(rootDirectory);
            _mutex = mutex;
        }

        public string RootDirectory { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (Environment.CurrentManagedThreadId != _owningThreadId)
            {
                throw new InvalidOperationException("The profile lease must be released on the thread that acquired it.");
            }

            _disposed = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            finally
            {
                _mutex.Dispose();
            }
        }
    }
}
