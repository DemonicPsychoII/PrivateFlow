using System.Text.Json;

namespace PrivateFlow.Core;

// What the recommendation for this PC rests on, read on this PC. GpuTotal and GpuFree are known only for an NVIDIA GPU.
public sealed record PcFacts(string Graphics, bool Nvidia, long? GpuTotal, long? GpuFree, string Processor, int LogicalProcessors, long Memory, string Drive, long DiskFree);

// What setup-engine.ps1 with Automatic installs here, as its -Recommend answer says: the engine build, the dictation model, the final-pass
// model (empty for none), the voice detector, and whether that engine build is already in place.
public sealed record SpeechRecommendation(string Backend, string Model, string MeetingModel, string VoiceDetection, bool EngineInstalled)
{
    // The last line the script printed that reads as its answer; null when there is none.
    public static SpeechRecommendation? FromOutput(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try { return JsonSerializer.Deserialize<SpeechRecommendation>(line) is { Backend.Length: > 0, Model.Length: > 0 } answer ? answer : null; }
            catch (JsonException) { }
        }
        return null;
    }
}
