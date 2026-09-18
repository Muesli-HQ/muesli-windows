using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Settings;

public partial class SettingsView : FeatureViewBase
{
    public SettingsView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => SettingsPage;
    public System.Windows.Controls.PasswordBox OpenAIApiKeyInput => OpenAIApiKeyBox;
    public System.Windows.Controls.PasswordBox OpenRouterApiKeyInput => OpenRouterApiKeyBox;
    public System.Windows.Controls.TabControl SettingsTabsControl => SettingsTabs;
}
