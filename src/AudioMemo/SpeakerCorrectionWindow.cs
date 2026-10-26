using System.Windows;
using System.Windows.Controls;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo;

public sealed class SpeakerCorrectionWindow : Window
{
    public TranscriptDocument? Result { get; private set; }
    public string? TargetSpeakerId { get; private set; }
    public bool MergeAll { get; private set; }

    public SpeakerCorrectionWindow(Window? owner, TranscriptDocument document, TranscriptCorrectionPlan selection)
    {
        Owner = owner;
        Title = UiText.Get("CorrectSpeaker");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = UiText.Get("CorrectSpeakerHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var choices = document.Segments.Select(segment => segment.SpeakerId).OfType<string>().Distinct()
            .Select(id => new LanguageOption(id, document.SpeakerName(id) == id ? id : $"{document.SpeakerName(id)} ({id})")).ToArray();
        var target = new ComboBox { Name = "TargetSpeaker", ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Code", SelectedIndex = 0 };
        System.Windows.Automation.AutomationProperties.SetName(target, UiText.Get("AssignToSpeaker"));
        panel.Children.Add(new TextBlock { Text = UiText.Get("AssignToSpeaker"), Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(target);
        var sourceId = selection.Segments[0].SpeakerId;
        var sourceName = sourceId is null ? UiText.Get("UnlabeledSpeakers") : document.SpeakerName(sourceId);
        var all = new CheckBox
        {
            Name = "MergeWholeSpeaker",
            Content = new TextBlock { Text = UiText.Format("MergeWholeSpeaker", sourceName, document.Segments.Count(segment => segment.SpeakerId == sourceId)), TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(0, 12, 0, 8)
        };
        panel.Children.Add(all);
        panel.Children.Add(new TextBlock { Text = UiText.Format("ReassignSelectedSegments", selection.Indices.Count), TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = UiText.Get("SaveChanges"), IsDefault = true };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        save.Click += (_, _) =>
        {
            try
            {
                TargetSpeakerId = target.SelectedValue as string ?? throw new InvalidOperationException(UiText.Get("ChooseLabeledSpeaker"));
                MergeAll = all.IsChecked == true;
                Result = TranscriptSpeakerCorrection.Apply(document, selection, TargetSpeakerId, MergeAll);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                status.Text = UiText.Format("ErrorDetails", ex.Message);
                Services.MessageJournal.Record(this, status.Text, Title, Services.MessageSeverity.Error);
            }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = UiText.Get("Cancel"), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        panel.Children.Add(buttons);
        Content = panel;
    }
}
