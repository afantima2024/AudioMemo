using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyPersonalNotes(Application application, string language)
    {
        VerifyEditablePersonalNotes(application, language);
        VerifyPersonalNotesCloseAndResume(language, true);
        VerifyPersonalNotesCloseAndResume(language, false);
    }

    private static void VerifyEditablePersonalNotes(Application application, string language)
    {
        using var folder = new TestFolder();
        var session = PersonalSummaryTests.Source(folder);
        var original = AppStorage.LoadTranscript(session);
        var originalNotes = File.ReadAllText(session.NotesPath);
        var mainPoints = File.ReadAllBytes(session.SummaryPath);
        var audio = File.ReadAllBytes(session.WavePath);
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.UiLanguage = language;
        var capture = new PersonalSummaryTests.Capture { HoldOnCall = 1 };
        var pipeline = SummaryOnlyPipeline(capture);
        var confirmations = new List<string>();
        var approveAppend = false;
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings,
            new RecordingEngine(new RecordingEngineTests.FakeFactory().Create), pipeline: pipeline,
            confirmAppendAboutMe: message => { confirmations.Add(message); return approveAppend; });
        var root = MountDialog(window, new Size(1440, 900));
        SelectProcessingSession(window, session);
        typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        var view = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
        var notes = Assert.IsType<TextBox>(window.FindName("NotesBox"));
        var rows = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
        var updateNotes = Assert.IsType<Button>(window.FindName("UpdateMyNotesButton"));
        var notesTitle = Assert.IsType<TextBlock>(window.FindName("MyNotesTitle"));
        try
        {
            Assert.Null(window.FindName("ExportButton"));
            Assert.Equal(UiText.Get("UpdateMyNotes"), updateNotes.Content);
            Assert.Equal(UiText.Get("UpdateMyNotesHint"), updateNotes.ToolTip);
            Assert.True(ToolTipService.GetShowOnDisabled(updateNotes));
            Assert.Equal(60000, ToolTipService.GetShowDuration(notesTitle));
            Assert.Contains(UiText.Get("PersonalNotesModelUnrecorded"), Assert.IsType<string>(notesTitle.ToolTip));
            foreach (var names in new[]
            {
                ("MainPointsHeader", "MainPointsActions", "CloseMainPointsButton", "SummarizeButton", "MainPointsTitle"),
                ("MyNotesHeader", "MyNotesActions", "CloseMyNotesButton", "UpdateMyNotesButton", "MyNotesTitle")
            })
            {
                var header = Assert.IsType<DockPanel>(window.FindName(names.Item1));
                var actions = Assert.IsType<WrapPanel>(window.FindName(names.Item2));
                var close = Assert.IsType<Button>(window.FindName(names.Item3));
                var update = Assert.IsType<Button>(window.FindName(names.Item4));
                var title = Assert.IsType<TextBlock>(window.FindName(names.Item5));
                Assert.Equal(Dock.Right, DockPanel.GetDock(actions));
                Assert.Equal(Dock.Right, DockPanel.GetDock(close));
                Assert.Same(update, Assert.Single(actions.Children.Cast<UIElement>()));
                Pump(root);
                Assert.InRange(close.TranslatePoint(new Point(), header).X -
                    update.TranslatePoint(new Point(update.ActualWidth, 0), header).X, 5, 7);
                Assert.True(actions.TranslatePoint(new Point(), header).X > 40);
                var headerParent = Assert.IsAssignableFrom<Panel>(header.Parent);
                var headerIndex = headerParent.Children.IndexOf(header);
                headerParent.Children.Remove(header);
                var headerHost = new Grid();
                headerHost.Children.Add(header);
                try
                {
                    foreach (var width in new[] { 180d, 220d, 400d })
                    {
                        headerHost.Width = width;
                        headerHost.Measure(new Size(width, 500));
                        headerHost.Arrange(new Rect(0, 0, width, headerHost.DesiredSize.Height));
                        headerHost.UpdateLayout();
                        var closeRight = close.TranslatePoint(new Point(close.ActualWidth, 0), header).X;
                        Assert.True(Math.Abs(closeRight - width) <= 1,
                            $"Header {names.Item1} width {header.ActualWidth}, desired {header.DesiredSize}, actions {actions.ActualWidth}, close {closeRight}; expected {width}.");
                        Assert.True(update.TranslatePoint(new Point(), header).X >= 0);
                        Assert.True(update.TranslatePoint(new Point(update.ActualWidth, 0), header).X <= width);
                        Assert.True(title.ActualWidth > 0, $"The {names.Item5} tooltip must remain reachable at {width} DIPs.");
                        Assert.True(title.TranslatePoint(new Point(title.ActualWidth, 0), header).X <=
                            update.TranslatePoint(new Point(), header).X + 1);
                        Assert.InRange(close.TranslatePoint(new Point(), header).X -
                            actions.TranslatePoint(new Point(actions.ActualWidth, 0), header).X, 5, 7);
                    }
                }
                finally { headerHost.Children.Remove(header); headerParent.Children.Insert(headerIndex, header); Pump(root); }
            }

            var dialog = new SelfSpeakerWindow(null, "spkr1", "Alex", original.Me);
            try
            {
                var dialogRoot = MountDialog(dialog, new Size(540, 470));
                var names = Assert.IsType<TextBox>(typeof(SelfSpeakerWindow)
                    .GetField("names", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog));
                Assert.Equal(UiText.Get("MeNamesLabel"), AutomationProperties.GetName(names));
                Assert.Equal(original.Me!.Names, dialog.ReadValue().Names);
                names.Text = "Alex\nAl\nAlex";
                Assert.Equal(["Alex", "Al"], dialog.ReadValue().Names);
                foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
                {
                    AppAppearance.Apply(application, theme);
                    Pump(dialogRoot);
                    var save = Descendants<Button>(dialogRoot).Single(button => Equals(button.Content, UiText.Get("SaveChanges")));
                    Assert.True(save.ActualWidth > 0);
                    Assert.True(save.TranslatePoint(new Point(save.ActualWidth, save.ActualHeight), dialogRoot).Y <= dialogRoot.ActualHeight + 1);
                }
            }
            finally { dialog.Close(); AppAppearance.Apply(application, AppThemes.Light); }

            Call(window, "SaveMeTag", SelfSpeaker.Create("spkr2", "Second person"));
            Assert.Equal("spkr2", AppStorage.LoadTranscript(session).Me!.SpeakerId);
            Call(window, "SaveMeTag", original.Me);
            Assert.Empty(capture.Requests);
            Assert.Equal(originalNotes, File.ReadAllText(session.NotesPath));
            view.SetSpeakerFilter(["spkr2"]);
            rows.SelectedIndex = 0;
            var summarize = Assert.IsType<MenuItem>(view.FindName("SummarizeAboutMeMenu"));
            Assert.True(summarize.IsEnabled);
            Assert.True(updateNotes.IsEnabled);
            Click(updateNotes);
            CompleteUiTask(root, capture.Held.Task);
            Assert.True(notes.IsEnabled);
            Assert.False(updateNotes.IsEnabled);
            Assert.False(summarize.IsEnabled);
            Assert.Contains(UiText.Get("PersonalNotesModelUnrecorded"), Assert.IsType<string>(notesTitle.ToolTip));
            Assert.False(Assert.IsType<MenuItem>(view.FindName("MarkMeMenu")).IsEnabled);
            notes.Text = "PRIVATE TYPED WHILE AI WAS RUNNING\n" + originalNotes;
            var privatePrefix = notes.Text;
            notes.CaretIndex = privatePrefix.Length;
            capture.Release.SetResult();
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.Empty(confirmations);
            Assert.Contains("My first contribution", Assert.Single(capture.Requests).Data.GetProperty("transcript").GetString());
            Assert.Contains("Alex agreed to review", capture.Requests[0].Data.GetProperty("transcript").GetString());
            Assert.StartsWith(privatePrefix, notes.Text);
            Assert.Equal(privatePrefix.Length, notes.CaretIndex);
            var first = PersonalSummaryStore.Load(session)!;
            Assert.Equal(first.GenerationId, first.AppliedGenerationId);
            Assert.Equal(first.Provider, first.AppliedProvider);
            Assert.Equal(first.Model, first.AppliedModel);
            Assert.Contains(GeneratedWith(first.Model), Assert.IsType<string>(notesTitle.ToolTip));
            Assert.True(PersonalNotesSection.ContainsOnce(notes.Text, first.NotesSection));
            Assert.Contains(UiText.Format("AboutMeHeading", "Alex"), notes.Text);
            Assert.Contains(settings.OllamaModel, notes.Text);
            Assert.Equal(mainPoints, File.ReadAllBytes(session.SummaryPath));
            Assert.Equal(audio, File.ReadAllBytes(session.WavePath));

            notes.AppendText("\n\nPRIVATE NEW SUFFIX");
            settings.OllamaModel = "second-personal-summary-model";
            Invoke(window, "UpdateModelStatus");
            Assert.Contains(GeneratedWith(first.Model), Assert.IsType<string>(notesTitle.ToolTip));
            Assert.DoesNotContain(GeneratedWith(settings.OllamaModel), Assert.IsType<string>(notesTitle.ToolTip));
            Assert.Contains(settings.OllamaModel, Assert.IsType<string>(notesTitle.ToolTip));
            Summarize();
            Assert.Equal(2, capture.Requests.Count);
            Assert.Empty(confirmations);
            Assert.StartsWith(privatePrefix, notes.Text);
            Assert.EndsWith("PRIVATE NEW SUFFIX", notes.Text);
            Assert.DoesNotContain("Synthetic contribution 1", notes.Text);
            Assert.Contains("Synthetic contribution 2", notes.Text);
            Assert.Contains(GeneratedWith(settings.OllamaModel), Assert.IsType<string>(notesTitle.ToolTip));
            var secondModel = settings.OllamaModel;
            notes.Text = notes.Text.Replace("Synthetic contribution 2", "MY MANUAL PRIVATE EDIT");
            Invoke(window, "SaveNotes");
            var editedNotes = File.ReadAllText(session.NotesPath);
            settings.OllamaModel = "third-personal-summary-model";
            Summarize();
            Assert.Single(confirmations);
            Assert.Equal(3, capture.Requests.Count);
            Assert.Equal(editedNotes, notes.Text);
            Assert.Equal(editedNotes, File.ReadAllText(session.NotesPath));
            Assert.NotEqual(PersonalSummaryStore.Load(session)!.GenerationId, PersonalSummaryStore.Load(session)!.AppliedGenerationId);
            Assert.Contains(GeneratedWith(secondModel), Assert.IsType<string>(notesTitle.ToolTip));
            Assert.DoesNotContain(GeneratedWith(settings.OllamaModel), Assert.IsType<string>(notesTitle.ToolTip));
            approveAppend = true;
            Summarize();
            Assert.Equal(2, confirmations.Count);
            Assert.Equal(3, capture.Requests.Count);
            Assert.StartsWith(editedNotes, notes.Text);
            Assert.Contains("MY MANUAL PRIVATE EDIT", notes.Text);
            Assert.Contains("Synthetic contribution 3", notes.Text);
            Assert.Contains(GeneratedWith(settings.OllamaModel), Assert.IsType<string>(notesTitle.ToolTip));

            var pending = pipeline.SummarizeAboutMeAsync(session, settings, new Progress<AiUpdate>(), CancellationToken.None,
                Guid.NewGuid().ToString("N"), AppStorage.LoadTranscript(session).Me!.TagId);
            CompleteUiTask(root, pending);
            var result = pending.Result;
            using (var locked = File.Open(PersonalSummaryStore.PathFor(session), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failure = Assert.Throws<TargetInvocationException>(() =>
                    Call(window, "ApplyPersonalNotes", session, result, CancellationToken.None));
                Assert.IsAssignableFrom<IOException>(failure.InnerException);
                Assert.Equal(File.ReadAllText(session.NotesPath), notes.Text);
                Assert.Contains("Synthetic contribution 4", notes.Text);
                Assert.NotEqual(result.GenerationId, PersonalSummaryStore.Load(session)!.AppliedGenerationId);
            }
            var savedWithFailedMetadata = notes.Text;
            Summarize();
            Assert.Equal(4, capture.Requests.Count);
            Assert.Equal(savedWithFailedMetadata, notes.Text);
            Assert.True(PersonalNotesSection.ContainsOnce(notes.Text, result.NotesSection));
            Assert.Equal(result.GenerationId, PersonalSummaryStore.Load(session)!.AppliedGenerationId);
            Assert.All(capture.Requests, request => Assert.DoesNotContain("PRIVATE", request.Prompt));

            var beforeRemoval = File.ReadAllBytes(session.NotesPath);
            Assert.IsType<MenuItem>(view.FindName("RemoveMeMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Null(AppStorage.LoadTranscript(session).Me);
            Assert.False(summarize.IsEnabled);
            Assert.True(updateNotes.IsEnabled);
            Click(updateNotes);
            Assert.Contains(UiText.Get("ChooseMeFirst"), Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            Assert.Equal(beforeRemoval, File.ReadAllBytes(session.NotesPath));
            Assert.Equal(4, capture.Requests.Count);
            Call(window, "SaveMeTag", SelfSpeaker.Create("spkr1", "Alex"));
            view.SetSpeakerFilter(null);
            rows.SelectedIndex = 0;
            Assert.Equal(UiText.Get("EditMeTag"), Assert.IsType<MenuItem>(view.FindName("MarkMeMenu")).Header);
            Assert.All(rows.Items.Cast<TranscriptRow>().Where(row => row.SpeakerId == "spkr1"),
                row => Assert.Equal($"Alex ({UiText.Get("MeTag")})", row.DisplaySpeaker));
            var metadataBytes = File.ReadAllBytes(PersonalSummaryStore.PathFor(session));
            var notesBytes = File.ReadAllBytes(session.NotesPath);
            var notesText = notes.Text;
            try
            {
                PersonalSummaryStore.Save(session, new()
                {
                    AppliedSection = PersonalSummaryStore.Load(session)!.AppliedSection,
                    Provider = SummaryProviders.Ollama, Model = "legacy-unapplied-model"
                });
                Call(window, "LoadSessionContent", session);
                Assert.Contains(UiText.Get("PersonalNotesModelUnrecorded"), Assert.IsType<string>(notesTitle.ToolTip));
                Assert.DoesNotContain("legacy-unapplied-model", Assert.IsType<string>(notesTitle.ToolTip));
                File.WriteAllText(PersonalSummaryStore.PathFor(session), "{");
                Call(window, "LoadSessionContent", session);
                Assert.Contains(UiText.Get("PersonalNotesModelUnavailable"), Assert.IsType<string>(notesTitle.ToolTip));
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("WarningBanner")).Visibility);
                Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<TextBlock>(window.FindName("WarningText")).Text));
                Assert.Equal(notesText, notes.Text);
                Assert.Equal(notesBytes, File.ReadAllBytes(session.NotesPath));
            }
            finally
            {
                File.WriteAllBytes(PersonalSummaryStore.PathFor(session), metadataBytes);
                Call(window, "LoadSessionContent", session);
            }
            Assert.Contains(GeneratedWith(settings.OllamaModel), Assert.IsType<string>(notesTitle.ToolTip));
            Assert.Equal(4, capture.Requests.Count);
        }
        finally
        {
            capture.Release.TrySetResult();
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "CancelAiAsync")));
            window.Close();
            AppAppearance.Apply(application, AppThemes.Light);
        }

        void Summarize()
        {
            Assert.True(updateNotes.IsEnabled);
            Click(updateNotes);
            CompleteUiTask(root, ActiveUiAiTask(window));
            Pump(root);
        }

        string GeneratedWith(string model) => UiText.Format("SummaryGeneratedWith",
            SummaryProviders.Label(SummaryProviders.Ollama, UiText.Current), model);
    }

    private static void VerifyPersonalNotesCloseAndResume(string language, bool approveStartup)
    {
        using var folder = new TestFolder();
        var session = PersonalSummaryTests.Source(folder);
        var transcript = AppStorage.LoadTranscript(session);
        transcript.Segments[0] = transcript.Segments[0] with { Text = "LONG FIRST CONTRIBUTION " + new string('x', 9500) };
        AppStorage.SaveTranscript(session, transcript);
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.UiLanguage = language;
        var originalModel = settings.OllamaModel;
        var first = new PersonalSummaryTests.Capture { HoldOnCall = 2 };
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings,
            new RecordingEngine(new RecordingEngineTests.FakeFactory().Create),
            pipeline: SummaryOnlyPipeline(first), confirmCloseProcessing: _ => true);
        var root = MountDialog(window, new Size(1080, 720));
        SelectProcessingSession(window, session);
        typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        try
        {
            Call(window, "SummarizeAboutMe");
            CompleteUiTask(root, first.Held.Task);
            Assert.False(PersonalSummaryStore.Load(session)!.Completed);
            Assert.IsType<TextBox>(window.FindName("NotesBox")).Text = "PRIVATE NOTE TYPED BEFORE SAFE CLOSE";
            var closing = PrepareTestClose(window);
            CompleteUiTask(root, closing);
            Assert.True(closing.Result);
            Assert.True(first.Cancelled);
            var work = Assert.Single(PendingProcessingStore.Load(session)!.Work);
            Assert.Equal(ProcessingWorkKind.AboutMe, work.Kind);
            Assert.Equal(transcript.Me!.TagId, work.MeTagId);
            Assert.Equal(PersonalSummaryStore.Load(session)!.GenerationId, work.SummaryGenerationId);
            Assert.Equal("PRIVATE NOTE TYPED BEFORE SAFE CLOSE", File.ReadAllText(session.NotesPath));
        }
        finally
        {
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "CancelAiAsync")));
            window.Close();
        }
        var checkpoint = PersonalSummaryStore.Load(session)!;
        settings.OllamaModel = "new-default-must-not-change-saved-job";
        var resumed = new PersonalSummaryTests.Capture();
        var prompts = 0;
        var reopened = new MainWindow(new AppStorage(folder.File("reopened-preferences")), settings,
            new RecordingEngine(new RecordingEngineTests.FakeFactory().Create), pipeline: SummaryOnlyPipeline(resumed),
            confirmResumeProcessing: message =>
            {
                prompts++;
                Assert.Contains(UiText.Get("SummarizeAboutMe"), message);
                Assert.Contains(originalModel, message);
                return approveStartup;
            });
        root = MountDialog(reopened, new Size(1080, 720));
        try
        {
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(reopened, "OfferPendingProcessingAsync")));
            Assert.Equal(1, prompts);
            if (!approveStartup)
            {
                Assert.Empty(resumed.Requests);
                Assert.False(PendingProcessingStore.Load(session)!.PromptOnStartup);
                var plan = PendingProcessingStore.Load(session)!;
                Call(reopened, "SaveMeTag", SelfSpeaker.Create("spkr2", "Different person"));
                var changedTag = Assert.Throws<TargetInvocationException>(() => Call(reopened, "StartPendingProcessing", session, plan));
                Assert.Contains(UiText.Get("MeTagChanged"), changedTag.InnerException!.Message);
                Assert.Empty(resumed.Requests);
                Call(reopened, "SaveMeTag", transcript.Me);
                var denied = plan with
                {
                    Work = plan.Work.Select(work => work with
                    {
                        Options = work.Options! with
                        {
                            SummaryProvider = SummaryProviders.Copilot, SummaryModel = "synthetic-online-model", SummaryEndpoint = ""
                        }
                    }).ToList()
                };
                var noConsent = Assert.Throws<TargetInvocationException>(() => Call(reopened, "StartPendingProcessing", session, denied));
                Assert.Contains(UiText.Get("PendingProcessingConsentRequired"), noConsent.InnerException!.Message);
                Assert.Empty(resumed.Requests);
                var resume = Assert.IsType<Button>(reopened.FindName("TranscribeButton"));
                Assert.Equal(UiText.Get("ResumeProcessing"), resume.Content);
                Click(resume);
                CompleteUiTask(root, ActiveUiAiTask(reopened));
            }
            Assert.Null(PendingProcessingStore.Load(session));
            Assert.Equal(checkpoint.Text, resumed.Requests[0].Data.GetProperty("previousSummary").GetString());
            Assert.All(resumed.Requests, request => Assert.Equal(originalModel, request.Model));
            var result = PersonalSummaryStore.Load(session)!;
            Assert.True(result.Completed);
            Assert.Equal(checkpoint.GenerationId, result.AppliedGenerationId);
            Assert.StartsWith("PRIVATE NOTE TYPED BEFORE SAFE CLOSE", File.ReadAllText(session.NotesPath));
            Assert.True(PersonalNotesSection.ContainsOnce(File.ReadAllText(session.NotesPath), result.NotesSection));
            Assert.Equal(File.ReadAllText(session.NotesPath), Assert.IsType<TextBox>(reopened.FindName("NotesBox")).Text);
        }
        finally { reopened.Close(); }
    }
}
