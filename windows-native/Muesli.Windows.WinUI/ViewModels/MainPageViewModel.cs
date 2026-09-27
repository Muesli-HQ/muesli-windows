using CommunityToolkit.Mvvm.ComponentModel;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

/// <summary>Shared shell state. Pages reload through this context and never fabricate history.</summary>
public partial class MainPageViewModel : ObservableObject
{
    private readonly WinUiLibraryContext _library;

    public MainPageViewModel(WinUiLibraryContext library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Snapshot = library.ReadSnapshot();
    }

    [ObservableProperty]
    public partial WinUiLibrarySnapshot Snapshot { get; private set; }

    public void Refresh() => Snapshot = _library.ReadSnapshot();
}
