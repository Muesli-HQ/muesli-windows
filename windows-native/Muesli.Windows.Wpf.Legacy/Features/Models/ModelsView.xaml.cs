using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Models;

public partial class ModelsView : FeatureViewBase
{
    public ModelsView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => ModelsPage;
}
