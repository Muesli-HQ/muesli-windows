using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Shortcuts;

public partial class ShortcutsView : FeatureViewBase
{
    public ShortcutsView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => ShortcutsPage;
}
