using System.Windows;
using System.Windows.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo;

public sealed class MessageDialog : Window
{
    public MessageBoxResult Result { get; private set; }

    public MessageDialog(Window? owner, string text, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        Owner = owner;
        Title = caption;
        ShowInTaskbar = owner is null;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Width = Math.Max(280, Math.Min(540, SystemParameters.WorkArea.Width - 48));
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(180, SystemParameters.WorkArea.Height - 48);
        SetResourceReference(StyleProperty, typeof(Window));
        var choices = buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => throw new ArgumentOutOfRangeException(nameof(buttons))
        };
        Result = choices.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel :
            choices.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        if (defaultResult == MessageBoxResult.None)
            defaultResult = choices[0];
        if (!choices.Contains(defaultResult))
            throw new ArgumentOutOfRangeException(nameof(defaultResult));

        var root = new StackPanel { Margin = new Thickness(20) };
        var body = new DockPanel();
        if (image != MessageBoxImage.None)
        {
            var symbol = new TextBlock
            {
                Text = image == MessageBoxImage.Question ? "?" : image == MessageBoxImage.Information ? "i" : "!",
                FontSize = 24, FontWeight = FontWeights.Bold, Width = 28, Margin = new Thickness(0, 0, 12, 0)
            };
            symbol.SetResourceReference(TextBlock.ForegroundProperty, image switch
            {
                MessageBoxImage.Error => "SystemFillColorCriticalBrush",
                MessageBoxImage.Warning => "SystemFillColorCautionBrush",
                _ => "AccentTextFillColorPrimaryBrush"
            });
            DockPanel.SetDock(symbol, Dock.Left);
            body.Children.Add(symbol);
        }
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = Math.Max(72, MaxHeight - 140),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
        });
        root.Children.Add(body);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = UiText.Get(choice switch
                {
                    MessageBoxResult.OK => "DialogOK", MessageBoxResult.Yes => "DialogYes",
                    MessageBoxResult.No => "DialogNo", _ => "Cancel"
                }),
                Tag = choice, IsDefault = choice == defaultResult, IsCancel = choice == Result,
                MinWidth = 72, MinHeight = 28, Padding = new Thickness(8, 3, 8, 3),
                FontSize = 12, Margin = new Thickness(actions.Children.Count == 0 ? 0 : 8, 0, 0, 0)
            };
            button.Click += (_, _) =>
            {
                Result = choice;
                DialogResult = choice is MessageBoxResult.Yes or MessageBoxResult.OK;
            };
            actions.Children.Add(button);
        }
        root.Children.Add(actions);
        Content = root;
    }

    public static MessageBoxResult ShowMessage(Window? owner, string text, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        MessageJournal.Record(owner, text, caption, image switch
        {
            MessageBoxImage.Error => MessageSeverity.Error,
            MessageBoxImage.Warning => MessageSeverity.Warning,
            MessageBoxImage.Question => MessageSeverity.Question,
            _ => MessageSeverity.Information
        });
        var dialog = new MessageDialog(owner, text, caption, buttons, image, defaultResult);
        dialog.ShowDialog();
        if (buttons != MessageBoxButton.OK)
            MessageJournal.Record(owner, UiText.Format("MessageDialogResponse", UiText.Get(dialog.Result switch
            {
                MessageBoxResult.Yes => "DialogYes", MessageBoxResult.No => "DialogNo",
                MessageBoxResult.OK => "DialogOK", _ => "Cancel"
            })), caption);
        return dialog.Result;
    }

    public static MessageBoxResult ShowMessage(string text, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        ShowMessage(Application.Current?.MainWindow, text, caption, buttons, image, defaultResult);
}
