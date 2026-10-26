using System.Runtime.InteropServices;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class RecycleBinTests
{
    [RecycleFact]
    [Trait("Category", "Recycle")]
    public async Task OnlyTheSyntheticSessionMovesToRecycleBinAndCanBeRestored()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new AppSettings { SaveFolder = folder.Root }, "Synthetic recycle check");
        File.WriteAllText(session.WavePath, "Synthetic test bytes, not a recording.");
        SessionCommands.Recycle(folder.Root, session);
        Assert.False(Directory.Exists(session.Folder));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic trash = shell.NameSpace(10);
        dynamic items = trash.Items();
        object? match = null;
        try
        {
            for (var index = 0; index < (int)items.Count; index++)
            {
                dynamic item = items.Item(index);
                if (string.Equals((string?)item.ExtendedProperty("System.Recycle.DeletedFrom"), folder.Root,
                    StringComparison.OrdinalIgnoreCase))
                {
                    match = item;
                    break;
                }
                Marshal.FinalReleaseComObject(item);
            }
            Assert.NotNull(match);
            dynamic recycled = match;
            Assert.Equal(Path.GetFileName(session.Folder), (string)recycled.Name);
            recycled.InvokeVerb("undelete");
            for (var attempt = 0; attempt < 50 && !File.Exists(session.MetadataPath); attempt++)
                await Task.Delay(100);
            Assert.True(File.Exists(session.MetadataPath), "The synthetic session could not be restored.");
            Assert.Equal("Synthetic test bytes, not a recording.", File.ReadAllText(session.WavePath));
            Assert.Equal(session.Id, AppStorage.LoadSessions(folder.Root).Sessions.Single().Id);
        }
        finally
        {
            if (match is not null)
                Marshal.FinalReleaseComObject(match);
            Marshal.FinalReleaseComObject(items);
            Marshal.FinalReleaseComObject(trash);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

public sealed class RecycleFactAttribute : FactAttribute
{
    public RecycleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_RECYCLE") != "1")
            Skip = "Set AUDIOMEMO_TEST_RECYCLE=1 to recycle and restore a generated temporary session only.";
    }
}
