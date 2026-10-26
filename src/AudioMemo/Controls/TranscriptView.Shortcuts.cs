using System.Windows;
using System.Windows.Input;
using AudioMemo.Core.Models;

namespace AudioMemo.Controls;

public partial class TranscriptView
{
    public ShortcutSettings Shortcuts { get; private set; } = new();
    public event Action<KeyEventArgs>? GlobalShortcutRequested;

    public void FocusTranscript() => RowsGrid.Focus();

    public FrameworkElement? ShortcutTarget(string id) => id switch
    {
        "copy" => CopyVisibleAction,
        "copyAnnotated" => CopyAnnotatedAction,
        "export" => ExportAudioMenu,
        "latest" => FollowAction,
        "first" => TranscriptTopButton,
        "pageUp" => TranscriptPageUpButton,
        "pageDown" => TranscriptPageDownButton,
        "speakerFilter" => SpeakerFilterAction,
        "play" => PlayRangeAction,
        "replayPause" => ReplayTransport.Visibility == Visibility.Visible ? ReplayPauseButton : null,
        "replayStop" => ReplayTransport.Visibility == Visibility.Visible ? ReplayStopButton : null,
        "silence" => MuteRowsAction,
        "edit" => CorrectRowsAction,
        "reviewUnclear" => ReviewUnclearMenu,
        "retranscribe" => RetranscribeRowsMenu,
        "online" => OnlineRowsAction,
        "search" => SearchAction,
        "searchNext" => SearchNextButton,
        "searchPrevious" => SearchPreviousButton,
        "searchClear" => ClearSearchButton,
        "speakerColor" => SpeakerColorMenu,
        "speakerCorrection" => CorrectSpeakerMenu,
        _ => null
    };

    public void ConfigureShortcuts(ShortcutSettings settings)
    {
        settings.Validate();
        Shortcuts = settings;
        foreach (var command in ShortcutSettings.Commands)
            if (ShortcutTarget(command.Id) is { } target)
                ShortcutActions.SetHint(target, settings, command.Id);
        CorrectRowsMenu.InputGestureText = ShortcutInput.Display(settings.Gesture("edit"));
        OnlineRowsMenu.InputGestureText = ShortcutInput.Display(settings.Gesture("online"));
        ShortcutActions.SetHint(ReplayPauseButton, settings, "replayPause");
        ShortcutActions.SetHint(ReplayStopButton, settings, "replayStop");
        ShortcutActions.SetHint(TranscriptBottomButton, settings, "latest");
    }
}
