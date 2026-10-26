using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo;

internal sealed class SelfSpeakerWindow : Window
{
    private readonly string speakerId;
    private readonly TextBox names;
    public SelfSpeaker? Value { get; private set; }

    public SelfSpeakerWindow(Window? owner, string speakerId, string speakerName, SelfSpeaker? existing)
    {
        this.speakerId = speakerId;
        Owner = owner;
        Title = UiText.Get("MarkAsMe");
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Format("MeSpeakerSelected", speakerName), FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        panel.Children.Add(new TextBlock { Text = UiText.Get("MeTagHint"), TextWrapping = TextWrapping.Wrap });
        if (existing is not null && existing.SpeakerId != speakerId)
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get("ReplaceMeTagHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });
        var label = new Label { Content = UiText.Get("MeNamesLabel"), Padding = new Thickness(0), Margin = new Thickness(0, 14, 0, 6) };
        panel.Children.Add(label);
        names = new TextBox
        {
            Text = existing?.SpeakerId == speakerId ? string.Join(Environment.NewLine, existing.Names) : "",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96,
            MaxLength = 1000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(names, UiText.Get("MeNamesLabel"));
        label.Target = names;
        panel.Children.Add(names);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("MeNamesHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        panel.Children.Add(error);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0)
        };
        var save = new Button { Content = UiText.Get("SaveChanges"), IsDefault = true };
        save.Click += (_, _) =>
        {
            try { Value = ReadValue(); DialogResult = true; }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                Services.MessageJournal.Record(this, error.Text, Title, Services.MessageSeverity.Error);
            }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = UiText.Get("Cancel"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        panel.Children.Add(buttons);
        Content = panel;
    }

    internal SelfSpeaker ReadValue() => SelfSpeaker.Create(speakerId, names.Text);
}
