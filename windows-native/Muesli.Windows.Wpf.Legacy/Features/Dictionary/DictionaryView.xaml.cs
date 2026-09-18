using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Dictionary;

public partial class DictionaryView : FeatureViewBase
{
    public DictionaryView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => DictionaryPage;
    public System.Windows.Controls.TextBox PhraseInput => DictionaryPhraseBox;
    public System.Windows.Controls.TextBox ReplacementInput => DictionaryReplacementBox;
    public System.Windows.Controls.Slider ThresholdInput => DictionaryThresholdSlider;
}
