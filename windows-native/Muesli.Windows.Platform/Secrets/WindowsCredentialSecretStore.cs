using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Muesli.Windows.Services;

public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private readonly string _targetPrefix;

    public WindowsCredentialSecretStore(string? profileRoot = null)
    {
        var production = Muesli.Windows.Core.Profiles.MuesliProfilePaths.Production().RootDirectory;
        var root = Path.GetFullPath(profileRoot ?? production).TrimEnd(Path.DirectorySeparatorChar);
        _targetPrefix = string.Equals(root, production, StringComparison.OrdinalIgnoreCase)
            ? "Muesli.Windows/"
            : "Muesli.Windows/Profile/" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))) + "/";
    }

    public string? Read(string key)
    {
        if (!CredRead(Target(key), CredTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 ? null : throw new Win32Exception(error, "Could not read the protected Muesli credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Delete(key);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(key),
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the protected Muesli credential.");
            }
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                Marshal.WriteByte(blob, index, 0);
            }
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(Target(key), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error, "Could not delete the protected Muesli credential.");
            }
        }
    }

    public bool IsConfigured(string key) => !string.IsNullOrWhiteSpace(Read(key));

    private string Target(string key) => $"{_targetPrefix}{key}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
