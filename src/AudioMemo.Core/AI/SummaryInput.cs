using System.Security.Cryptography;
using System.Text.Json;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.AI;

public static class SummaryInput
{
    public static string Fingerprint(TranscriptDocument transcript, int? segmentCount = null)
    {
        var count = segmentCount ?? transcript.Segments.Count;
        if (count < 0 || count > transcript.Segments.Count)
            throw new InvalidDataException("Invalid summary source segment count.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            transcript.Revision,
            Segments = transcript.Segments.Take(count).Select(segment => new
            {
                segment.StartSeconds,
                segment.EndSeconds,
                Text = segment.Text.Trim(),
                segment.SpeakerId,
                SpeakerName = segment.SpeakerId is null ? null : transcript.SpeakerName(segment.SpeakerId)
            })
        });
        return "sha256-v1:" + Convert.ToHexString(SHA256.HashData(bytes));
    }
}
