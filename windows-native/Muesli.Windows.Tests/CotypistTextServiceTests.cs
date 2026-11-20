using System.Runtime.InteropServices;

namespace Muesli.Windows.Tests;

/// <summary>
/// Loads the built TSF helper without registering it: its class factory makes a text service that
/// TSF can activate, and refuses any other class.
/// </summary>
public sealed class CotypistTextServiceTests
{
    private static readonly string Built = FindBuilt();

    private static string FindBuilt()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Muesli.Cotypist.TextService", "bin", "x64", "Muesli.Cotypist.TextService.dll");
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    private sealed class TextServiceBuiltFactAttribute : FactAttribute
    {
        public TextServiceBuiltFactAttribute()
        {
            if (!File.Exists(Built)) Skip = "Build windows-native/Muesli.Cotypist.TextService (scripts/cotypist-register.ps1) first.";
        }
    }

    private delegate int GetClassObject(in Guid clsid, in Guid iid, out IntPtr factory);
    private delegate int CreateInstance(IntPtr self, IntPtr outer, in Guid iid, out IntPtr instance);

    [TextServiceBuiltFact]
    public void ItsFactoryMakesATextInputProcessorAndNothingElse()
    {
        var library = NativeLibrary.Load(Built);
        var getClassObject = Marshal.GetDelegateForFunctionPointer<GetClassObject>(NativeLibrary.GetExport(library, "DllGetClassObject"));
        var classFactory = new Guid("00000001-0000-0000-C000-000000000046");
        var textInputProcessorEx = new Guid("6E4E2102-F9CD-433D-B496-303CE03A6507"); // ITfTextInputProcessorEx
        var keyEventSink = new Guid("AA80E7F5-2021-11D2-93E0-0060B067B86E"); // ITfKeyEventSink

        Assert.Equal(unchecked((int)0x80040111), getClassObject(Guid.NewGuid(), classFactory, out _)); // CLASS_E_CLASSNOTAVAILABLE
        Assert.Equal(0, getClassObject(new Guid("847EA321-420D-4289-8CA1-6C2623C50551"), classFactory, out var factory));

        // IClassFactory vtable: QueryInterface, AddRef, Release, CreateInstance, LockServer.
        var create = Marshal.GetDelegateForFunctionPointer<CreateInstance>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * IntPtr.Size));
        Assert.Equal(0, create(factory, IntPtr.Zero, textInputProcessorEx, out var service));
        Assert.Equal(0, Marshal.QueryInterface(service, keyEventSink, out var sink));
        Marshal.Release(sink);
        Marshal.Release(service);
        Marshal.Release(factory);
    }
}
