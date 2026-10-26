using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class PerformanceRegressionTests
{
    [Theory]
    [InlineData(1, 0L)]
    [InlineData(7, 0L)]
    [InlineData(32, 172_800_000_000L)]
    [InlineData(97, 172_800_000_037L)]
    [InlineData(768_000, 37L)]
    public void OptimizedMixerMatchesOriginalSamplesAndCountersExactly(int capacity, long start)
    {
        var actual = new TimelineMixer(capacity, start);
        var expected = new OriginalMixer(capacity, start);
        var random = new Random(capacity);
        for (var iteration = 0; iteration < 800; iteration++)
        {
            for (var source = 0; source < 2; source++)
            {
                var first = actual.Position + random.Next(-8, 9);
                var samples = Enumerable.Range(0, 2 * random.Next(Math.Min(capacity, 64) + 1))
                    .Select(_ => random.Next(32) switch
                    {
                        0 => float.NaN,
                        1 => float.PositiveInfinity,
                        2 => float.NegativeInfinity,
                        3 => float.MaxValue,
                        4 => -float.MaxValue,
                        _ => random.Next(-4000, 4001) / 1000f
                    }).ToArray();
                var gain = random.Next(5) / 2f;
                var expectedError = Record.Exception(() => expected.Add(first, samples, gain));
                var actualError = Record.Exception(() => actual.Add(first, samples, gain));
                Assert.Equal(expectedError?.GetType(), actualError?.GetType());
            }
            var length = random.Next(Math.Min(capacity, 96) + 1) * PcmFormat.BlockAlign;
            var expectedBytes = new byte[length];
            var actualBytes = new byte[length];
            Assert.Equal(expected.Read(expectedBytes), actual.Read(actualBytes));
            Assert.Equal(expectedBytes, actualBytes);
            Assert.Equal(expected.Position, actual.Position);
            Assert.Equal(expected.LateFrames, actual.LateFrames);
            Assert.Equal(expected.ClippedSamples, actual.ClippedSamples);
        }
    }

    [Fact]
    public async Task RecordingRenderReusesItsBufferWithoutPerTickAllocations()
    {
        var factory = new RecordingEngineTests.FakeFactory();
        await using var engine = new RecordingEngine(factory.Create);
        var render = typeof(RecordingEngine).GetMethod("RenderUntil", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<long, byte[]>>(engine);
        var buffer = new byte[960 * PcmFormat.BlockAlign];
        for (var index = 0; index < 128; index++)
            render(0, buffer);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            render(0, buffer);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
        Assert.Empty(factory.Inputs);
        Assert.Null(engine.Session);
    }

    [Fact]
    public void IncrementalGroupingPreservesTextTimesSpeakerLabelsAndEverySourceIndex()
    {
        var random = new Random(5271);
        string[] text =
        [
            "", " ", "\r\n", "word", " next ", "ending.", "question?", "important!", ",", ";\r\n",
            "\u4f60", "\u597d", "\u3002", "\uff1f", "\uff01", "\uff41", "\uff0c", "\u3010", "\u3011",
            "caf\u00e9", "\ud83d\ude00", "two\nlines", new('a', 598), new('b', 599), new('c', 600), new('d', 601)
        ];
        var document = new TranscriptDocument
        {
            SpeakerNames = new() { ["spkr1"] = "Alex", ["spkr3"] = "Me" },
            Me = new("spkr3", ["Me"]),
            Segments = Enumerable.Range(0, 1500).Select(index =>
                new TranscriptSegment(index * 0.5, index * 0.5 + 0.75, "word") { SpeakerId = "spkr3" }).ToList()
        };
        double start = 800;
        for (var index = 0; index < 6000; index++)
        {
            start += random.Next(0, 8) * 0.5;
            var end = start + random.Next(0, 10) * 0.5;
            var words = text[random.Next(text.Length)];
            var speaker = random.Next(4);
            document.Segments.Add(new(start, end, words)
            {
                SpeakerId = speaker > 0 ? "spkr" + speaker : null
            });
        }
        var before = JsonSerializer.Serialize(document);
        var expected = OriginalGroups(document);
        var actual = TranscriptPresentation.Groups(document);
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Row, actual[index].Row);
            Assert.Equal(expected[index].SegmentIndices, actual[index].SegmentIndices);
        }
        Assert.Equal(expected.Select(group => group.Row), TranscriptPresentation.Rows(document));
        Assert.Equal(Enumerable.Range(0, document.Segments.Count).Where(index => document.Segments[index].Text.Trim().Length > 0),
            actual.SelectMany(group => group.SegmentIndices));
        Assert.Equal(before, JsonSerializer.Serialize(document));
    }

    [Fact]
    public void DisplayingShortFragmentsDoesNotAllocateRepeatedSegmentIndexCopies()
    {
        var document = new TranscriptDocument
        {
            Segments = Enumerable.Range(0, 6000).Select(index =>
                new TranscriptSegment(index * 0.5, (index + 1) * 0.5, "word") { SpeakerId = "spkr1" }).ToList()
        };
        _ = TranscriptPresentation.Rows(document);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var rows = TranscriptPresentation.Rows(document);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 200_000);
        Assert.Equal(TranscriptPresentation.Groups(document).Select(group => group.Row), rows);
    }

    [Fact]
    public void SpeakerReferenceMemoizationIsLocalAndDoesNotReuseOldNamesOrResolveMissingSpeakers()
    {
        var document = new TranscriptDocument
        {
            SpeakerNames = new() { ["spkr1"] = "Alex [spkr2]", ["spkr2"] = "Later speaker", ["spkr9"] = "Missing" },
            Segments = Enumerable.Range(0, 4000).Select(index =>
                new TranscriptSegment(index, index + 1, "Sentence.") { SpeakerId = index < 3999 ? "spkr1" : "spkr2" }).ToList()
        };
        var input = string.Concat(Enumerable.Repeat("[spkr1], [spkr2], [spkr9], [spkr0], [spkr01], [Spkr2], [spkr\u0661]. ", 80));
        Verify();
        document.RenameSpeaker("spkr2", "Renamed speaker");
        Verify();
        document.Segments.RemoveAll(segment => segment.SpeakerId == "spkr2");
        Verify();
        Assert.Contains("[spkr2]", TranscriptPresentation.RenderSpeakerReferences(input, document));
        Assert.DoesNotContain("Missing", TranscriptPresentation.RenderSpeakerReferences(input, document));

        void Verify()
        {
            var expected = Regex.Replace(input, @"\[spkr([1-9][0-9]*)\]", match =>
            {
                var id = "spkr" + match.Groups[1].Value;
                return document.Segments.Any(segment => segment.SpeakerId == id) ? document.SpeakerName(id) : match.Value;
            });
            Assert.Equal(expected, TranscriptPresentation.RenderSpeakerReferences(input, document));
        }
    }

    [Fact]
    public void VectorizedAsciiDetectionKeepsEveryAsciiCodeUnitAndMixedUnicode()
    {
        var ascii = new string(Enumerable.Range(0, 128).Select(value => (char)value).ToArray());
        Assert.Same(ascii, ChineseText.ToSimplified(ascii));
        Assert.Equal(ascii + "\u0080caf\u00e9 \ud83d\ude00 \u8bb0\u5f55", ChineseText.ToSimplified(ascii + "\u0080caf\u00e9 \ud83d\ude00 \u8a18\u9304"));
        Assert.Throws<ArgumentNullException>(() => ChineseText.ToSimplified(null!));
    }

    private static IReadOnlyList<TranscriptGroup> OriginalGroups(TranscriptDocument document)
    {
        var groups = new List<TranscriptGroup>();
        for (var index = 0; index < document.Segments.Count; index++)
        {
            var segment = document.Segments[index];
            var text = segment.Text.Trim();
            if (text.Length == 0)
                continue;
            if (groups.Count > 0 && groups[^1].Row is var previous &&
                previous.SpeakerId == segment.SpeakerId && segment.StartSeconds - previous.EndSeconds <= 1.5 &&
                previous.Text.Length + text.Length < 600 && !".!?\u3002\uff01\uff1f".Contains(previous.Text[^1]))
            {
                var space = !(IsCjk(previous.Text[^1]) && IsCjk(text[0])) &&
                    !"\uff0c\u3002\uff01\uff1f\u3001\uff1b\uff1a,.!?;:)\u3011\uff09".Contains(text[0]);
                groups[^1] = new(previous with
                {
                    EndSeconds = Math.Max(previous.EndSeconds, segment.EndSeconds),
                    Text = previous.Text + (space ? " " : "") + text
                }, [.. groups[^1].SegmentIndices, index]);
            }
            else
                groups.Add(new(new(segment.StartSeconds, segment.EndSeconds, segment.SpeakerId,
                    segment.SpeakerId is null ? "" : document.SpeakerName(segment.SpeakerId), text)
                {
                    MeLabel = segment.SpeakerId is not null && document.Me?.SpeakerId == segment.SpeakerId
                        ? UiText.Get("MeTag") : ""
                }, [index]));
        }
        return groups;

        static bool IsCjk(char value) => value is >= '\u2e80' and <= '\u9fff' or >= '\uff00' and <= '\uffef';
    }

    private sealed class OriginalMixer(int capacity, long position)
    {
        private readonly float[] samples = new float[capacity * PcmFormat.Channels];
        public long Position { get; private set; } = position;
        public long LateFrames { get; private set; }
        public long ClippedSamples { get; private set; }

        public void Add(long firstFrame, ReadOnlySpan<float> stereo, float gain)
        {
            var frameCount = stereo.Length / PcmFormat.Channels;
            var skipped = (int)Math.Min(frameCount, Math.Max(0, Position - firstFrame));
            LateFrames += skipped;
            firstFrame += skipped;
            stereo = stereo[(skipped * PcmFormat.Channels)..];
            if (stereo.IsEmpty)
                return;
            if (firstFrame + stereo.Length / PcmFormat.Channels > Position + capacity)
                throw new IOException("Synthetic overflow.");
            for (var index = 0; index < stereo.Length; index++)
            {
                var slot = (int)(((firstFrame * PcmFormat.Channels) + index) % samples.Length);
                samples[slot] += float.IsFinite(stereo[index]) ? stereo[index] * gain : 0;
            }
        }

        public float Read(Span<byte> pcm16)
        {
            float peak = 0;
            for (var index = 0; index < pcm16.Length / 2; index++)
            {
                var slot = (int)(((Position * PcmFormat.Channels) + index) % samples.Length);
                var value = samples[slot];
                samples[slot] = 0;
                peak = Math.Max(peak, Math.Abs(value));
                if (value is > 1 or < -1)
                    ClippedSamples++;
                var sample = (short)Math.Round(Math.Clamp(value, -1, 1) * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(pcm16[(index * 2)..], sample);
            }
            Position += pcm16.Length / PcmFormat.BlockAlign;
            return Math.Min(1, peak);
        }
    }
}
