using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AudioMemo.Services;
using Microsoft.Win32;

namespace AudioMemo.Tests;

public sealed class InstallerPackagingTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));

    [Fact]
    public void ReleaseVersionIsSharedByApplicationCoreAndWindowsMetadata()
    {
        var properties = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var release = Assert.Single(properties.Descendants("Version")).Value;
        Assert.Matches(@"^\d+\.\d+\.\d+$", release);
        foreach (var assembly in new[] { typeof(MainWindow).Assembly, typeof(AudioMemo.Core.Models.AppSettings).Assembly })
        {
            Assert.Equal(release, assembly.GetName().Version!.ToString(3));
            var info = FileVersionInfo.GetVersionInfo(assembly.Location);
            Assert.Equal(release + ".0", info.FileVersion);
            Assert.Equal(release, info.ProductVersion);
        }
        foreach (var project in new[] { @"src\AudioMemo\AudioMemo.csproj", @"src\AudioMemo.Core\AudioMemo.Core.csproj" })
            Assert.Empty(XDocument.Load(Path.Combine(RepositoryRoot, project)).Descendants("Version"));
    }

    [Fact]
    public void InstallerIsPerUserOfflineAndDoesNotRecursivelyDeleteUserData()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, @"packaging\AudioMemo.nsi"));
        var builder = File.ReadAllText(Path.Combine(RepositoryRoot, @"scripts\Package-Setup.ps1"));
        Assert.Contains("RequestExecutionLevel user", script);
        Assert.Contains("SetShellVarContext current", script);
        Assert.Contains(@"$LOCALAPPDATA\Programs\AudioMemo", script);
        Assert.Contains("MUI_LANGUAGE \"English\"", script);
        Assert.Contains("MUI_LANGUAGE \"SimpChinese\"", script);
        Assert.Contains("Section /o \"$(DesktopSection)\"", script);
        Assert.Contains("Call AcquireAudioMemoMutex", script);
        Assert.Contains("Call un.AcquireAudioMemoMutex", script);
        Assert.DoesNotMatch(@"(?im)^\s*(Exec|ExecShell|ExecWait|ExecShellWait|RMDir\s+/r)\b", script);
        Assert.DoesNotContain("MUI_FINISHPAGE_RUN", script);
        Assert.DoesNotContain("HKLM", script);
        Assert.DoesNotContain("taskkill", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TerminateProcess", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"(?im)^\s*Delete\s+.*[*?]", script);
        Assert.Contains("foreach ($entry in $manifest) { $include.Add('Delete \"$INSTDIR\\'", builder);
        Assert.DoesNotMatch(@"(?i)RMDir\s+/r", builder);
        Assert.DoesNotContain("Remove-Item -LiteralPath $published", builder);
        Assert.DoesNotContain("Remove-Item -LiteralPath $output", builder);
    }

    [SetupPackageFact]
    public void BuiltInstallerHasMatchingMetadataChecksumAndCurrentWindowsIcons()
    {
        var package = Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SETUP")!;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(package, ".manifest.json")));
        var bytes = File.ReadAllBytes(package);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Equal(hash, manifest.RootElement.GetProperty("InstallerSHA256").GetString());
        Assert.Equal($"{hash} *{Path.GetFileName(package)}", File.ReadAllText(package + ".sha256").Trim());
        Assert.False(manifest.RootElement.GetProperty("Signed").GetBoolean());
        var info = FileVersionInfo.GetVersionInfo(package);
        var release = manifest.RootElement.GetProperty("Version").GetString();
        Assert.Equal(release + ".0", info.FileVersion);
        Assert.Equal(release, info.ProductVersion);
        Assert.Equal("Afanti Ma", info.CompanyName);
        Assert.Equal("AudioMemo", info.ProductName);
        Assert.Equal("Copyright \u00a9 2026 Afanti Ma", info.LegalCopyright);
        foreach (var (size, png) in IconAssetTests.ReadIconFrames())
        {
            Assert.True(bytes.AsSpan().IndexOf(png) >= 0, $"Setup is missing the {size}-pixel app icon.");
            Assert.Equal(IconAssetTests.ExtractSourceWindowsIcon(size), IconAssetTests.ExtractWindowsIcon(package, size));
        }
    }

    [NsisFact]
    public async Task GuardUsesTheCurrentUserSidAndNeverClosesAnExistingSession()
    {
        using var folder = new TestFolder();
        var probe = folder.File("guard.exe");
        var script = folder.File("guard.nsi");
        var prefix = @"Local\AudioMemo-setup-test-" + Guid.NewGuid().ToString("N") + "-";
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var include = Path.Combine(RepositoryRoot, @"packaging\SetupGuard.nsh");
        File.WriteAllText(script, $$"""
            Unicode true
            RequestExecutionLevel user
            SilentInstall silent
            Name "AudioMemo isolated setup guard check"
            OutFile "{{probe}}"
            !define AUDIOMEMO_MUTEX_PREFIX "{{prefix}}"
            !include "{{include}}"
            LoadLanguageFile "${NSISDIR}\Contrib\Language files\English.nlf"
            LangString UnsupportedWindows 1033 "Unsupported Windows"
            LangString CloseAudioMemo 1033 "Isolated test mutex is busy"
            LangString IdentityCheckFailed 1033 "Cannot obtain current user identity"
            !insertmacro DefineAudioMemoGuard ""
            Function .onInit
                Call AcquireAudioMemoMutex
                SetErrorLevel 0
                Quit
            FunctionEnd
            Section
            SectionEnd
            """, new UTF8Encoding(false));
        var compile = await RunAsync(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_NSIS")!,
            "/NOCONFIG", "/INPUTCHARSET", "UTF8", "/WX", "/V2", script);
        Assert.True(compile.ExitCode == 0, compile.Output + compile.Error);
        Assert.Equal(0, (await RunAsync(probe, "/S")).ExitCode);
        using (var mutex = new Mutex(false, prefix + sid, out var created))
        {
            Assert.True(created);
            Assert.Equal(3, (await RunAsync(probe, "/S")).ExitCode);
            Assert.True(Mutex.TryOpenExisting(prefix + sid, out var retained));
            Assert.NotNull(retained);
            retained.Dispose();
        }
        Assert.Equal(0, (await RunAsync(probe, "/S")).ExitCode);
    }

    [NsisFact]
    public async Task IsolatedSetupSupportsUpdatesAndUninstallWithoutDeletingAddedFiles()
    {
        using var folder = new TestFolder();
        var installed = folder.File("installed");
        var startMenu = folder.File("shortcuts");
        var desktop = folder.File("desktop");
        Directory.CreateDirectory(desktop);
        var registryPath = @"Software\AudioMemo.Tests\" + Guid.NewGuid().ToString("N");
        var prefix = @"Local\AudioMemo-setup-test-" + Guid.NewGuid().ToString("N") + "-";
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var script = folder.File("setup.nsi");
        var payload = folder.File("payload.nsh");
        var content = folder.File("synthetic.txt");
        var ignoredOverride = folder.File("not-an-installation-folder");
        Directory.CreateDirectory(ignoredOverride);
        File.WriteAllText(Path.Combine(ignoredOverride, "keep.txt"), "unchanged");
        var template = File.ReadAllText(Path.Combine(RepositoryRoot, @"packaging\AudioMemo.nsi"));
        template = template
            .Replace(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioMemo", registryPath, StringComparison.Ordinal)
            .Replace(@"$LOCALAPPDATA\Programs\AudioMemo", installed, StringComparison.Ordinal)
            .Replace(@"$SMPROGRAMS\AudioMemo", startMenu, StringComparison.Ordinal)
            .Replace(@"$DESKTOP\AudioMemo.lnk", Path.Combine(desktop, "AudioMemo.lnk"), StringComparison.Ordinal);
        Assert.DoesNotContain("$LOCALAPPDATA", template);
        Assert.DoesNotContain("$SMPROGRAMS", template);
        Assert.DoesNotContain("$DESKTOP", template);
        Assert.DoesNotContain(@"CurrentVersion\Uninstall", template);
        File.Copy(Path.Combine(RepositoryRoot, @"packaging\SetupGuard.nsh"), folder.File("SetupGuard.nsh"));
        File.WriteAllText(payload, $$"""
            !macro InstallPayload
            SetOutPath "$INSTDIR"
            File /oname=AudioMemo.exe "{{content}}"
            SetOutPath "$INSTDIR\support\Docs"
            File /oname=owned.txt "{{content}}"
            !macroend
            !macro RemovePayloadFiles
            Delete "$INSTDIR\AudioMemo.exe"
            Delete "$INSTDIR\support\Docs\owned.txt"
            !macroend
            !macro RemovePayloadDirectories
            RMDir "$INSTDIR\support\Docs"
            RMDir "$INSTDIR\support"
            !macroend
            """, new UTF8Encoding(false));
        try
        {
            var first = await CompileAsync("first", false);
            Assert.Equal(0, (await RunAsync(first, "/S", "/D=" + ignoredOverride)).ExitCode);
            Assert.Equal("first", File.ReadAllText(Path.Combine(installed, "AudioMemo.exe")));
            Assert.Equal("unchanged", File.ReadAllText(Path.Combine(ignoredOverride, "keep.txt")));
            Assert.Single(Directory.GetFiles(ignoredOverride));
            Assert.True(File.Exists(Path.Combine(startMenu, "AudioMemo.lnk")));
            Assert.False(File.Exists(Path.Combine(desktop, "AudioMemo.lnk")));
            using (var registration = registry.OpenSubKey(registryPath))
            {
                Assert.NotNull(registration);
                Assert.Equal(installed, registration.GetValue("InstallLocation"));
            }
            var added = Path.Combine(installed, "support", "Docs", "keep-notes.txt");
            File.WriteAllText(added, "user-added content");
            var second = await CompileAsync("second", true);
            using (var mutex = new Mutex(false, prefix + sid))
            {
                Assert.Equal(3, (await RunAsync(second, "/S")).ExitCode);
                Assert.Equal("first", File.ReadAllText(Path.Combine(installed, "AudioMemo.exe")));
            }
            Assert.Equal(0, (await RunAsync(second, "/S")).ExitCode);
            Assert.Equal("second", File.ReadAllText(Path.Combine(installed, "AudioMemo.exe")));
            Assert.True(File.Exists(Path.Combine(desktop, "AudioMemo.lnk")));
            var uninstaller = Path.Combine(installed, "Uninstall.exe");
            using (var mutex = new Mutex(false, prefix + sid))
            {
                Assert.Equal(3, (await RunAsync(uninstaller, "/S", "_?=" + installed)).ExitCode);
                Assert.True(File.Exists(Path.Combine(installed, "AudioMemo.exe")));
            }
            Assert.Equal(0, (await RunAsync(uninstaller, "/S")).ExitCode);
            var wait = Stopwatch.StartNew();
            while (File.Exists(uninstaller) && wait.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(100);
            Assert.False(File.Exists(uninstaller));
            Assert.False(File.Exists(Path.Combine(installed, "AudioMemo.exe")));
            Assert.False(File.Exists(Path.Combine(installed, "support", "Docs", "owned.txt")));
            Assert.False(File.Exists(Path.Combine(startMenu, "AudioMemo.lnk")));
            Assert.False(File.Exists(Path.Combine(desktop, "AudioMemo.lnk")));
            Assert.Null(registry.OpenSubKey(registryPath));
            Assert.Equal("user-added content", File.ReadAllText(added));

            Assert.Equal(5, (await RunAsync(first, "/S")).ExitCode);
            Assert.Equal("user-added content", File.ReadAllText(added));
        }
        finally { registry.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false); }

        async Task<string> CompileAsync(string contents, bool desktopSelected)
        {
            File.WriteAllText(content, contents);
            File.WriteAllText(script, desktopSelected
                ? template.Replace("Section /o \"$(DesktopSection)\"", "Section \"$(DesktopSection)\"", StringComparison.Ordinal)
                : template, new UTF8Encoding(false));
            var executable = folder.File(contents + "-setup.exe");
            var result = await RunAsync(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_NSIS")!,
                "/NOCONFIG", "/INPUTCHARSET", "UTF8", "/WX", "/V2",
                "/DAUDIOMEMO_MUTEX_PREFIX=" + prefix,
                "/DPAYLOAD_INCLUDE=" + payload, "/DOUTPUT_FILE=" + executable,
                "/DAPP_ICON=" + Path.Combine(RepositoryRoot, @"src\AudioMemo\Assets\AudioMemo.ico"),
                "/DAPP_VERSION=0.1.0.0", "/DDISPLAY_VERSION=0.1.0", "/DAPP_COPYRIGHT=Fixture",
                "/DOUTPUT_NAME=fixture.exe", "/DINSTALL_SIZE_KB=1", script);
            Assert.True(result.ExitCode == 0, result.Output + result.Error);
            return executable;
        }
    }

    private static Task<ProcessResult> RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(30), CancellationToken.None);
    }
}

public sealed class NsisFactAttribute : FactAttribute
{
    public NsisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_NSIS")))
            Skip = "Set AUDIOMEMO_TEST_NSIS to the official makensis.exe to check the installer guard in isolation.";
    }
}

public sealed class SetupPackageFactAttribute : FactAttribute
{
    public SetupPackageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SETUP")))
            Skip = "Set AUDIOMEMO_TEST_SETUP to the built AudioMemo Setup.exe to check its identity and icons.";
    }
}
