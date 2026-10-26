using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace AudioMemo.AI;

// Run native inference in a child process: even a native crash cannot take the recorder down.
public static class SpeechWorker
{
    public static void ConfigureRuntime(string acceleration)
    {
        RuntimeOptions.RuntimeLibraryOrder = acceleration switch
        {
            "cpu" => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "vulkan" => [RuntimeLibrary.Vulkan],
            "auto" => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ => throw new ArgumentException("Unknown speech acceleration mode.")
        };
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (arguments.Length != 4 || arguments[0] != "--speech-worker")
                throw new ArgumentException("Expected --speech-worker <model> <16kHz-WAV> <language>.");
            ConfigureRuntime("cpu");
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            using var factory = WhisperFactory.FromPath(arguments[1]);
            using var processor = factory.CreateBuilder()
                .WithLanguage(arguments[3])
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4))
                .WithNoContext()
                .Build();
            await using var audio = File.OpenRead(arguments[2]);
            await foreach (var result in processor.ProcessAsync(audio))
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                    Console.WriteLine(JsonSerializer.Serialize(new TranscriptSegment(
                        result.Start.TotalSeconds, result.End.TotalSeconds, result.Text.Trim())));
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static async Task<int> RunServerAsync(string[] arguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        try
        {
            if (arguments.Length is not (5 or 6) || arguments[4] is not ("draft" or "final") ||
                (arguments.Length == 6 && arguments[5] != "--with-hints"))
                throw new ArgumentException("Expected --speech-server <model> <language> <auto|cpu|vulkan> <draft|final>.");
            var prompt = "";
            if (arguments.Length == 6)
            {
                var configuration = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("Missing speech hints.");
                prompt = JsonSerializer.Deserialize<string>(configuration) ?? throw new InvalidDataException("Empty speech hints.");
                if (Encoding.UTF8.GetByteCount(prompt) > SpeechVocabulary.MaximumPromptBytes || prompt.Any(char.IsControl))
                    throw new InvalidDataException("Invalid speech hint size or format.");
            }
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            ConfigureRuntime(arguments[3]);
            using var logging = LogProvider.AddLogger((_, message) =>
            {
                if (message is not null && (message.Contains("backend", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)))
                    Console.Error.WriteLine(message);
            });
            using var factory = WhisperFactory.FromPath(arguments[1],
                new WhisperFactoryOptions { UseGpu = arguments[3] != "cpu" });
            var builder = factory.CreateBuilder().WithLanguage(arguments[2])
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4)).WithNoContext();
            if (prompt.Length > 0)
                builder.WithPrompt(prompt).WithCarryInitialPrompt(true);
            if (arguments[4] == "draft")
                builder.WithSingleSegment();
            using var processor = builder.Build();
            if (RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan && !HasVulkanDevice())
                throw new InvalidOperationException("Vulkan loaded, but no compatible GPU is available. Choose CPU or Automatic.");
            await WriteMessageAsync(new("ready", Runtime: RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"));
            string? request;
            while ((request = await Console.In.ReadLineAsync()) is not null)
            {
                var path = JsonSerializer.Deserialize<string>(request) ?? throw new InvalidDataException("Empty speech request.");
                var segments = new List<TranscriptSegment>();
                await using (var audio = File.OpenRead(path))
                {
                    await foreach (var result in processor.ProcessAsync(audio))
                    {
                        if (!string.IsNullOrWhiteSpace(result.Text))
                            segments.Add(new(result.Start.TotalSeconds, result.End.TotalSeconds, result.Text.Trim()));
                    }
                }
                await WriteMessageAsync(new("result", segments));
            }
            return 0;
        }
        catch (Exception ex)
        {
            await WriteMessageAsync(new("error", Error: $"{ex.GetType().Name}: {ex.Message}"));
            return 1;
        }
    }

    private static async Task WriteMessageAsync(SpeechWorkerMessage message)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message));
        await Console.Out.FlushAsync();
    }

    private static bool HasVulkanDevice()
    {
        var library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "vulkan", "win-x64", "ggml-vulkan-whisper.dll"));
        try
        {
            var count = Marshal.GetDelegateForFunctionPointer<VulkanDeviceCount>(
                NativeLibrary.GetExport(library, "ggml_backend_vk_get_device_count"));
            return count() > 0;
        }
        finally { NativeLibrary.Free(library); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VulkanDeviceCount();
}

public sealed record SpeechWorkerMessage(string Kind, IReadOnlyList<TranscriptSegment>? Segments = null,
    string? Runtime = null, string? Error = null);
