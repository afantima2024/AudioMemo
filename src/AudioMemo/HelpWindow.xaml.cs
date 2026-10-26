using System.Windows;
using System.Windows.Markup;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class HelpWindow : Window
{
    private readonly Action<Window> showReference;
    internal ShortcutSettings Shortcuts { get; }

    public HelpWindow(ShortcutSettings shortcuts, Action<Window>? showReference = null)
    {
        Shortcuts = new() { Overrides = new(shortcuts.Overrides) };
        this.showReference = showReference ?? (dialog => { dialog.Owner = this; dialog.ShowDialog(); });
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(UiText.Current.Culture.Name);
        AboutVersion.Text = UiText.Format("AboutVersion", typeof(MainWindow).Assembly.GetName().Version?.ToString(3));
    }

    private void UserGuide_Click(object sender, RoutedEventArgs e) => showReference(new UserGuideWindow());
    private void Shortcuts_Click(object sender, RoutedEventArgs e) => showReference(new ShortcutHelpWindow(Shortcuts));

    private void MessageHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageJournal.For(this) is { } history)
            new MessageHistoryWindow(this, history).Show();
    }
}
