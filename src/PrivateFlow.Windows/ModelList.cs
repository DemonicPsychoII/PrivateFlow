using System.IO;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

internal enum ModelUse { Speech, VoiceDetection, Polish, Notes }

// One downloadable model as Settings → Models lists it; its name, file, size and hash come from scripts/models.json.
internal sealed record ListedModel(string Key, ModelUse Use, string Purpose)
{
    public PinnedFile Pinned => ModelCatalogue.Model(Key);
}

internal static class ModelFiles
{
    // The path with every link on it resolved, as far as its parts exist: what a deletion would really reach.
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target) current = target.FullName;
        }
        return current;
    }
    // Why Remove must not delete this file, or null. It deletes only a regular file in the data folder's models or runtime folder, reached
    // without any link: the data folder, every folder below it on the way and the file itself are no junction or symbolic link, so a link
    // cannot lead a deletion out of Private Flow's own folders. Asked again right before deleting.
    public static string? DeletionRefusal(string file, string dataRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "It is outside Private Flow's data folder; Private Flow removes only files in its own folders.";
        var current = root;
        foreach (var part in new[] { "" }.Concat(full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)))
        {
            current = part.Length == 0 ? root : Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null))
                return $"“{info.Name}” on its path is a link to another place; Private Flow removes only files in its own folders.";
        }
        if (!File.Exists(full)) return "It is not installed.";
        var real = RealPath(full); var realRoot = RealPath(root);
        return new[] { "models", "runtime" }.Any(folder => real.StartsWith(Path.Combine(realRoot, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ? null : "It is not in Private Flow's models or runtime folder; Private Flow removes only files in its own folders.";
    }
    // A cancelled download leaves the file it was writing, up to the whole model; it is never used, and the next try starts it again.
    // Only the files of this model's setup go, by name: partial downloads of other files stay.
    public static void RemovePartialDownloads(string dataRoot, PinnedFile model, IEnumerable<PinnedFile> runtimes)
    {
        var leftovers = new[] { Path.Combine(dataRoot, "models", model.File + ".partial"), Path.Combine(dataRoot, "models", model.File + ".range") }
            .Concat(runtimes.Select(runtime => Path.Combine(dataRoot, "downloads", runtime.File + ".partial")));
        foreach (var file in leftovers)
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

// Which model each feature uses, where it runs, and whether it may be removed, from the stored or the shown settings.
internal static class ModelList
{
    public static readonly IReadOnlyList<ListedModel> All =
    [
        new(ModelCatalogue.SpeechTurbo, ModelUse.Speech, "Dictation with the GPU, and the live transcript of meetings"),
        new(ModelCatalogue.SpeechMedium, ModelUse.Speech, "Dictation; recognizes names better than base, slow without the GPU"),
        new(ModelCatalogue.SpeechBase, ModelUse.Speech, "Dictation on the processor"),
        new(ModelCatalogue.SpeechFinalPass, ModelUse.Speech, "Final pass of meetings and files with the GPU"),
        new(ModelCatalogue.VoiceDetection, ModelUse.VoiceDetection, "Voice detection: skips silence before recognition"),
        new(ModelCatalogue.PolishModel, ModelUse.Polish, "Local polish"),
        new(ModelCatalogue.NotesModel, ModelUse.Notes, "Meeting notes, and Local polish when chosen below"),
    ];
    public static ListedModel Find(string key) => All.Single(model => model.Key == key);

    // Where Private Flow's own setup puts the model; Remove deletes only this file.
    public static string OwnFile(ListedModel model) => Path.Combine(Settings.Root, "models", model.Pinned.File);
    // The file the feature reads: the setting for the polish and notes models; for a speech model its file in the models folder, or a
    // speech path of the settings that names its file elsewhere, such as one entered by hand.
    public static string ModelPath(Settings settings, ListedModel model) => model.Use switch
    {
        ModelUse.Polish => settings.LocalRewriteModelPath,
        ModelUse.Notes => settings.MeetingNotesModelPath,
        _ => File.Exists(OwnFile(model)) ? OwnFile(model)
            : new[] { settings.ModelPath, settings.MeetingModelPath, settings.VadModelPath }.FirstOrDefault(path => Path.GetFileName(path).Equals(model.Pinned.File, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
              ?? OwnFile(model)
    };
    public static bool IsInstalled(Settings settings, ListedModel model) => model.Use switch
    {
        ModelUse.Polish => File.Exists(settings.LocalRewriteEnginePath) && File.Exists(settings.LocalRewriteModelPath),
        ModelUse.Notes => MeetingNotesModel.IsInstalled(settings),
        _ => File.Exists(ModelPath(settings, model))
    };
    // Two paths name the same file once the links on them are resolved.
    private static bool SamePath(string first, string second) =>
        first.Length > 0 && second.Length > 0 && string.Equals(ModelFiles.RealPath(first), ModelFiles.RealPath(second), StringComparison.OrdinalIgnoreCase);
    // The llama.cpp archives the setup of a language model fetches with it. A speech model comes alone: Apply fetches the engine.
    public static IReadOnlyList<PinnedFile> Runtimes(ListedModel model, bool nvidiaGpu) => model.Use switch
    {
        ModelUse.Polish => ModelCatalogue.LlamaRuntimes(false, nvidiaGpu),
        ModelUse.Notes => ModelCatalogue.LlamaRuntimes(true, nvidiaGpu),
        _ => []
    };
    public static long DownloadBytes(ListedModel model, bool nvidiaGpu) => model.Pinned.Bytes + Runtimes(model, nvidiaGpu).Sum(runtime => runtime.Bytes);
    // A cancelled install leaves the download it was writing, up to the whole model; it is never used, and the next try starts it again.
    // Only the files that install fetches go, by name: partial downloads of other files stay. A partial llama.cpp archive of the same name
    // left by the other language model's setup goes too; either setup would have continued that same file.
    public static void RemovePartialDownloads(string dataRoot, ListedModel model, bool nvidiaGpu) =>
        ModelFiles.RemovePartialDownloads(dataRoot, model.Pinned, Runtimes(model, nvidiaGpu));
    public static string Size(long bytes) => bytes == 0 ? "0 MB" : bytes < 1_000_000_000 ? $"{Math.Max(1, Math.Round(bytes / 1e6)):0} MB" : SpeechRuntime.Gigabytes(bytes) + " GB";

    public static bool UsesLocalPolish(Settings settings) =>
        settings.OutputStyle == OutputStyle.LocalPolish || settings.Profiles.Any(profile => profile.OutputStyle == OutputStyle.LocalPolish);
    // The model Local polish runs with these settings.
    public static ListedModel PolishModel(Settings settings) =>
        Find(settings.LocalPolishModel == LocalPolishModel.MeetingNotesModel ? ModelCatalogue.NotesModel : ModelCatalogue.PolishModel);
    // The model a chosen feature lacks, or null: Local polish chosen as text style, and the Local model notes style.
    public static ListedModel? MissingForPolish(Settings settings) =>
        settings.OutputStyle == OutputStyle.LocalPolish && PolishModel(settings) is var model && !IsInstalled(settings, model) ? model : null;
    public static ListedModel? MissingForNotes(Settings settings) =>
        settings.MeetingNotesStyle == MeetingNotesStyle.LocalModel && !MeetingNotesModel.IsInstalled(settings) ? Find(ModelCatalogue.NotesModel) : null;

    // A llama.cpp build with the CUDA back end beside llama-server.exe offloads to the GPU; the processor build cannot.
    public static bool IsCudaBuild(string enginePath) => Path.GetDirectoryName(enginePath) is { } folder && File.Exists(Path.Combine(folder, "ggml-cuda.dll"));
    // Where the model runs with these settings: the speech engine build in use, the layers the polish and notes servers are started with.
    public static string RunsOn(Settings settings, ListedModel model, bool nvidiaGpu) => model.Use switch
    {
        ModelUse.Speech when !File.Exists(settings.EnginePath) => "Runs on: GPU or processor, as Apply under Speech recognition sets up the speech engine",
        ModelUse.Speech when SpeechSetup.Processor(settings.EnginePath) == "GPU" =>
            $"Runs on: GPU (the CUDA build of the speech engine is in use), about {SpeechRuntime.Gigabytes(SpeechRuntime.GpuMemoryNeeded(model.Pinned.Bytes))} GB of GPU memory",
        ModelUse.Speech => "Runs on: processor (the processor build of the speech engine is in use)",
        ModelUse.VoiceDetection => "Runs on: processor, always",
        ModelUse.Polish => LocalRewriter.PolishModelGpuLayers == 0 ? "Runs on: processor (llama.cpp processor build, no GPU layers)" : $"Runs on: GPU, {LocalRewriter.PolishModelGpuLayers} layers",
        _ => NotesRunsOn(settings, nvidiaGpu)
    };
    private static string NotesRunsOn(Settings settings, bool nvidiaGpu)
    {
        var cuda = File.Exists(settings.MeetingNotesEnginePath) ? IsCudaBuild(settings.MeetingNotesEnginePath) : nvidiaGpu;
        if (!cuda) return "Runs on: processor (llama.cpp processor build: no NVIDIA GPU)";
        return settings.MeetingNotesGpuLayers switch
        {
            < 0 => "Runs on: GPU, as many layers as fit (about 3.9 GB of GPU memory for notes, 3.1 GB for Local polish)",
            0 => "Runs on: processor, as chosen below (llama.cpp CUDA build without GPU layers)",
            var layers => $"Runs on: GPU, {layers} layers (set in settings.json)"
        };
    }

    // What needs the model now, or null when nothing does.
    // A model is needed while any feature in use reads its file, whichever row the setting belongs to: a model path entered by hand can name
    // another row's file. Decided by the file's real path.
    public static string? NeededBy(Settings settings, ListedModel model)
    {
        var files = new[] { OwnFile(model), ModelPath(settings, model) };
        return FilesInUse(settings).FirstOrDefault(use => files.Any(file => SamePath(use.Path, file))).Reason;
    }
    // What each feature reads when it runs. The speech engine, the dictation model (also the live preview of meetings and the final pass's
    // fallback), the final-pass model and the voice detector are read whatever the toggles say: every meeting final pass and file
    // transcription finds speech with the voice detector even with speech detection off for dictation.
    private static IEnumerable<(string Path, string Reason)> FilesInUse(Settings settings)
    {
        yield return (settings.ModelPath, "Dictation uses it. Choose another model under Speech recognition and press Apply first.");
        yield return (settings.EnginePath, "Speech recognition uses it.");
        yield return (settings.MeetingModelPath, "The final pass of meetings and files uses it. Apply another model under Speech recognition first.");
        yield return (settings.VadModelPath, "The final pass of meetings and file transcription use it to find speech, also with speech detection off.");
        var polish = UsesLocalPolish(settings);
        if (polish && settings.LocalPolishModel == LocalPolishModel.PolishModel)
            foreach (var path in new[] { settings.LocalRewriteModelPath, settings.LocalRewriteEnginePath })
                yield return (path, "Local polish uses it. Choose another text style, or the meeting notes model for Local polish, first.");
        if (settings.MeetingNotesStyle == MeetingNotesStyle.LocalModel)
            foreach (var path in new[] { settings.MeetingNotesModelPath, settings.MeetingNotesEnginePath })
                yield return (path, "Meeting notes use it. Choose rules or the API for meeting notes first.");
        if (polish && settings.LocalPolishModel == LocalPolishModel.MeetingNotesModel)
            foreach (var path in new[] { settings.MeetingNotesModelPath, settings.MeetingNotesEnginePath })
                yield return (path, "Local polish uses it. Choose the Local polish model for Local polish, or another text style, first.");
    }
    // The recommendation for this PC: the transcription setup is exactly what setup-engine.ps1 with Automatic installs (its -Recommend
    // answer); the local processing model is the meeting notes model for notes and Local polish, as measured on 2026-11-17, unless the PC
    // lacks the memory or the disk space for it. Missing lists what is not installed yet; the sizes are what Install would download.
    internal sealed record Recommendation(SpeechRecommendation Speech, IReadOnlyList<ListedModel> Transcription, IReadOnlyList<ListedModel> TranscriptionMissing, long TranscriptionBytes,
        IReadOnlyList<ListedModel> Local, IReadOnlyList<ListedModel> LocalMissing, long LocalBytes, string? LocalNote, string? DiskNote)
    {
        public bool TranscriptionComplete => TranscriptionBytes == 0;
        public bool LocalComplete => LocalBytes == 0;
        public long TotalBytes => TranscriptionBytes + LocalBytes;
    }
    // Measured on 2026-11-17: llama-server with the meeting notes model on the processor took 5.6 GB of memory.
    public const long LocalProcessingMemory = 8L * 1024 * 1024 * 1024;
    // Unpacked runtimes and the cached archives need room beside the downloads.
    public const double DiskMargin = 1.25;
    public static Recommendation Recommend(Settings settings, SpeechRecommendation speech, PcFacts facts)
    {
        var transcription = new[] { speech.Model, speech.MeetingModel, speech.VoiceDetection }.Where(key => key.Length > 0).Select(Find).ToList();
        var transcriptionMissing = transcription.Where(model => !IsInstalled(settings, model)).ToList();
        var transcriptionBytes = transcriptionMissing.Sum(model => model.Pinned.Bytes) + (speech.EngineInstalled ? 0 : ModelCatalogue.Runtime("whisper-" + speech.Backend).Bytes);
        var notesModel = Find(ModelCatalogue.NotesModel);
        string? localNote = null, diskNote = null;
        List<ListedModel> local = [notesModel];
        if (facts.Memory > 0 && facts.Memory < LocalProcessingMemory)
        {
            local = [];
            localNote = $"None: this PC has {SpeechRuntime.Gigabytes(facts.Memory)} GB of memory, and the meeting notes model took 5.6 GB while it ran. Notes stay rule-based, and Local polish falls back to Clean.";
        }
        else if (!facts.Nvidia)
            localNote = "Without an NVIDIA GPU it runs on the processor: a 50-minute test meeting took 166 s instead of 28 s, Local polish 2.5 s instead of 0.4 s.";
        else if (facts.GpuTotal is { } total && total < 4_500_000_000)
            localNote = $"The GPU's {SpeechRuntime.Gigabytes(total)} GB holds only part of it beside the speech model; llama.cpp runs the rest on the processor, which is slower.";
        var localMissing = local.Where(model => !IsInstalled(settings, model)).ToList();
        var localBytes = localMissing.Sum(model => DownloadBytes(model, facts.Nvidia));
        if (facts.DiskFree > 0 && (transcriptionBytes + localBytes) * DiskMargin > facts.DiskFree)
        {
            if (localBytes > 0 && transcriptionBytes * DiskMargin <= facts.DiskFree)
            {
                diskNote = $"Only {Size(facts.DiskFree)} is free on {facts.Drive}: enough for the transcription models, not for the local processing model as well.";
                local = []; localMissing = []; localBytes = 0;
            }
            else diskNote = $"Only {Size(facts.DiskFree)} is free on {facts.Drive}; the recommended transcription setup needs about {Size((long)(transcriptionBytes * DiskMargin))}. Free some space first.";
        }
        return new(speech, transcription, transcriptionMissing, transcriptionBytes, local, localMissing, localBytes, localNote, diskNote);
    }

    // Why Remove does not remove the model, or null when it may. running names an operation that is running, such as a dictation.
    public static string? RemoveRefusal(Settings settings, ListedModel model, string? running)
    {
        if (!IsInstalled(settings, model)) return "It is not installed.";
        if (running is not null) return $"{running} is running. Remove the model once it has finished.";
        if (NeededBy(settings, model) is { } needed) return needed;
        if (!SamePath(ModelPath(settings, model), OwnFile(model)))
            return "Its path was entered by hand; Private Flow removes only the models it installed.";
        if (ModelFiles.DeletionRefusal(OwnFile(model), Settings.Root) is { } unsafeFile) return unsafeFile;
        return null;
    }
}
