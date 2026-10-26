using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class PersonalNotesSectionTests
{
    private const string OldSection = "## About me - Alex\nGenerated with: test-model\n\n- Original contribution.";
    private const string NewSection = "## About me - Alex\nGenerated with: new-model\n\n- New contribution and mention.";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void RefreshPreservesEveryCharacterOutsideTheOwnedBlock(string newline)
    {
        var prefix = "PRIVATE FIRST NOTE \t" + newline + newline;
        var suffix = newline + newline + "PRIVATE LAST NOTE  ";
        var original = prefix + OldSection.ReplaceLineEndings(newline) + suffix;
        var update = PersonalNotesSection.Update(original, OldSection, NewSection);
        Assert.Equal(prefix + NewSection.ReplaceLineEndings(newline) + suffix, update.Text);
        Assert.Equal(NewSection.ReplaceLineEndings(newline), update.Section);
        Assert.Equal(original.IndexOf("##", StringComparison.Ordinal), update.Start);
        Assert.Equal(0, update.MapPosition(0));
        Assert.Equal(update.Text.Length, update.MapPosition(original.Length));
        var retry = PersonalNotesSection.Update(update.Text, OldSection, NewSection);
        Assert.Equal(update.Text, retry.Text);
        Assert.Equal(0, retry.InsertedLength);
        Assert.Equal(0, retry.RemovedLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PRIVATE FIRST NOTE")]
    [InlineData("PRIVATE FIRST NOTE\n")]
    [InlineData("PRIVATE FIRST NOTE\r\n\r\n")]
    public void FirstInsertOnlyAppendsAndKeepsTheTypingPosition(string notes)
    {
        var update = PersonalNotesSection.Update(notes, "", NewSection);
        Assert.StartsWith(notes, update.Text);
        Assert.Contains("New contribution", update.Text);
        Assert.Equal(notes.Length, update.MapPosition(notes.Length));
        Assert.Equal(0, update.RemovedLength);
        Assert.True(PersonalNotesSection.ContainsOnce(update.Text, NewSection));
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("removed")]
    [InlineData("duplicated")]
    [InlineData("embedded")]
    public void ChangedOwnedSectionsRequireApprovalBeforeAnAppend(string change)
    {
        var notes = change switch
        {
            "edited" => OldSection.Replace("Original contribution", "MY PRIVATE EDIT"),
            "removed" => "MY PRIVATE REPLACEMENT",
            "duplicated" => OldSection + "\n\nPRIVATE MIDDLE\n\n" + OldSection.ReplaceLineEndings("\r\n"),
            _ => "I quoted this inline: " + OldSection
        };
        Assert.Throws<NotesSectionConflictException>(() => PersonalNotesSection.Update(notes, OldSection, NewSection));
        var appended = PersonalNotesSection.Update(notes, OldSection, NewSection, appendIfChanged: true);
        Assert.StartsWith(notes, appended.Text);
        Assert.Equal(notes.Length, appended.Start);
        Assert.Equal(0, appended.RemovedLength);
        Assert.True(PersonalNotesSection.ContainsOnce(appended.Text, NewSection));
    }

    [Fact]
    public void ConfirmedAppendKeepsEverythingEvenIfTheOldSectionWasRestoredDuringConfirmation()
    {
        var notes = "PRIVATE NOTE\n\n" + OldSection;
        var update = PersonalNotesSection.Update(notes, OldSection, NewSection, appendIfChanged: true);
        Assert.StartsWith(notes, update.Text);
        Assert.Equal(0, update.RemovedLength);
        Assert.True(PersonalNotesSection.ContainsOnce(update.Text, OldSection));
        Assert.True(PersonalNotesSection.ContainsOnce(update.Text, NewSection));
    }

    [Fact]
    public void MixedLineEndingsAndDuplicateNewResultsDoNotCauseAHiddenReplacement()
    {
        var notes = "PRIVATE\r\n\n" + OldSection + "\r\nPRIVATE END\r\n";
        var updated = PersonalNotesSection.Update(notes, OldSection, NewSection);
        Assert.StartsWith("PRIVATE\r\n\n", updated.Text);
        Assert.EndsWith("\r\nPRIVATE END\r\n", updated.Text);
        Assert.False(PersonalNotesSection.ContainsOnce(NewSection + "\n\n" + NewSection, NewSection));
        Assert.Throws<NotesSectionConflictException>(() =>
            PersonalNotesSection.Update(NewSection + "\n\n" + NewSection, OldSection, NewSection));
        Assert.Throws<InvalidDataException>(() => PersonalNotesSection.Update(notes, OldSection, ""));
    }
}
