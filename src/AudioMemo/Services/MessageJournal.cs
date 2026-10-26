using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AudioMemo.Core.Localization;

namespace AudioMemo.Services;

public static class MessageJournal
{
    public static readonly DependencyProperty ReasonProperty = DependencyProperty.RegisterAttached(
        "Reason", typeof(string), typeof(MessageJournal), new PropertyMetadata(null, Observe));
    public static string? GetReason(DependencyObject element) => (string?)element.GetValue(ReasonProperty);
    public static void SetReason(DependencyObject element, string value) => element.SetValue(ReasonProperty, value);

    internal static MessageHistoryStore? For(Window? window)
    {
        for (var owner = window; owner is not null; owner = owner.Owner)
        {
            if (owner is MainWindow main)
                return main.MessageHistory;
            if (owner is SetupWizardWindow setup)
                return setup.MessageHistory;
            if (owner is MessageHistoryWindow messages)
                return messages.MessageHistory;
        }
        return (Application.Current as App)?.Messages;
    }

    internal static void Record(Window? owner, string text, string reason, MessageSeverity severity = MessageSeverity.Information)
    {
        if (string.IsNullOrWhiteSpace(text) || For(owner) is not { } history)
            return;
        history.Add(text, reason, severity);
        if (history.TakeWriteWarning() is not { } warning)
            return;
        for (var window = owner; window is not null; window = window.Owner)
        {
            if (window is MainWindow main)
            {
                main.ShowHistoryWriteWarning(warning);
                return;
            }
        }
        MessageBox.Show(owner, warning, UiText.Get("MessageHistory"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Observe(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock text || e.OldValue is not null)
            return;
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        string? previous = null;
        void Changed(object? sender, EventArgs args)
        {
            if (previous == text.Text)
                return;
            previous = text.Text;
            if (GetReason(text) is not { Length: > 0 } reason)
                return;
            var error = text.Text.StartsWith(UiText.Format("ErrorDetails", ""), StringComparison.CurrentCulture);
            Record(Window.GetWindow(text), text.Text, reason, error ? MessageSeverity.Error : MessageSeverity.Information);
        }
        text.Loaded += (_, _) =>
        {
            descriptor.AddValueChanged(text, Changed);
            Changed(null, EventArgs.Empty);
        };
        text.Unloaded += (_, _) => descriptor.RemoveValueChanged(text, Changed);
    }
}
