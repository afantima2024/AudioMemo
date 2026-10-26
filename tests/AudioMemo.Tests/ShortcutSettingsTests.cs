using System.Text.Json;
using System.Windows.Input;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class ShortcutSettingsTests
{
    [Fact]
    public void EveryDefaultCommandHasAUniqueValidLocalizedShortcut()
    {
        var settings = new ShortcutSettings();
        settings.Validate();
        Assert.Equal(ShortcutSettings.Commands.Count, ShortcutSettings.Commands.Select(command => command.Id).Distinct().Count());
        foreach (var command in ShortcutSettings.Commands)
        {
            Assert.False(string.IsNullOrWhiteSpace(new UiText("en")[command.LabelKey]));
            Assert.False(string.IsNullOrWhiteSpace(new UiText("zh-CN")[command.LabelKey]));
            Assert.Equal(command.DefaultGesture, ShortcutSettings.Normalize(command.DefaultGesture));
        }
        Assert.True(ShortcutInput.Matches(settings, "search", Key.F, ModifierKeys.Control));
        Assert.True(ShortcutInput.Matches(settings, "latest", Key.End, ModifierKeys.Control));
        Assert.True(ShortcutInput.Matches(settings, "pageUp", Key.PageUp, ModifierKeys.None));
        Assert.True(ShortcutInput.Matches(settings, "model", Key.Return, ModifierKeys.Control));
        Assert.Equal("Ctrl+,", ShortcutInput.Display(settings.Gesture("settings")));
        Assert.False(ShortcutInput.Matches(settings, "search", Key.F, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void OverridesAndDisabledCommandsPersistWithoutKeepingOldBindings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Format":"WAV"}""")!;
        Assert.Empty(settings.Shortcuts.Overrides);
        settings.Shortcuts.Overrides["search"] = " ctrl + alt + g ";
        settings.Shortcuts.Overrides["edit"] = "";
        settings.Shortcuts.Overrides["help"] = "F8";
        settings.Shortcuts.Overrides["settings"] = "Ctrl+Alt+OemComma";
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.Validate();
        Assert.Equal("Ctrl+Alt+G", restored.Shortcuts.Gesture("search"));
        Assert.True(ShortcutInput.Matches(restored.Shortcuts, "search", Key.G, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.False(ShortcutInput.Matches(restored.Shortcuts, "search", Key.F, ModifierKeys.Control));
        Assert.False(ShortcutInput.Matches(restored.Shortcuts, "edit", Key.F2, ModifierKeys.None));
        Assert.True(ShortcutInput.Matches(restored.Shortcuts, "help", Key.F8, ModifierKeys.None));
        Assert.False(ShortcutInput.Matches(restored.Shortcuts, "help", Key.F1, ModifierKeys.None));
        Assert.True(ShortcutInput.Matches(restored.Shortcuts, "settings", Key.OemComma, ModifierKeys.Control | ModifierKeys.Alt));
        Assert.False(ShortcutInput.Matches(restored.Shortcuts, "settings", Key.OemComma, ModifierKeys.Control));
    }

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+X")]
    [InlineData("Ctrl+A")]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Shift+Left")]
    [InlineData("Ctrl+Home")]
    [InlineData("Ctrl+End")]
    [InlineData("PageDown")]
    [InlineData("Shift+PageUp")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Space")]
    [InlineData("Shift+F10")]
    [InlineData("Escape")]
    [InlineData("Win+R")]
    [InlineData("Ctrl+Ctrl+P")]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Unknown")]
    public void UnsafeOrInvalidOverridesAreRejected(string gesture)
    {
        var settings = new ShortcutSettings { Overrides = new() { ["record"] = gesture } };
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void ConflictsAndMalformedSavedMapsAreReported()
    {
        var conflict = new ShortcutSettings { Overrides = new() { ["record"] = "ctrl+n" } };
        var error = Assert.Throws<InvalidDataException>(conflict.Validate);
        Assert.Contains("Ctrl+N", error.Message);
        Assert.Throws<InvalidDataException>(() => new ShortcutSettings { Overrides = new() { ["unknown"] = "F12" } }.Validate());
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<ShortcutSettings>("""{"Overrides":null}""")!.Validate());
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<ShortcutSettings>("""{"Overrides":{"record":null}}""")!.Validate());
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<AppSettings>("""{"Shortcuts":null}""")!.Validate());
    }

    [Fact]
    public void GlobalCommandsWorkFromNotesAndSearchButTranscriptNavigationStaysScoped()
    {
        var settings = new ShortcutSettings();
        Assert.Equal("search", settings.Find("Ctrl+F", false, false)?.Id);
        Assert.Equal("search", settings.Find("Ctrl+F", false, true)?.Id);
        Assert.Equal("record", settings.Find("Ctrl+R", false, false)?.Id);
        Assert.Null(settings.Find("Ctrl+End", false, false));
        Assert.Null(settings.Find("Ctrl+End", false, true));
        Assert.Equal("latest", settings.Find("Ctrl+End", true, false)?.Id);
        Assert.Null(settings.Find("Escape", false, false));
        Assert.Equal("searchClear", settings.Find("Escape", false, true)?.Id);
        Assert.Equal("searchClear", settings.Find("Escape", true, false)?.Id);
        Assert.Null(settings.Find("Ctrl+C", true, false));
    }
}
