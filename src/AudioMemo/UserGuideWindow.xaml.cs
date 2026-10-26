using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AudioMemo.Core.Localization;

namespace AudioMemo;

internal sealed record UserGuideTopic(string Id, string Title, string Body);

public partial class UserGuideWindow : Window
{
    internal IReadOnlyList<UserGuideTopic> Topics { get; }

    public UserGuideWindow()
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(UiText.Current.Culture.Name);
        Topics = new[] { "QuickStart", "Recording", "Speech", "Models", "MainPoints", "Notes", "Navigation",
            "Review", "Corrections", "Silence", "Library", "Workspace", "Recovery", "Troubleshooting" }
            .Select(id => new UserGuideTopic(id, UiText.Get("Guide" + id + "Title"), UiText.Get("Guide" + id + "Body")))
            .ToArray();
        GuideTopics.ItemsSource = Topics;
        GuideTopics.SelectedIndex = 0;
    }

    private void GuideBody_TextChanged(object sender, TextChangedEventArgs e)
    {
        var body = (TextBox)sender;
        body.Select(0, 0);
        body.ScrollToHome();
    }
}
