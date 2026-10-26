using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void ChineseResourcesCoverEveryEnglishKeyAndPreserveFormatArguments()
    {
        var resources = new ResourceManager("AudioMemo.Core.Localization.Strings", typeof(UiText).Assembly);
        var english = ReadResources(resources, CultureInfo.InvariantCulture);
        var chinese = ReadResources(resources, CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Equal(english.Keys.Order(), chinese.Keys.Order());
        foreach (var (key, value) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(chinese[key]), key);
            if (key == "LlmLabel")
                Assert.Equal("LLM", chinese[key]);
            else
                Assert.Contains(chinese[key], character => character is >= '\u4e00' and <= '\u9fff');
            Assert.True(chinese[key] == ChineseText.ToSimplified(chinese[key]),
                $"The Chinese resource '{key}' contains non-Simplified text.");
            Assert.Equal(CompositeFormat.Parse(value).MinimumArgumentCount,
                CompositeFormat.Parse(chinese[key]).MinimumArgumentCount);
            Assert.Equal(ArgumentIndexes(value), ArgumentIndexes(chinese[key]));
            Assert.Equal(value, new UiText("en")[key]);
            Assert.Equal(chinese[key], new UiText("zh-CN")[key]);
        }
    }

    [Fact]
    public void SwitchingLanguagesNotifiesBindingsAndCanSwitchBack()
    {
        var text = new UiText();
        var changes = new List<string?>();
        text.PropertyChanged += (_, change) => changes.Add(change.PropertyName);

        text.ChangeLanguage("zh-CN");
        Assert.Equal("\u8bbe\u7f6e", text["Settings"]);
        Assert.Equal("zh-CN", text.Culture.Name);
        Assert.Contains("Item[]", changes);
        Assert.Contains(nameof(UiText.Culture), changes);

        changes.Clear();
        text.ChangeLanguage("zh-CN");
        Assert.Empty(changes);
        text.ChangeLanguage("en");
        Assert.Equal("Settings", text["Settings"]);
        Assert.Contains("Item[]", changes);
    }

    [Fact]
    public void RecordingDatesFollowTheWindowsExplicitLanguage()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new StackPanel { Language = XmlLanguage.GetLanguage("en") };
                var date = new DateTimeOffset(2026, 10, 13, 14, 16, 0, TimeSpan.FromHours(8));
                var label = new TextBlock();
                root.Children.Add(label);
                label.SetBinding(TextBlock.TextProperty, new Binding
                {
                    Source = date,
                    StringFormat = "{0:MMM d  -  HH:mm}"
                });
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.Contains("Oct", label.Text);

                root.Language = XmlLanguage.GetLanguage("zh-CN");
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Assert.Equal(date.ToString("MMM d  -  HH:mm", CultureInfo.GetCultureInfo("zh-CN")), label.Text);
                Assert.DoesNotContain("Oct", label.Text);
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WPF language-binding check did not finish.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void InvalidLanguageAndMissingResourceAreReportedExplicitly()
    {
        var text = new UiText("zh-CN");
        Assert.Throws<ArgumentException>(() => text.ChangeLanguage("invalid"));
        Assert.Equal("zh-CN", text.Culture.Name);
        Assert.Throws<InvalidOperationException>(() => text["NonexistentTranslation"]);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void SpeechPresetsStoreWhisperCodesRatherThanTranslatedLabels(string uiLanguage)
    {
        var text = new UiText(uiLanguage);
        var options = SpeechLanguages.GetOptions(text);
        Assert.Equal(["auto", "zh", "en"], options.Select(option => option.Code));
        foreach (var option in options)
        {
            Assert.Equal(option.Code, SpeechLanguages.ParseInput(option.Label, text));
            Assert.Equal(option.Label, option.ToString());
        }
        Assert.All(UiText.InterfaceLanguages, option => Assert.Equal(option.Label, option.ToString()));
        Assert.Equal("zh", SpeechLanguages.ParseInput(" ZH ", text));
        Assert.Throws<ArgumentException>(() => SpeechLanguages.ParseInput(" JA ", text));
        Assert.Throws<ArgumentException>(() => SpeechLanguages.ParseInput("fr", text));
        Assert.Throws<ArgumentException>(() => SpeechLanguages.ParseInput("zh-TW", text));
        Assert.Throws<ArgumentException>(() => SpeechLanguages.ParseInput(" ", text));
        Assert.Equal(["en", "zh-CN"], UiText.InterfaceLanguages.Select(option => option.Code));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void CompactRecordingLanguageOptionsUseTheSameRecognitionCodes(string language)
    {
        var text = new UiText(language);
        var options = SpeechLanguages.GetOptions(text, compact: true);
        Assert.Equal(SpeechLanguages.GetOptions(text).Select(option => option.Code), options.Select(option => option.Code));
        Assert.Equal(text["SpeechLanguageChineseShort"], options.Single(option => option.Code == "zh").Label);
        Assert.All(options, option => Assert.Equal(option.Code, SpeechLanguages.ParseInput(option.Code, text)));
    }

    [Theory]
    [InlineData("en", "zh")]
    [InlineData("zh-CN", "en")]
    [InlineData("zh-CN", "auto")]
    [InlineData("zh-CN", "zh")]
    public void InterfaceAndSpeechLanguagesPersistIndependently(string uiLanguage, string speechLanguage)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            UiLanguage = uiLanguage,
            Language = speechLanguage,
            SummaryModel = "example-model"
        };
        var path = folder.File("settings.json");
        AtomicFile.WriteJson(path, settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        Assert.NotNull(restored);
        restored.Validate();
        Assert.Equal(uiLanguage, restored.UiLanguage);
        Assert.Equal(speechLanguage, restored.Language);
        Assert.Equal("example-model", restored.SummaryModel);
        Assert.False(restored.CloudConsent);
        Assert.False(restored.LiveSummary);
        Assert.False(restored.LiveTranscription);
    }

    [Fact]
    public void ExistingSettingsKeepEnglishAndTheirCustomSpeechLanguage()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Language":"ja","Format":"MP3"}""");
        Assert.NotNull(settings);
        settings.Validate();
        Assert.Equal("en", settings.UiLanguage);
        Assert.Equal("ja", settings.Language);
        Assert.Equal("MP3", settings.Format);
    }

    [Theory]
    [InlineData("""{"UiLanguage":"unsupported"}""")]
    [InlineData("""{"UiLanguage":null}""")]
    [InlineData("""{"Language":null}""")]
    [InlineData("""{"Language":""}""")]
    public void InvalidSavedLanguageSettingsAreRejected(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(settings);
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void UntitledRecordingsUseTheirActualStartDateAndTime()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.Root, UiLanguage = "zh-CN" };
        var session = AppStorage.CreateSession(settings, "");
        Assert.Equal(session.CreatedAt.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture), session.Title);
        Assert.Equal(-1, session.Title.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.Equal("Recording", session.Status);
        var (sessions, errors) = AppStorage.LoadSessions(folder.Root);
        Assert.Empty(errors);
        Assert.Equal(session.Title, Assert.Single(sessions).Title);
    }

    [Fact]
    public void ChineseSummaryContentRemainsDataAndHeadingsFollowTheConversationLanguage()
    {
        const string transcript = "\u4f1a\u8bae\u5b9a\u5728\u661f\u671f\u4e94\uff0c\u9884\u7b97\u5df2\u6279\u51c6\u3002";
        var prompt = SummaryPrompt.Create(transcript, "", false);
        using var payload = JsonDocument.Parse(prompt[prompt.IndexOf("{\"previousSummary\"", StringComparison.Ordinal)..]);
        Assert.Equal(transcript, payload.RootElement.GetProperty("transcript").GetString());
        Assert.Contains("Translate these section headings into the conversation's primary language too.", prompt);
        Assert.Contains("must use Simplified Chinese (zh-CN), never Traditional Chinese.", prompt);
        Assert.Contains("Keep English content in English.", prompt);
    }

    [Fact]
    public void TraditionalPromptDataIsPreservedWhileTheOutputPolicyRequiresSimplifiedChinese()
    {
        const string traditional = "\u6703\u8b70\u8a18\u9304\uff1aAlex \u5728\u9031\u4e94\u6e96\u5099 API slides\u3002";
        var prompt = SummaryPrompt.Create(traditional, traditional, true);
        using var payload = JsonDocument.Parse(prompt[prompt.IndexOf("{\"previousSummary\"", StringComparison.Ordinal)..]);
        Assert.Equal(traditional, payload.RootElement.GetProperty("transcript").GetString());
        Assert.Equal(traditional, payload.RootElement.GetProperty("previousSummary").GetString());
        Assert.Contains("must use Simplified Chinese (zh-CN), never Traditional Chinese.", prompt);
    }

    private static Dictionary<string, string> ReadResources(ResourceManager resources, CultureInfo culture)
    {
        var set = resources.GetResourceSet(culture, true, false);
        Assert.NotNull(set);
        return set.Cast<DictionaryEntry>().ToDictionary(
            entry => Assert.IsType<string>(entry.Key),
            entry => Assert.IsType<string>(entry.Value));
    }

    private static string[] ArgumentIndexes(string value) =>
        Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[,}:])")
            .Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
}
