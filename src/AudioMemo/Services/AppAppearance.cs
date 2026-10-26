using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using Microsoft.Win32;

namespace AudioMemo.Services;

public static class AppAppearance
{
    private static readonly ConditionalWeakTable<Application, AppearanceState> states = new();

    public static void Apply(Application application, string theme) => Apply(application, new AppSettings { Theme = theme });

    public static void Apply(Application application, AppSettings settings)
    {
        if (!AppThemes.IsSupported(settings.Theme))
            throw new InvalidDataException(UiText.Get("InvalidTheme"));
        settings.Appearance.Validate();
        application.Dispatcher.VerifyAccess();
        WindowSizing.Initialize();
#pragma warning disable WPF0001
        application.ThemeMode = settings.Theme switch
        {
            AppThemes.Dark => ThemeMode.Dark,
            AppThemes.System => ThemeMode.System,
            _ => ThemeMode.Light
        };
#pragma warning restore WPF0001
        var state = states.GetValue(application, app => new(app));
        state.Settings = settings.Appearance;
        state.Refresh();
    }

    public static SolidColorBrush Brush(RgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    public static RgbColor Rgb(Color color) => new(color.R, color.G, color.B);

    private sealed class AppearanceState
    {
        private readonly Application application;
        private bool queued;
        public AppearanceSettings Settings { get; set; } = new();

        public AppearanceState(Application application)
        {
            this.application = application;
            SystemParameters.StaticPropertyChanged += ParametersChanged;
            SystemEvents.UserPreferenceChanged += PreferencesChanged;
            application.Exit += (_, _) =>
            {
                SystemParameters.StaticPropertyChanged -= ParametersChanged;
                SystemEvents.UserPreferenceChanged -= PreferencesChanged;
            };
        }

        private void ParametersChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
        private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e) => QueueRefresh();

        private void QueueRefresh()
        {
            if (application.Dispatcher.HasShutdownStarted)
                return;
            application.Dispatcher.BeginInvoke(() =>
            {
                if (queued)
                    return;
                queued = true;
                // Native Fluent resources settle before applying the user's custom colors.
                application.Dispatcher.BeginInvoke(() =>
                {
                    queued = false;
                    Refresh();
                }, DispatcherPriority.ContextIdle);
            }, DispatcherPriority.Background);
        }

        public void Refresh()
        {
            var background = (SolidColorBrush)application.FindResource("SolidBackgroundFillColorBaseBrush");
            var dark = Rgb(background.Color).Luminance < 0.4;
            var palette = dark ? Settings.Dark : Settings.Light;
            var highContrast = SystemParameters.HighContrast;
            var selection = highContrast ? Rgb(SystemColors.HighlightColor) : RgbColor.Parse(palette.Selection);
            var selectionText = highContrast ? Rgb(SystemColors.HighlightTextColor) : selection.ReadableText;
            var resources = application.Resources;
            // Fluent's disabled DataGrid animation otherwise falls back to white.
            resources["ControlLightColor"] = ((SolidColorBrush)application.FindResource("SolidBackgroundFillColorQuarternaryBrush")).Color;
            resources["ButtonStyleId"] = Settings.ButtonStyle;
            resources["TranscriptSelectionBackground"] = Brush(selection);
            resources["TranscriptSelectionForeground"] = Brush(selectionText);
            resources["SearchHighlight"] = highContrast ? SystemColors.HighlightBrush : Brush(RgbColor.Parse(palette.Match));
            resources["SearchActiveHighlight"] = highContrast ? SystemColors.HighlightBrush : Brush(RgbColor.Parse(palette.ActiveMatch));
            resources["TranscriptTextSelectionForeground"] = highContrast ? SystemColors.HighlightTextBrush : Brush(RgbColor.Parse(palette.ActiveMatch).ReadableText);
            var comparison = dark ? Settings.DarkComparison : Settings.LightComparison;
            foreach (var (name, value) in new[] { ("Different", comparison.Different),
                ("Matching", comparison.Matching), ("Unclear", comparison.Unclear) })
            {
                var color = RgbColor.Parse(value);
                resources[$"Comparison{name}Background"] = highContrast ? SystemColors.WindowBrush : Brush(color);
                resources[$"Comparison{name}Foreground"] = highContrast ? SystemColors.WindowTextBrush : Brush(color.ReadableText);
            }
            resources["AppDivider"] = highContrast ? SystemColors.WindowTextBrush : Brush(RgbColor.Parse(dark ? "#65758A" : "#7B8798"));
            resources["RowDivider"] = highContrast ? SystemColors.WindowTextBrush : Brush(RgbColor.Parse(dark ? "#59697B" : "#B2BECA"));
        }
    }
}
