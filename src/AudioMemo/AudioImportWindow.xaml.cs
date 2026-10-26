using System.ComponentModel;
using System.Windows;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class AudioImportWindow : Window
{
    private readonly string path;
    private readonly string library;
    private readonly CancellationTokenSource cancellation = new();
    private VideoMediaInfo? media;
    private bool started;
    private bool running;
    private bool closeRequested;
    public RecordingSession? Result { get; private set; }

    public AudioImportWindow(string path, string library)
    {
        this.path = path;
        this.library = library;
        InitializeComponent();
        SetResourceReference(StyleProperty, typeof(Window));
        ImportStatus.Text = System.IO.Path.GetFileName(path);
        Closed += (_, _) => cancellation.Dispose();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (started)
            return;
        started = true;
        await RunImportAsync();
    }

    private async Task RunImportAsync()
    {
        running = true;
        UpdateTrackActions();
        try
        {
            if (VideoAudioService.IsVideo(path) && media is null)
            {
                ImportProgress.IsIndeterminate = true;
                ImportStatus.Text = UiText.Format("VideoInspecting", System.IO.Path.GetFileName(path));
                media = await new VideoAudioService().ProbeAsync(path, cancellation.Token);
                if (media.AudioTracks.Count > 1)
                {
                    ShowAudioTracks(media);
                    return;
                }
            }
            VideoImportSelection? selection = null;
            if (media is not null)
            {
                var index = media.AudioTracks.Count == 1 ? media.AudioTracks[0].StreamIndex :
                    (AudioTrackBox.SelectedItem as AudioTrackChoice)?.StreamIndex
                    ?? throw new InvalidOperationException(UiText.Get("VideoChooseTrack"));
                selection = new(media, index);
                var track = VideoAudioService.ValidateSelection(path, selection);
                ImportProgress.IsIndeterminate = media.ExpectedSeconds(track) is null;
            }
            AudioTrackPanel.Visibility = Visibility.Collapsed;
            ImportSelectedButton.Visibility = Visibility.Collapsed;
            ImportStatus.Text = System.IO.Path.GetFileName(path);
            Result = await new AudioImportService().ImportAsync(path, library,
                new Progress<double>(value => ImportProgress.Value = value), cancellation.Token, selection);
        }
        catch (OperationCanceledException) { closeRequested = true; }
        catch (Exception ex)
        {
            ImportStatus.Text = UiText.Format("ErrorDetails", ex.Message);
            CancelImportButton.Content = UiText.Get("Close");
            AudioTrackPanel.Visibility = Visibility.Collapsed;
            ImportSelectedButton.Visibility = Visibility.Collapsed;
            ImportProgress.IsIndeterminate = false;
        }
        finally
        {
            running = false;
            UpdateTrackActions();
            if (Result is not null)
                DialogResult = true;
            else if (closeRequested)
                Close();
        }
    }

    private void ShowAudioTracks(VideoMediaInfo information)
    {
        media = information;
        var choices = information.AudioTracks.Select((track, index) =>
        {
            var language = string.IsNullOrWhiteSpace(track.Language) || track.Language == "und"
                ? UiText.Get("VideoUnknownLanguage") : track.Language;
            var details = string.IsNullOrWhiteSpace(track.Title) ? language : $"{track.Title} ({language})";
            var channels = track.Channels == 0 ? "?" : track.Channels.ToString(System.Globalization.CultureInfo.CurrentCulture);
            return new AudioTrackChoice(track.StreamIndex, UiText.Format("VideoTrackLabel", index + 1,
                details, track.Codec, channels, track.IsDefault ? UiText.Get("VideoDefaultTrack") : ""));
        }).ToArray();
        AudioTrackBox.ItemsSource = choices;
        AudioTrackBox.SelectedValue = information.AudioTracks.FirstOrDefault(track => track.IsDefault)?.StreamIndex
            ?? information.AudioTracks[0].StreamIndex;
        AudioTrackPanel.Visibility = Visibility.Visible;
        ImportSelectedButton.Visibility = Visibility.Visible;
        ImportProgress.IsIndeterminate = false;
        ImportProgress.Value = 0;
        ImportStatus.Text = UiText.Get("VideoChooseTrack");
        Height = Math.Max(Height, 380);
        UpdateTrackActions();
    }

    private void UpdateTrackActions()
    {
        if (AudioTrackBox is null || ImportSelectedButton is null)
            return;
        AudioTrackBox.IsEnabled = !running;
        ImportSelectedButton.IsEnabled = !running && AudioTrackBox.SelectedItem is AudioTrackChoice;
    }

    private void AudioTrack_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateTrackActions();
    private async void ImportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!running && AudioTrackBox.SelectedItem is AudioTrackChoice)
            await RunImportAsync();
    }

    private sealed record AudioTrackChoice(int StreamIndex, string Label);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!running)
            return;
        closeRequested = true;
        cancellation.Cancel();
        e.Cancel = true;
    }
}
