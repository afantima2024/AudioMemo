using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Services;

using AudioMemo.Core.Localization;

namespace AudioMemo.AI;

public sealed class CopilotService
{
    public static string ConfigurationFolder => Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    public static string FindExecutable(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured) ||
                !Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException(UiText.Get("ChooseInstalledCli"));
            return configured;
        }
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links"));
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            var candidate = Path.Combine(directory.Trim('"'), "copilot.exe");
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException(UiText.Get("CliNotFound"));
    }

    public static CopilotConfiguration ReadConfiguration()
    {
        var path = Path.Combine(ConfigurationFolder, "config.json");
        var configuration = File.Exists(path) ? CopilotConfiguration.Parse(File.ReadAllText(path)) : new(null, []);
        var environmentModel = Environment.GetEnvironmentVariable("COPILOT_MODEL");
        return string.IsNullOrWhiteSpace(environmentModel) ? configuration : configuration with { Model = environmentModel };
    }

    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(string configuredExecutable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(FindExecutable(configuredExecutable));
        start.ArgumentList.Add("--no-auto-update");
        start.ArgumentList.Add("help");
        start.ArgumentList.Add("config");
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(UiText.Format("CliModelsFailed", result.Error.Trim()));
        var configuration = ReadConfiguration();
        return configuration.RecentModels.Concat(CopilotConfiguration.ParseModelHelp(result.Output))
            .Prepend(configuration.Model ?? "auto").Distinct().ToArray();
    }

    public static ProcessStartInfo CreateSummaryStartInfo(AppSettings settings, string prompt)
    {
        if (prompt.Length > 26_000)
            throw new ArgumentException("The summary prompt is too long for Windows process arguments.");
        Directory.CreateDirectory(AppStorage.WorkFolder);
        var start = new ProcessStartInfo(FindExecutable(settings.CopilotExecutable))
        {
            WorkingDirectory = AppStorage.WorkFolder
        };
        foreach (var argument in new[]
        {
            "-p", prompt, "--silent", "--stream", "off", "--no-color",
            "--available-tools=", "--deny-tool=shell", "--deny-tool=write", "--deny-tool=read",
            "--deny-tool=url", "--disable-builtin-mcps",
            "--no-custom-instructions", "--no-ask-user", "--no-auto-update",
            "--no-remote-export", "--log-level", "error"
        })
            start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(settings.SummaryModel))
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(settings.SummaryModel.Trim());
        }
        var mcpPath = Path.Combine(ConfigurationFolder, "mcp-config.json");
        if (File.Exists(mcpPath))
        {
            using var config = JsonDocument.Parse(File.ReadAllText(mcpPath), CopilotConfiguration.DocumentOptions);
            if (config.RootElement.TryGetProperty("mcpServers", out var servers))
                foreach (var server in servers.EnumerateObject())
                    start.ArgumentList.Add("--disable-mcp-server=" + server.Name);
        }
        // Do not inherit permission escalation from the terminal that launched AudioMemo.
        start.Environment["COPILOT_ALLOW_ALL"] = "false";
        start.Environment["COPILOT_ASSISTED_APPROVAL"] = "false";
        start.Environment["USE_TGREP"] = "false";
        return start;
    }

    public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous,
        bool live, CancellationToken cancellationToken) =>
        SummarizePromptAsync(settings, SummaryPrompt.Create(transcript, previous, live), cancellationToken);

    internal async Task<string> SummarizePromptAsync(AppSettings settings, string prompt, CancellationToken cancellationToken,
        bool normalizeText = true)
    {
        if (!settings.CloudConsent)
            throw new InvalidOperationException(UiText.Get("AllowTranscriptFirst"));
        var start = CreateSummaryStartInfo(settings, prompt);
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        return ReadResponse(result, normalizeText);
    }

    internal static string ReadResponse(ProcessResult result, bool normalizeText = true)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(UiText.Format("SummaryFailed", result.Error.Trim(), result.Output.Trim()));
        var summary = Regex.Replace(result.Output, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim();
        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidDataException(UiText.Get("EmptySummary"));
        if (summary.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
            throw new InvalidDataException(UiText.Get("OversizedSummary"));
        if (normalizeText)
            summary = ChineseText.ToSimplified(summary);
        if (summary.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
            throw new InvalidDataException(UiText.Get("OversizedSummary"));
        return summary;
    }

    public static void Login(AppSettings settings)
    {
        var start = new ProcessStartInfo(FindExecutable(settings.CopilotExecutable)) { UseShellExecute = true };
        start.ArgumentList.Add("login");
        Process.Start(start)?.Dispose();
    }
}
