using System.Text.Json;

namespace PrivateFlow.Core;

// Which model Local polish runs: its own model (Qwen3-4B) on the processor, or the meeting notes model (Qwen3.5-4B).
public enum LocalPolishModel { PolishModel, MeetingNotesModel }

// A pinned download: a model or the runtime that runs it.
public sealed record PinnedFile(string Key, string Name, string File, long Bytes, string Sha256, string Url);

// The names, sources, sizes and hashes of every download, from scripts/models.json, which the setup scripts read as well; built into
// this assembly so the app and the scripts it ships with cannot disagree.
public static class ModelCatalogue
{
    private sealed record Catalogue(List<PinnedFile> Models, List<PinnedFile> Runtimes);
    private static readonly Catalogue Current = Parse(ReadEmbedded());
    public static IReadOnlyList<PinnedFile> Models => Current.Models;
    public static IReadOnlyList<PinnedFile> Runtimes => Current.Runtimes;
    public static PinnedFile Model(string key) => Current.Models.Single(model => model.Key == key);
    public static PinnedFile Runtime(string key) => Current.Runtimes.Single(runtime => runtime.Key == key);
    public const string SpeechTurbo = "large-v3-turbo-q8_0", SpeechMedium = "medium", SpeechBase = "base", SpeechFinalPass = "large-v3", VoiceDetection = "silero-v6.2.0",
        PolishModel = "qwen3-4b", NotesModel = "qwen3.5-4b";
    // The llama.cpp archives a setup of the polish or the notes model fetches: the notes model gets the CUDA build on a PC with an NVIDIA GPU.
    public static IReadOnlyList<PinnedFile> LlamaRuntimes(bool notesModel, bool nvidiaGpu) =>
        notesModel && nvidiaGpu ? [Runtime("llama-cuda12"), Runtime("llama-cudart12")] : [Runtime("llama-cpu")];

    private static Catalogue Parse(string json) =>
        JsonSerializer.Deserialize<Catalogue>(json) ?? throw new InvalidDataException("models.json is empty.");
    private static string ReadEmbedded()
    {
        using var stream = typeof(ModelCatalogue).Assembly.GetManifestResourceStream("models.json") ?? throw new InvalidDataException("models.json is not built in.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
