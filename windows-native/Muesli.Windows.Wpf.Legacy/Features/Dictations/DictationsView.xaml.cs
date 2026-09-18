using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Dictations;

public partial class DictationsView : FeatureViewBase
{
    public DictationsView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => DictationsPage;
}
