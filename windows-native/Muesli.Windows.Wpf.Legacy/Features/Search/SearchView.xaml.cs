using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Search;

public partial class SearchView : FeatureViewBase
{
    public SearchView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => SearchPage;
    public System.Windows.Controls.TextBox MainSearchInputControl => MainSearchInput;
}
