using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public enum TranscriptComparisonState { NotCompared, Matching, Different, Unclear }

public partial class TranscriptEditWindow : Window
{
    private readonly RecordingSession session;
    private readonly TranscriptCorrectionPlan plan;
    private readonly AppSettings settings;
    private readonly SelectedTranscriptionService recognizer;
    private readonly Action<IReadOnlyList<string>, bool> saveVocabulary;
    private readonly List<CorrectionRow> rows;
    private readonly ProcessingTiming recognitionTiming;
    private readonly DispatcherTimer recognitionClock = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? operation;
    private AudioSourceVersion? audioVersion;
    private string selectedWords = "";
    private bool autoTranscribe;
    private bool closeRequested;
    private SelectedTranscriptionCheckpoint? recognitionCheckpoint;
    private AppSettings? recognitionSettings;
    private RecognitionRowSnapshot[]? recognitionBaseline;
    private AudioSourceVersion? recognitionBaselineAudio;
    private readonly HashSet<int> appliedRecognitionRows = [];
    private bool recognizingLocally;
    private bool discardRecognition;
    public TranscriptDocument? ResultDocument { get; private set; }

    public TranscriptEditWindow(RecordingSession session, TranscriptCorrectionPlan plan, AppSettings settings,
        bool reTranscribe, Action<IReadOnlyList<string>, bool> saveVocabulary, SelectedTranscriptionService? recognizer = null,
        bool online = false, Action<OnlineSpeechSettings>? saveOnlineSettings = null,
        SelectedOnlineTranscriptionService? onlineRecognizer = null, TimeProvider? processingTimeProvider = null)
    {
        this.session = session;
        this.plan = plan;
        this.settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        this.saveVocabulary = saveVocabulary;
        this.recognizer = recognizer ?? new();
        recognitionTiming = new(processingTimeProvider);
        this.saveOnlineSettings = saveOnlineSettings;
        this.onlineRecognizer = onlineRecognizer ?? new();
        autoTranscribe = reTranscribe;
        rows = plan.Segments.Select(segment => new CorrectionRow(
            $"{AudioEditPlan.FormatTime(AudioEditPlan.FrameAt(segment.StartSeconds, plan.SourceFrames))} - " +
            AudioEditPlan.FormatTime(AudioEditPlan.FrameAt(segment.EndSeconds, plan.SourceFrames)),
            plan.SpeakerName(segment.SpeakerId), segment.Text)).ToList();
        InitializeComponent();
        recognitionClock.Tick += (_, _) => UpdateRecognitionTiming();
        Closed += (_, _) =>
        {
            recognitionClock.Stop();
            recognitionTiming.Stop();
        };
        Language = XmlLanguage.GetLanguage(settings.UiLanguage);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        foreach (var row in rows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CorrectionRow.HasCorrection))
                    UpdateComparisonActions();
            };
        CorrectionRows.ItemsSource = rows;
        CorrectionRows.AddHandler(TextBoxBase.SelectionChangedEvent, new RoutedEventHandler(CaptureSelectedWords));
        CorrectionModelBox.ItemsSource = SpeechModels.Available;
        CorrectionModelBox.SelectedValue = settings.SpeechModel;
        CorrectionLanguageBox.ItemsSource = SpeechLanguages.GetOptions(UiText.Current);
        CorrectionLanguageBox.SelectedValue = settings.Language;
        UseVocabularyCheck.IsChecked = settings.SpeechVocabularyEnabled;
        CorrectionVocabularyBox.Text = string.Join(Environment.NewLine, settings.SpeechVocabularyTerms);
        UpdateVocabularyStatus();
        InitializeOnline(online);
        UpdateControls();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (autoTranscribe)
        {
            autoTranscribe = false;
            await RetranscribeAsync();
        }
    }

    private async void Retranscribe_Click(object sender, RoutedEventArgs e) => await RetranscribeAsync();

    private async Task RetranscribeAsync()
    {
        if (operation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        var resuming = recognitionCheckpoint is not null;
        var warning = "";
        try
        {
            CommitCell();
            if (comparingOnline)
                ToggleComparison();
            if (recognitionCheckpoint is null)
            {
                var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
                snapshot.SpeechModel = (string?)CorrectionModelBox.SelectedValue
                    ?? throw new InvalidOperationException(UiText.Get("ChooseSpeechModel"));
                snapshot.Language = (string?)CorrectionLanguageBox.SelectedValue
                    ?? throw new InvalidOperationException(UiText.Get("InvalidSpeechLanguage"));
                snapshot.SpeechVocabularyEnabled = UseVocabularyCheck.IsChecked == true;
                snapshot.SpeechVocabularyTerms = SpeechVocabulary.Parse(CorrectionVocabularyBox.Text);
                snapshot.Validate();
                recognitionSettings = snapshot;
                recognitionCheckpoint = new(plan, snapshot);
                recognitionBaseline = rows.Select(row => new RecognitionRowSnapshot(row.Text, row.Unrecognized, row.HasCorrection)).ToArray();
                recognitionBaselineAudio = audioVersion;
                appliedRecognitionRows.Clear();
            }
            recognizingLocally = true;
            discardRecognition = false;
            operation = cancellation;
            StartRecognitionTiming(resuming);
            recognitionTiming.Report("selected-transcription", recognitionCheckpoint.Fraction);
            UpdateControls();
            CorrectionStatus.Text = UiText.Get("SelectionRecognitionStarting");
            var progress = new Progress<SelectedTranscriptionProgress>(update =>
            {
                if (operation != cancellation)
                    return;
                var fraction = update.Fraction ?? (double)update.Completed / update.Total;
                RecognitionProgress.Value = 100 * fraction;
                recognitionTiming.Report("selected-transcription", fraction);
                UpdateRecognitionTiming();
                if (update.Row is not null)
                    ApplyRecognitionRow(update.Row);
                if (update.PartialText is not null)
                    ShowPartialRecognition(update.PartialText, update.Completed);
                if (update.Warning is not null)
                    warning = update.Warning;
                CorrectionStatus.Text = UiText.Format("SelectionRecognitionProgress", update.Completed, update.Total,
                    update.Model, update.Runtime ?? "") + (warning.Length == 0 ? "" : Environment.NewLine + warning);
            });
            var result = await recognizer.GenerateAsync(session, plan, recognitionSettings!, progress,
                cancellation.Token, recognitionCheckpoint);
            audioVersion = result.AudioVersion;
            CorrectionStatus.Text = UiText.Format("SelectionRecognitionReady", result.Unrecognized.Count) +
                (warning.Length == 0 ? "" : Environment.NewLine + warning);
        }
        catch (OperationCanceledException)
        {
            CorrectionStatus.Text = discardRecognition ? UiText.Get("SelectionRecognitionCancelled") :
                UiText.Format("SelectionRecognitionStopped", recognitionCheckpoint?.CompletedRows.Count ?? 0, rows.Count);
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            StopRecognitionTiming();
            if (discardRecognition)
                ClearRecognitionCheckpoint(restore: true);
            else if (recognitionCheckpoint is not null)
            {
                audioVersion = recognitionCheckpoint.AudioVersion ?? audioVersion;
                foreach (var row in recognitionCheckpoint.CompletedRows)
                    ApplyRecognitionRow(row);
                ShowPartialRecognition(recognitionCheckpoint.PartialText, recognitionCheckpoint.CompletedRows.Count);
                RecognitionProgress.Value = 100 * recognitionCheckpoint.Fraction;
                if (recognitionCheckpoint.IsComplete)
                    ClearRecognitionCheckpoint(restore: false);
            }
            operation = null;
            recognizingLocally = false;
            UpdateControls();
            if (closeRequested)
                Close();
        }
    }

    private void ApplyRecognitionRow(SelectedTranscriptionRow result)
    {
        if (!appliedRecognitionRows.Add(result.Index))
            return;
        var row = rows[result.Index];
        row.SetCorrection(result.Unrecognized ? row.Text : result.Text, result.Unrecognized);
    }

    private void ShowPartialRecognition(string text, int index)
    {
        PartialRecognitionPanel.Visibility = text.Length == 0 || index >= rows.Count ? Visibility.Collapsed : Visibility.Visible;
        PartialRecognitionText.Text = text;
        PartialRecognitionCaption.Text = index < rows.Count ? UiText.Format("SelectionPartialPreview", rows[index].Time) : "";
    }

    private void ClearRecognitionCheckpoint(bool restore)
    {
        if (restore && recognitionBaseline is not null)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var snapshot = recognitionBaseline[index];
                rows[index].SetCorrection(snapshot.Text, snapshot.Unrecognized, snapshot.HasCorrection);
            }
            audioVersion = recognitionBaselineAudio;
        }
        recognitionCheckpoint = null;
        recognitionSettings = null;
        recognitionBaseline = null;
        recognitionBaselineAudio = null;
        appliedRecognitionRows.Clear();
        ShowPartialRecognition("", rows.Count);
    }

    private async void StopContinueRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (recognizingLocally)
        {
            operation?.Cancel();
            CorrectionStatus.Text = UiText.Get("SelectionRecognitionStopping");
            UpdateControls();
        }
        else
            await RetranscribeAsync();
    }

    private sealed record RecognitionRowSnapshot(string Text, bool Unrecognized, bool HasCorrection);

    private void CommitCell()
    {
        if (!CorrectionRows.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !CorrectionRows.CommitEdit(DataGridEditingUnit.Row, true))
            throw new InvalidOperationException(UiText.Get("InvalidCorrectionText"));
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (operation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        try
        {
            CommitCell();
            var texts = rows.Select(row => row.Text).ToArray();
            plan.Apply(texts);
            operation = cancellation;
            UpdateControls();
            ResultDocument = await Task.Run(() => TranscriptCorrectionService.Save(
                session, plan, texts, cancellation.Token, audioVersion), cancellation.Token);
        }
        catch (OperationCanceledException) { CorrectionStatus.Text = UiText.Get("SelectionRecognitionCancelled"); }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            operation = null;
            UpdateControls();
            if (ResultDocument is not null)
                DialogResult = true;
            else if (closeRequested)
                Close();
        }
    }

    private void CaptureSelectedWords(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TextBox { SelectionLength: > 0 } box)
            return;
        selectedWords = box.SelectedText.Trim();
        RememberSelectionButton.IsEnabled = operation is null && recognitionCheckpoint is null && selectedWords.Length > 0;
        RememberSelectionButton.ToolTip = selectedWords;
    }

    private void RememberSelection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (selectedWords.Length == 0)
                throw new InvalidOperationException(UiText.Get("SelectVocabularyWords"));
            CorrectionVocabularyBox.Text = string.Join(Environment.NewLine,
                SpeechVocabulary.Normalize([.. SpeechVocabulary.Parse(CorrectionVocabularyBox.Text), selectedWords]));
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void SaveVocabulary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            saveVocabulary(SpeechVocabulary.Parse(CorrectionVocabularyBox.Text), UseVocabularyCheck.IsChecked == true);
            CorrectionStatus.Text = UiText.Get("SpeechVocabularySaved");
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void Vocabulary_TextChanged(object sender, TextChangedEventArgs e) => UpdateVocabularyStatus();
    private void UpdateVocabularyStatus()
    {
        if (VocabularyStatus is null || CorrectionVocabularyBox is null)
            return;
        try
        {
            var hint = SpeechVocabulary.CreatePrompt(SpeechVocabulary.Parse(CorrectionVocabularyBox.Text));
            VocabularyStatus.Text = UiText.Format("SpeechVocabularyStatus", hint.UsedTerms, hint.TotalTerms) +
                (hint.Text.Length == 0 ? "" : Environment.NewLine + hint.Text);
        }
        catch (Exception ex)
        {
            VocabularyStatus.Text = UiText.Format("ErrorDetails", ex.Message);
            MessageJournal.Record(this, VocabularyStatus.Text, UiText.Get("SpeechVocabularyTitle"), MessageSeverity.Error);
        }
    }

    private void UpdateControls()
    {
        var ready = operation is null;
        var canConfigure = ready && recognitionCheckpoint is null;
        CorrectionRows.IsEnabled = ready;
        CorrectionModelBox.IsEnabled = canConfigure;
        CorrectionLanguageBox.IsEnabled = canConfigure;
        RetranscribeButton.IsEnabled = canConfigure && File.Exists(session.WavePath);
        SaveCorrectionButton.IsEnabled = ready;
        CorrectionVocabularyBox.IsEnabled = canConfigure;
        UseVocabularyCheck.IsEnabled = canConfigure;
        SaveVocabularyButton.IsEnabled = canConfigure;
        RememberSelectionButton.IsEnabled = canConfigure && selectedWords.Length > 0;
        StopContinueRecognitionButton.Visibility = recognizingLocally || recognitionCheckpoint is not null ? Visibility.Visible : Visibility.Collapsed;
        StopContinueRecognitionButton.Content = UiText.Get(recognizingLocally ? "Stop" : "ContinueRecognition");
        StopContinueRecognitionButton.IsEnabled = recognizingLocally ? operation?.IsCancellationRequested == false : ready;
        CancelRecognitionButton.Visibility = ready && recognitionCheckpoint is null ? Visibility.Collapsed : Visibility.Visible;
        CancelRecognitionButton.Content = UiText.Get(ready ? "DiscardRecognitionPreview" : "Cancel");
        RecognitionProgress.Visibility = ready && recognitionCheckpoint is null ? Visibility.Collapsed : Visibility.Visible;
        RecognitionActivity.Visibility = !ready || recognitionTiming.HasStarted || recognitionCheckpoint is not null
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateOnlineControls(canConfigure);
    }

    private void StartRecognitionTiming(bool resume = false)
    {
        recognitionTiming.Start(resume);
        if (!resume)
            RecognitionProgress.Value = 0;
        recognitionClock.Start();
        UpdateRecognitionTiming();
    }

    private void StopRecognitionTiming()
    {
        recognitionTiming.Stop();
        recognitionClock.Stop();
        UpdateRecognitionTiming();
    }

    private void UpdateRecognitionTiming()
    {
        var display = recognitionTiming.Display(UiText.Current);
        RecognitionTimingText.Text = display.Text;
        RecognitionTimingText.ToolTip = display.ToolTip;
        RecognitionTimingText.Visibility = recognitionTiming.HasStarted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (operation is not null)
        {
            if (recognizingLocally)
                discardRecognition = true;
            operation.Cancel();
        }
        else
        {
            ClearRecognitionCheckpoint(restore: true);
            CorrectionStatus.Text = UiText.Get("SelectionRecognitionCancelled");
            UpdateControls();
        }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (operation is null)
            return;
        closeRequested = true;
        operation.Cancel();
        e.Cancel = true;
    }

    public sealed class CorrectionRow(string time, string speaker, string originalText) : INotifyPropertyChanged
    {
        private string text = originalText;
        private string? onlineText;
        private bool comparingOnline;

        public string Time { get; } = time;
        public string Speaker { get; } = speaker;
        public string OriginalText { get; } = originalText;
        public string LocalText => OriginalText;
        public bool HasCorrection { get; private set; }
        public bool Unrecognized { get; private set; }
        public string Text
        {
            get => text;
            set
            {
                if (text != value)
                    SetCorrection(value);
            }
        }
        public string? OnlineText
        {
            get => onlineText;
            set
            {
                if (onlineText == value)
                    return;
                onlineText = value;
                Changed(nameof(OnlineText));
                Changed(nameof(OnlineDifferent));
                Changed(nameof(ComparisonState));
            }
        }
        public bool ComparingOnline
        {
            get => comparingOnline;
            set
            {
                if (comparingOnline == value)
                    return;
                comparingOnline = value;
                Changed(nameof(ComparingOnline));
                Changed(nameof(ComparisonState));
            }
        }
        public bool OnlineDifferent => OnlineText is not null && Compare(OnlineText) != TranscriptComparisonState.Matching;
        public TranscriptComparisonState ComparisonState => ComparingOnline ? Compare(OnlineText)
            : HasCorrection ? Compare(Text, Unrecognized) : TranscriptComparisonState.NotCompared;

        internal void SetCorrection(string value, bool unrecognized = false, bool hasCorrection = true)
        {
            var textChanged = text != value;
            var missingChanged = Unrecognized != unrecognized;
            var comparedChanged = HasCorrection != hasCorrection;
            if (!textChanged && !missingChanged && !comparedChanged)
                return;
            text = value;
            Unrecognized = unrecognized;
            HasCorrection = hasCorrection;
            if (textChanged)
                Changed(nameof(Text));
            if (missingChanged)
                Changed(nameof(Unrecognized));
            if (comparedChanged)
                Changed(nameof(HasCorrection));
            Changed(nameof(ComparisonState));
        }

        private TranscriptComparisonState Compare(string? result, bool unrecognized = false) =>
            result is null ? TranscriptComparisonState.NotCompared
            : unrecognized || string.IsNullOrWhiteSpace(LocalText) || string.IsNullOrWhiteSpace(result)
                ? TranscriptComparisonState.Unclear
                : LocalText == result ? TranscriptComparisonState.Matching : TranscriptComparisonState.Different;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }
}
