using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.About;

public partial class AboutView : FeatureViewBase
{
    public AboutView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => AboutPage;
}
