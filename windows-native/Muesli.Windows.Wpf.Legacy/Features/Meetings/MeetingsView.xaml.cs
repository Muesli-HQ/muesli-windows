using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Meetings;

public partial class MeetingsView : FeatureViewBase
{
    public MeetingsView()
    {
        InitializeComponent();
    }

    public Grid BrowserRoot => MeetingsBrowserView;
    public System.Windows.Controls.Panel HeaderActions => MeetingsHeaderActions;
}
