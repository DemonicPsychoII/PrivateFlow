using PrivateFlow.Core;
using static Harness;

static class ModelSetupChecks
{
    public static void VerifySetupProgress()
    {
        {
            var setup = new SetupProgress();
            var shown = new List<string>();
            foreach (var line in new[] { "Downloading llama-b11577-bin-win-cuda-12.4-x64.zip...", "Downloading Qwen3.5-4B-Q4_K_M.gguf...", "Local model download: 1%", "Local model download: 42%",
                "Local model download: 30%", "  ", "Meeting notes model ready. Choose the local model for meeting notes; setup does not change the notes style." })
            { setup.Report(line); shown.Add(setup.Text); }
            Check(shown.SequenceEqual(["Downloading llama-b11577-bin-win-cuda-12.4-x64.zip · 0%", "Downloading Qwen3.5-4B-Q4_K_M.gguf · 0%", "Downloading Qwen3.5-4B-Q4_K_M.gguf · 1%",
                "Downloading Qwen3.5-4B-Q4_K_M.gguf · 42%", "Downloading Qwen3.5-4B-Q4_K_M.gguf · 42%", "Downloading Qwen3.5-4B-Q4_K_M.gguf · 42%",
                "Meeting notes model ready. Choose the local model for meeting notes; setup does not change the notes style · 42%"]) && setup.Share == .42,
                $"setup progress names the step the script printed, takes the share from its percentages, never falls and holds it through steps without one ({string.Join(" | ", shown)})");
        }
    }

    public static void VerifyModels()
    {
        // Settings → Models in its own data folder: the catalogue, the rules of what each feature uses, and the recommendation for a PC.
        var oldRoot = Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR");
        var testRoot = Path.Combine(Path.GetTempPath(), "PrivateFlow-models-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", testRoot);
        try
        {
            Directory.CreateDirectory(testRoot);
            var file = Path.Combine(testRoot, "settings.json");
            {
                // Settings → Models. scripts/models.json is the one source of every pinned download: built into the app, read by the setup
                // scripts, which hold no source or hash of their own, and copied beside them into the package.
                var checkout = new DirectoryInfo(AppContext.BaseDirectory);
                while (checkout is not null && !File.Exists(Path.Combine(checkout.FullName, "scripts", "models.json"))) checkout = checkout.Parent;
                if (checkout is null) throw new Exception("scripts/models.json was not found above " + AppContext.BaseDirectory);
                string Script(string name) => File.ReadAllText(Path.Combine(checkout.FullName, "scripts", name));
                var onDisk = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<PinnedFile>>>(Script("models.json"))!;
                Check(onDisk["Models"].SequenceEqual(ModelCatalogue.Models) && onDisk["Runtimes"].SequenceEqual(ModelCatalogue.Runtimes),
                    "the catalogue built into the app is scripts/models.json, entry for entry");
                var everyFile = ModelCatalogue.Models.Concat(ModelCatalogue.Runtimes).ToList();
                var unpinned = everyFile.Where(entry => !System.Text.RegularExpressions.Regex.IsMatch(entry.Sha256, "^[0-9a-f]{64}$")
                    || !(System.Text.RegularExpressions.Regex.IsMatch(entry.Url, @"^https://huggingface\.co/[\w.-]+/[\w.-]+/resolve/[0-9a-f]{40}/" + System.Text.RegularExpressions.Regex.Escape(entry.File) + @"(\?download=true)?$")
                        || System.Text.RegularExpressions.Regex.IsMatch(entry.Url, @"^https://github\.com/ggml-org/(whisper|llama)\.cpp/releases/download/(v\d+\.\d+\.\d+|b\d+)/" + System.Text.RegularExpressions.Regex.Escape(entry.File) + "$"))).Select(entry => entry.Key).ToList();
                Check(unpinned.Count == 0 && everyFile.Select(entry => entry.Key).Distinct().Count() == everyFile.Count && everyFile.All(entry => entry.Bytes > 0),
                    $"every catalogue entry has a SHA-256 and a source pinned to a commit or a release tag that names its file, a unique key and its size ({string.Join(", ", unpinned)})");
                var scripts = new[] { "setup-engine.ps1", "setup-rewrite.ps1", "install.ps1" }.ToDictionary(name => name, Script);
                // The public app updater has its own release URL; inference runtime and model pins must still come only from the catalogue.
                var ownPins = scripts.Where(script => System.Text.RegularExpressions.Regex.IsMatch(script.Value, @"[0-9a-fA-F]{64}|/resolve/|(?<!DemonicPsychoII/PrivateFlow)/releases/download/")).Select(script => script.Key).ToList();
                var referenced = scripts.Values.SelectMany(text => System.Text.RegularExpressions.Regex.Matches(text, @"Get-Pinned '([^']+)'|-Download '([^']+)'").Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value))
                    .Concat(System.Text.RegularExpressions.Regex.Matches(scripts["setup-engine.ps1"], @"\[ValidateSet\(([^)]*)\)\]\[string\]\$(?:Model|Download)").SelectMany(match => match.Groups[1].Value.Split(',').Select(value => value.Trim('\'', ' '))))
                    .Concat(["whisper-cpu", "whisper-cuda12"]).Where(key => key is not ("auto" or "none")).Distinct().ToList();
                var unknown = referenced.Where(key => everyFile.All(entry => entry.Key != key)).ToList();
                Check(ownPins.Count == 0 && unknown.Count == 0 && referenced.Count >= 11 && scripts["setup-engine.ps1"].Contains("Get-Pinned \"whisper-$Backend\""),
                    $"the setup scripts and the installer hold no source or hash of their own and name only catalogue entries ({referenced.Count} keys; own pins in: {string.Join(", ", ownPins)}; unknown: {string.Join(", ", unknown)})");
                Check(Script("build.ps1").Contains("scripts/models.json") && Script("build.sh").Contains("'models.json'"), "both package builds copy models.json beside the setup scripts");
                Check(PrivateFlow.Windows.ModelList.All.Select(model => model.Key).Order().SequenceEqual(ModelCatalogue.Models.Select(model => model.Key).Order()),
                    "Settings → Models lists every model of the catalogue once");
                // UI Automation drops an underscore from a button's name as an access-key marker, so "Install Qwen3-4B (Q4_K_M)" reached screen
                // readers as "(Q4KM)"; the names the buttons carry have none.
                Check(everyFile.All(entry => !entry.Name.Contains('_')), "no catalogue name has an underscore, which screen readers would not hear in the Install and Remove buttons");
                Check(PrivateFlow.Windows.MeetingNotesModel.FileName == "Qwen3.5-4B-Q4_K_M.gguf" && new PrivateFlow.Windows.Settings().LocalRewriteModelPath.EndsWith("Qwen3-4B-Q4_K_M.gguf")
                    && PrivateFlow.Windows.MeetingNotesModel.DownloadBytes == ModelCatalogue.Model(ModelCatalogue.NotesModel).Bytes + ModelCatalogue.LlamaRuntimes(true, PrivateFlow.Windows.SpeechSetup.HasNvidiaGpu).Sum(runtime => runtime.Bytes)
                    && PrivateFlow.Windows.ModelList.DownloadBytes(PrivateFlow.Windows.ModelList.Find(ModelCatalogue.NotesModel), true) == 2_740_937_888 + 265_226_400 + 391_443_627
                    && PrivateFlow.Windows.ModelList.DownloadBytes(PrivateFlow.Windows.ModelList.Find(ModelCatalogue.PolishModel), true) == 2_497_280_256 + 19_509_426
                    && PrivateFlow.Windows.ModelList.DownloadBytes(PrivateFlow.Windows.ModelList.Find(ModelCatalogue.SpeechTurbo), true) == 874_188_075
                    && PrivateFlow.Windows.ModelList.Size(874_188_075) == "874 MB" && PrivateFlow.Windows.ModelList.Size(885_098) == "1 MB" && PrivateFlow.Windows.ModelList.Size(3_095_033_483) == "3.1 GB",
                    "names and sizes the app shows come from the catalogue: a language model's download adds the llama.cpp archives its setup fetches, the CUDA build with its runtime for the notes model on an NVIDIA PC");

                // What each feature uses, where it runs and whether Remove may delete a model, with stand-in files in the test data folder.
                PrivateFlow.Windows.ListedModel Listed(string key) => PrivateFlow.Windows.ModelList.Find(key);
                string Touch(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); return path; }
                var models = new PrivateFlow.Windows.Settings
                {
                    EnginePath = Touch(Path.Combine(testRoot, "runtime", "cuda12", "Release", "whisper-cli.exe")), ModelPath = Touch(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechTurbo))),
                    MeetingModelPath = Touch(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechFinalPass))), VadModelPath = Touch(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.VoiceDetection))),
                    LocalRewriteEnginePath = Path.Combine(testRoot, "runtime", "rewrite", "llama-server.exe"), LocalRewriteModelPath = PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.PolishModel)),
                    MeetingNotesEnginePath = Path.Combine(testRoot, "runtime", "notes", "llama-server.exe"), MeetingNotesModelPath = PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.NotesModel)),
                };
                Touch(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechBase)));
                string? Refusal(string key, string? running = null) => PrivateFlow.Windows.ModelList.RemoveRefusal(models, Listed(key), running);
                Check(PrivateFlow.Windows.ModelList.IsInstalled(models, Listed(ModelCatalogue.SpeechTurbo)) && !PrivateFlow.Windows.ModelList.IsInstalled(models, Listed(ModelCatalogue.SpeechMedium))
                    && Refusal(ModelCatalogue.SpeechTurbo)!.StartsWith("Dictation uses it.") && Refusal(ModelCatalogue.SpeechFinalPass)!.StartsWith("The final pass of meetings and files uses it.")
                    && Refusal(ModelCatalogue.VoiceDetection)!.StartsWith("The final pass of meetings and file transcription use it") && Refusal(ModelCatalogue.SpeechMedium) == "It is not installed."
                    && Refusal(ModelCatalogue.SpeechBase) is null && Refusal(ModelCatalogue.SpeechBase, "A meeting") == "A meeting is running. Remove the model once it has finished.",
                    "Remove refuses the speech model dictation uses, the final-pass model and the speech detection model in use, each with the reason, and any model while an operation runs; a model nothing uses may go");
                models.SpeechDetection = false;
                Check(Refusal(ModelCatalogue.VoiceDetection)!.StartsWith("The final pass of meetings and file transcription use it to find speech, also with speech detection off"),
                    "with speech detection off for dictation, the voice detector stays needed, as every meeting final pass and file transcription reads it");
                Check(PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.SpeechTurbo), true) == "Runs on: GPU (the CUDA build of the speech engine is in use), about 1.3 GB of GPU memory"
                    && PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.SpeechBase), true).StartsWith("Runs on: GPU")
                    && PrivateFlow.Windows.ModelList.RunsOn(new PrivateFlow.Windows.Settings { EnginePath = Touch(Path.Combine(testRoot, "runtime", "cpu", "whisper-cli.exe")) }, Listed(ModelCatalogue.SpeechBase), true) == "Runs on: processor (the processor build of the speech engine is in use)"
                    && PrivateFlow.Windows.ModelList.RunsOn(new PrivateFlow.Windows.Settings { EnginePath = Path.Combine(testRoot, "missing", "whisper-cli.exe") }, Listed(ModelCatalogue.SpeechBase), false).Contains("as Apply under Speech recognition sets up")
                    && PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.PolishModel), true) == "Runs on: processor (llama.cpp processor build, no GPU layers)",
                    "a speech model runs where the speech engine build in use runs, with the GPU memory it needs; the Local polish model runs on the processor as its server is started");
                Check(PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.NotesModel), true).StartsWith("Runs on: GPU, as many layers as fit")
                    && PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.NotesModel), false) == "Runs on: processor (llama.cpp processor build: no NVIDIA GPU)",
                    "the meeting notes model not installed yet runs on the GPU where setup would install the CUDA build, otherwise on the processor");
                Touch(models.MeetingNotesEnginePath); Touch(models.MeetingNotesModelPath);
                var notesProcessorBuild = PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.NotesModel), true);
                Touch(Path.Combine(testRoot, "runtime", "notes", "ggml-cuda.dll"));
                var notesAutomatic = PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.NotesModel), true);
                models.MeetingNotesGpuLayers = 0; var notesProcessor = PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.NotesModel), true);
                models.MeetingNotesGpuLayers = 20; var notesLayers = PrivateFlow.Windows.ModelList.RunsOn(models, Listed(ModelCatalogue.NotesModel), true);
                models.MeetingNotesGpuLayers = -1;
                Check(notesProcessorBuild.Contains("processor build") && notesAutomatic.StartsWith("Runs on: GPU, as many layers as fit") && notesProcessor.StartsWith("Runs on: processor, as chosen below")
                    && notesLayers == "Runs on: GPU, 20 layers (set in settings.json)",
                    "the installed meeting notes model runs where its llama.cpp build and the stored layers put it: the processor build on the processor, the CUDA build on the GPU unless the processor is chosen");
                // Local polish: the model chosen for it is needed while Local polish is the text style or a profile's style, and missing while not installed.
                models.OutputStyle = OutputStyle.LocalPolish;
                var polishMissing = PrivateFlow.Windows.ModelList.MissingForPolish(models)?.Key;
                Touch(models.LocalRewriteEnginePath); Touch(models.LocalRewriteModelPath);
                var polishNeeded = Refusal(ModelCatalogue.PolishModel);
                models.LocalPolishModel = LocalPolishModel.MeetingNotesModel;
                var notesForPolish = (Refusal(ModelCatalogue.NotesModel), Refusal(ModelCatalogue.PolishModel), PrivateFlow.Windows.ModelList.PolishModel(models).Key, PrivateFlow.Windows.ModelList.MissingForPolish(models));
                models.OutputStyle = OutputStyle.Clean; models.LocalPolishModel = LocalPolishModel.PolishModel;
                models.Profiles = [new() { Name = "Mail", OutputStyle = OutputStyle.LocalPolish }];
                var neededByProfile = Refusal(ModelCatalogue.PolishModel);
                models.Profiles = [];
                Check(polishMissing == ModelCatalogue.PolishModel && polishNeeded!.StartsWith("Local polish uses it.") && notesForPolish.Item1!.StartsWith("Local polish uses it.") && notesForPolish.Item2 is null
                    && notesForPolish.Item3 == ModelCatalogue.NotesModel && notesForPolish.Item4 is null && neededByProfile!.StartsWith("Local polish uses it.") && Refusal(ModelCatalogue.PolishModel) is null,
                    "Local polish needs the model chosen for it, also through a profile; chosen without its files it is missing, and the other one may be removed");
                models.MeetingNotesStyle = MeetingNotesStyle.LocalModel;
                var notesNeeded = Refusal(ModelCatalogue.NotesModel);
                File.Delete(models.MeetingNotesModelPath);
                var notesMissing = PrivateFlow.Windows.ModelList.MissingForNotes(models)?.Key;
                models.MeetingNotesStyle = MeetingNotesStyle.Api;
                Check(notesNeeded!.StartsWith("Meeting notes use it.") && notesMissing == ModelCatalogue.NotesModel && PrivateFlow.Windows.ModelList.MissingForNotes(models) is null,
                    "the Local model notes style needs the meeting notes model and lacks it once its file is gone; the API style needs no model");
                models.LocalRewriteModelPath = Touch(Path.Combine(testRoot, "elsewhere", "Qwen3-4B-Q4_K_M.gguf"));
                var handMedium = new PrivateFlow.Windows.Settings { ModelPath = Touch(Path.Combine(testRoot, "elsewhere", "ggml-medium.bin")), SpeechDetection = false };
                var handInstalled = PrivateFlow.Windows.ModelList.IsInstalled(handMedium, Listed(ModelCatalogue.SpeechMedium));
                var handNeeded = PrivateFlow.Windows.ModelList.RemoveRefusal(handMedium, Listed(ModelCatalogue.SpeechMedium), null);
                handMedium.ModelPath = PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechBase)); handMedium.MeetingModelPath = Path.Combine(testRoot, "elsewhere", "ggml-medium.bin");
                var handFinalPass = PrivateFlow.Windows.ModelList.RemoveRefusal(handMedium, Listed(ModelCatalogue.SpeechMedium), null);
                Check(Refusal(ModelCatalogue.PolishModel) == "Its path was entered by hand; Private Flow removes only the models it installed." && handInstalled && handNeeded!.StartsWith("Dictation uses it.")
                    && handFinalPass!.StartsWith("The final pass of meetings and files uses it."),
                    "a model whose path was entered by hand outside the models folder counts as installed and needed by what uses it, and Remove never deletes it");
                // The recommendation for this PC: the transcription part is the setup script's -Recommend answer as it stands, the local part the
                // meeting notes model unless memory or disk space rule it out; sizes are what would be downloaded.
                var gpuAnswer = SpeechRecommendation.FromOutput(["Downloading nothing", "{\"Backend\":\"cuda12\",\"Model\":\"large-v3-turbo-q8_0\",\"MeetingModel\":\"large-v3\",\"VoiceDetection\":\"silero-v6.2.0\",\"EngineInstalled\":false}"])!;
                Check(gpuAnswer == new SpeechRecommendation("cuda12", ModelCatalogue.SpeechTurbo, ModelCatalogue.SpeechFinalPass, ModelCatalogue.VoiceDetection, false)
                    && SpeechRecommendation.FromOutput(["Setup failed"]) is null && SpeechRecommendation.FromOutput(["{\"Backend\":\"\"}"]) is null,
                    "the app reads the setup script's -Recommend answer from its last JSON line and nothing else");
                var bare = new PrivateFlow.Windows.Settings { MeetingNotesEnginePath = Path.Combine(testRoot, "none", "llama-server.exe"), MeetingNotesModelPath = Path.Combine(testRoot, "none", "notes.gguf"),
                    ModelPath = Path.Combine(testRoot, "none", "ggml-base.bin"), MeetingModelPath = "", VadModelPath = Path.Combine(testRoot, "none", "ggml-silero-v6.2.0.bin") };
                foreach (var key in new[] { ModelCatalogue.SpeechTurbo, ModelCatalogue.SpeechFinalPass, ModelCatalogue.VoiceDetection, ModelCatalogue.SpeechBase }) File.Delete(PrivateFlow.Windows.ModelList.OwnFile(Listed(key)));
                PcFacts Pc(bool nvidia = true, long memory = 32L << 30, long disk = 500_000_000_000, long? gpu = 8L << 30) => new("Test graphics", nvidia, nvidia ? gpu : null, nvidia ? gpu : null, "Test processor", 16, memory, "C:", disk);
                var full = PrivateFlow.Windows.ModelList.Recommend(bare, gpuAnswer, Pc());
                var notesDownload = PrivateFlow.Windows.ModelList.DownloadBytes(Listed(ModelCatalogue.NotesModel), true);
                Check(full.Transcription.Select(model => model.Key).SequenceEqual([ModelCatalogue.SpeechTurbo, ModelCatalogue.SpeechFinalPass, ModelCatalogue.VoiceDetection])
                    && full.TranscriptionBytes == 670_611_449L + 874_188_075 + 3_095_033_483 + 885_098 && full.Local.Single().Key == ModelCatalogue.NotesModel && full.LocalBytes == notesDownload
                    && full.LocalNote is null && full.DiskNote is null && full.TotalBytes == full.TranscriptionBytes + full.LocalBytes,
                    "on a PC with an NVIDIA GPU, nothing installed: the CUDA engine with turbo, large-v3 and Silero, and the meeting notes model for notes and Local polish, each with its download size");
                Touch(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechTurbo)));
                var partlyInstalled = PrivateFlow.Windows.ModelList.Recommend(bare, gpuAnswer with { EngineInstalled = true }, Pc());
                File.Delete(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechTurbo)));
                var cpuAnswer = new SpeechRecommendation("cpu", ModelCatalogue.SpeechBase, "", ModelCatalogue.VoiceDetection, false);
                var withoutGpu = PrivateFlow.Windows.ModelList.Recommend(bare, cpuAnswer, Pc(nvidia: false));
                var smallMemory = PrivateFlow.Windows.ModelList.Recommend(bare, gpuAnswer, Pc(memory: 6L << 30));
                var smallGpu = PrivateFlow.Windows.ModelList.Recommend(bare, gpuAnswer, Pc(gpu: 4L << 30));
                var smallDisk = PrivateFlow.Windows.ModelList.Recommend(bare, gpuAnswer, Pc(disk: 7_000_000_000));
                var tinyDisk = PrivateFlow.Windows.ModelList.Recommend(bare, gpuAnswer, Pc(disk: 1_000_000_000));
                Check(partlyInstalled.TranscriptionMissing.Select(model => model.Key).SequenceEqual([ModelCatalogue.SpeechFinalPass, ModelCatalogue.VoiceDetection]) && partlyInstalled.TranscriptionBytes == 3_095_033_483L + 885_098
                    && withoutGpu.Transcription.Select(model => model.Key).SequenceEqual([ModelCatalogue.SpeechBase, ModelCatalogue.VoiceDetection]) && withoutGpu.TranscriptionBytes == 8_194_445 + 147_951_465 + 885_098
                    && withoutGpu.LocalBytes == PrivateFlow.Windows.ModelList.DownloadBytes(Listed(ModelCatalogue.NotesModel), false) && withoutGpu.LocalNote!.StartsWith("Without an NVIDIA GPU it runs on the processor")
                    && smallMemory.Local.Count == 0 && smallMemory.LocalBytes == 0 && smallMemory.LocalNote!.StartsWith("None: this PC has 6.4 GB of memory")
                    && smallGpu.Local.Count == 1 && smallGpu.LocalNote!.Contains("holds only part of it")
                    && smallDisk.Local.Count == 0 && smallDisk.DiskNote!.Contains("enough for the transcription models, not for the local processing model")
                    && tinyDisk.DiskNote!.Contains("Free some space first") && tinyDisk.TranscriptionBytes > 0,
                    "the recommendation counts only what is missing, follows the script's processor answer without an NVIDIA GPU, leaves the local model out with too little memory or disk and says why, and notes a GPU too small to hold it");
                // Remove decides by the file's real path. A models folder that is a junction (Windows) or a symbolic link elsewhere is refused, and the
            // file it would reach stays; another feature reading the same file through a path entered by hand keeps it; the shared speech file and
            // a path outside the data folder keep holding.
            bool Link(string link, string target)
            {
                if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return true; }
                using var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                made.WaitForExit(); return made.ExitCode == 0;
            }
            var linkedRoot = Path.Combine(testRoot, "linked-data"); var outside = Path.Combine(testRoot, "outside-target");
            Directory.CreateDirectory(linkedRoot); Directory.CreateDirectory(outside);
            var outsideMedium = Touch(Path.Combine(outside, "ggml-medium.bin"));
            var linked = Link(Path.Combine(linkedRoot, "models"), outside);
            Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", linkedRoot);
            var linkedSettings = new PrivateFlow.Windows.Settings { ModelPath = Path.Combine(linkedRoot, "elsewhere", "ggml-base.bin"), SpeechDetection = false };
            var throughLink = PrivateFlow.Windows.ModelList.RemoveRefusal(linkedSettings, Listed(ModelCatalogue.SpeechMedium), null);
            var directRefusal = PrivateFlow.Windows.ModelFiles.DeletionRefusal(Path.Combine(linkedRoot, "models", "ggml-medium.bin"), linkedRoot);
            Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", testRoot);
            Directory.Delete(Path.Combine(linkedRoot, "models"));
            Check(linked && throughLink is { } linkReason && linkReason.Contains("is a link to another place") && directRefusal == throughLink && File.Exists(outsideMedium),
                $"Remove refuses a model whose models folder is a junction or link out of the data folder, says why, and the file there stays ({throughLink})");
            var sharedSettings = new PrivateFlow.Windows.Settings { OutputStyle = OutputStyle.LocalPolish, LocalPolishModel = LocalPolishModel.PolishModel, MeetingNotesStyle = MeetingNotesStyle.Extractive,
                LocalRewriteEnginePath = Touch(Path.Combine(testRoot, "runtime", "rewrite", "llama-server.exe")), LocalRewriteModelPath = Touch(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.NotesModel))),
                MeetingNotesEnginePath = Touch(Path.Combine(testRoot, "runtime", "notes", "llama-server.exe")), MeetingNotesModelPath = PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.NotesModel)), SpeechDetection = false };
            var sharedNotes = PrivateFlow.Windows.ModelList.RemoveRefusal(sharedSettings, Listed(ModelCatalogue.NotesModel), null);
            var speechShared = new PrivateFlow.Windows.Settings { ModelPath = Touch(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechMedium))), SpeechDetection = false };
            speechShared.MeetingModelPath = speechShared.ModelPath;
            var sharedDictation = PrivateFlow.Windows.ModelList.RemoveRefusal(speechShared, Listed(ModelCatalogue.SpeechMedium), null);
            speechShared.ModelPath = PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.SpeechBase));
            var sharedFinalPass = PrivateFlow.Windows.ModelList.RemoveRefusal(speechShared, Listed(ModelCatalogue.SpeechMedium), null);
            var outsideTyped = new PrivateFlow.Windows.Settings { MeetingNotesModelPath = Touch(Path.Combine(Path.GetTempPath(), "PrivateFlow-outside-" + Guid.NewGuid().ToString("N"), "notes.gguf")),
                MeetingNotesEnginePath = sharedSettings.MeetingNotesEnginePath, MeetingNotesStyle = MeetingNotesStyle.Extractive, SpeechDetection = false };
            File.Delete(PrivateFlow.Windows.ModelList.OwnFile(Listed(ModelCatalogue.NotesModel)));
            var outsideRefusal = PrivateFlow.Windows.ModelList.RemoveRefusal(outsideTyped, Listed(ModelCatalogue.NotesModel), null);
            Directory.Delete(Path.GetDirectoryName(outsideTyped.MeetingNotesModelPath)!, true);
            Check(sharedNotes!.StartsWith("Local polish uses it.") && sharedDictation!.StartsWith("Dictation uses it.") && sharedFinalPass!.StartsWith("The final pass of meetings and files uses it.")
                && outsideRefusal == "Its path was entered by hand; Private Flow removes only the models it installed.",
                "Remove decides per file: the notes model's file that Local polish reads through its own path is kept, a speech file the final pass shares stays needed after dictation moves away, and a path entered by hand outside the data folder is never deleted");
            // Sol's reproduction: a warm server whose launch arguments no longer match the saved choice is not reused.
            var serverType = typeof(PrivateFlow.Windows.LocalRewriter).GetNestedType("LocalServer", System.Reflection.BindingFlags.NonPublic)!;
            var launchMethod = typeof(PrivateFlow.Windows.LocalRewriter).GetMethod("PolishLaunch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var warm = new PrivateFlow.Windows.Settings { LocalPolishModel = LocalPolishModel.MeetingNotesModel, MeetingNotesGpuLayers = -1,
                MeetingNotesEnginePath = Path.Combine(testRoot, "missing-llama-server.exe"), MeetingNotesModelPath = Touch(Path.Combine(testRoot, "stand-in.gguf")) };
            var gpuLaunch = launchMethod.Invoke(null, [warm])!;
            warm.MeetingNotesGpuLayers = 0;
            var processorLaunch = launchMethod.Invoke(null, [warm])!;
            object NewServer(out System.Diagnostics.Process inert)
            {
                var server = Activator.CreateInstance(serverType, true)!;
                inert = System.Diagnostics.Process.Start(OperatingSystem.IsWindows()
                    ? new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep 60") { UseShellExecute = false, CreateNoWindow = true }
                    : new System.Diagnostics.ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
                serverType.GetField("process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(server, inert);
                serverType.GetField("identity", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(server, gpuLaunch.GetType().GetProperty("Identity")!.GetValue(gpuLaunch));
                return server;
            }
            async Task<(bool Reused, bool Failed)> Start(object server, object launch, System.Diagnostics.Process inert)
            {
                var failed = false;
                try { await (Task)serverType.GetMethod("StartAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(server, [launch, CancellationToken.None])!; }
                catch (InvalidOperationException) { failed = true; }
                // A stopped server has killed and disposed the process; a reused one still holds it, running.
                var reused = ReferenceEquals(serverType.GetField("process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(server), inert) && !inert.HasExited;
                if (reused) { inert.Kill(); inert.WaitForExit(); }
                return (reused, failed);
            }
            var sameServer = NewServer(out var sameInert);
            var sameChoice = Start(sameServer, gpuLaunch, sameInert).GetAwaiter().GetResult();
            var changedServer = NewServer(out var changedInert);
            var processorChoice = Start(changedServer, processorLaunch, changedInert).GetAwaiter().GetResult();
            Check(sameChoice == (true, false) && processorChoice == (false, true),
                "a warm polish server is reused for the same launch, and after Save moves Local polish to the processor only it is stopped and started with the new arguments (here a missing engine, so the start fails)");
            // A local model server is started again when what it would run changes, such as Local polish moved to the processor only.
            var served = new PrivateFlow.Windows.Settings { LocalPolishModel = LocalPolishModel.MeetingNotesModel, MeetingNotesGpuLayers = -1 };
            var onGpu = PrivateFlow.Windows.LocalRewriter.PolishServerIdentity(served);
            var sameAgain = PrivateFlow.Windows.LocalRewriter.PolishServerIdentity(served.Snapshot());
            served.MeetingNotesGpuLayers = 0; var onProcessor = PrivateFlow.Windows.LocalRewriter.PolishServerIdentity(served);
            served.LocalPolishModel = LocalPolishModel.PolishModel; var polishModelServer = PrivateFlow.Windows.LocalRewriter.PolishServerIdentity(served);
            var cpuBuild = new PrivateFlow.Windows.Settings { LocalPolishModel = LocalPolishModel.MeetingNotesModel, MeetingNotesGpuLayers = -1,
                MeetingNotesEnginePath = Touch(Path.Combine(testRoot, "cpu-build", "llama-server.exe")), MeetingNotesModelPath = Touch(Path.Combine(testRoot, "cpu-build", "notes.gguf")) };
            var launchOf = typeof(PrivateFlow.Windows.LocalRewriter).GetMethod("PolishLaunch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            bool UsesGpu(PrivateFlow.Windows.Settings of) { var launch = launchOf.Invoke(null, [of])!; return (bool)launch.GetType().GetProperty("UsesGpu")!.GetValue(launch)!; }
            var processorBuildOnGpu = UsesGpu(cpuBuild);
            Touch(Path.Combine(testRoot, "cpu-build", "ggml-cuda.dll"));
            var cudaBuildOnGpu = UsesGpu(cpuBuild);
            cpuBuild.MeetingNotesGpuLayers = 0;
            var cudaBuildProcessorOnly = UsesGpu(cpuBuild);
            Check(!processorBuildOnGpu && cudaBuildOnGpu && !cudaBuildProcessorOnly,
                "a polish server counts as on the GPU only with the CUDA build of llama.cpp and GPU layers; the processor build with automatic layers does not, so the final pass does not wait to stop it");
            Check(onGpu == sameAgain && onGpu != onProcessor && onProcessor.Contains("--device") && onGpu.Contains("auto") && polishModelServer != onProcessor && polishModelServer.Contains(served.LocalRewriteModelPath),
                "the polish server's identity is its engine, model and launch arguments: moving Local polish to the processor only or to the other model starts it again, the same settings keep it");
            // The choice for Local polish is a stored preference of the Models section, and a dictation with the meeting notes model chosen but
                // missing pastes Clean and says why.
                var choice = new PrivateFlow.Windows.Settings { LocalPolishModel = LocalPolishModel.MeetingNotesModel }; choice.SaveAs(choice);
                var storedChoice = File.ReadAllText(file).Contains("\"LocalPolishModel\": \"MeetingNotesModel\"") && PrivateFlow.Windows.Settings.Load().LocalPolishModel == LocalPolishModel.MeetingNotesModel;
                File.WriteAllText(file, "{\"SettingsVersion\":5,\"LocalPolishModel\":\"SomethingNewer\"}");
                var unknownChoice = PrivateFlow.Windows.Settings.Load().LocalPolishModel;
                var shownChoice = new PrivateFlow.Windows.Settings(); var changedChoice = shownChoice.Snapshot(); changedChoice.LocalPolishModel = LocalPolishModel.MeetingNotesModel;
                var changedLayers = shownChoice.Snapshot(); changedLayers.MeetingNotesGpuLayers = 0;
                Check(storedChoice && unknownChoice == LocalPolishModel.PolishModel && PrivateFlow.Windows.PreferenceSections.Changed(changedChoice, shownChoice).SequenceEqual([PrivateFlow.Windows.PreferenceSections.Models])
                    && PrivateFlow.Windows.PreferenceSections.Changed(changedLayers, shownChoice).SequenceEqual([PrivateFlow.Windows.PreferenceSections.Models]),
                    "the model for Local polish is stored by name, an unknown name reads as the Local polish model, and it and where the notes model runs are preferences of the Models section");
                var missingNotesPolish = new PrivateFlow.Windows.Settings { OutputStyle = OutputStyle.LocalPolish, LocalPolishModel = LocalPolishModel.MeetingNotesModel,
                    MeetingNotesEnginePath = Path.Combine(testRoot, "missing", "llama-server.exe"), MeetingNotesModelPath = Path.Combine(testRoot, "missing", "model.gguf") };
                var pasted = PrivateFlow.Windows.DictationPipeline.ProcessAsync("please send the report today", missingNotesPolish, CancellationToken.None).GetAwaiter().GetResult();
                Check(pasted.Notice == "Used Clean: " + PrivateFlow.Windows.LocalRewriter.PolishWithNotesModelMissing,
                    $"Local polish with the meeting notes model chosen but not installed pastes Clean and says to install it under Models ({pasted.Notice})");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", oldRoot);
            Directory.Delete(testRoot, true);
        }
    }

    public static void VerifyCpuBackendOrder()
    {
        // Feature lists from ggml/src/CMakeLists.txt and weights from ggml-cpu/arch/x86/cpu-feats.cpp at whisper.cpp v1.9.2.
        var featureWeights = new Dictionary<string, int> { ["FMA"] = 1, ["F16C"] = 2, ["SSE42"] = 4, ["BMI2"] = 8, ["AVX"] = 16, ["AVX2"] = 32,
            ["AVX_VNNI"] = 64, ["AVX512"] = 128, ["AVX512_VBMI"] = 256, ["AVX512_BF16"] = 512, ["AVX512_VNNI"] = 1024 };
        var upstreamVariants = new Dictionary<string, string[]>
        {
            ["x64"] = [], ["sse42"] = ["SSE42"], ["sandybridge"] = ["SSE42", "AVX"],
            ["haswell"] = ["SSE42", "AVX", "F16C", "FMA", "AVX2", "BMI2"],
            ["skylakex"] = ["SSE42", "AVX", "F16C", "FMA", "AVX2", "BMI2", "AVX512"],
            ["cannonlake"] = ["SSE42", "AVX", "F16C", "FMA", "AVX2", "BMI2", "AVX512", "AVX512_VBMI"],
            ["cascadelake"] = ["SSE42", "AVX", "F16C", "FMA", "AVX2", "BMI2", "AVX512", "AVX512_VNNI"],
            ["icelake"] = ["SSE42", "AVX", "F16C", "FMA", "AVX2", "BMI2", "AVX512", "AVX512_VBMI", "AVX512_VNNI"],
            ["alderlake"] = ["SSE42", "AVX", "F16C", "FMA", "AVX2", "BMI2", "AVX_VNNI"],
        };
        var upstreamOrder = upstreamVariants.OrderByDescending(variant => 1 + variant.Value.Sum(feature => featureWeights[feature]))
            .Select(variant => $"ggml-cpu-{variant.Key}.dll");
        Check(SpeechRuntime.CpuBackendFiles.SequenceEqual(upstreamOrder), "CPU speech backends are tried in ggml's score order of their feature sets");
        Check(SpeechRuntime.IsApplicationControlBlock(4551) && SpeechRuntime.IsApplicationControlBlock(4559) && SpeechRuntime.IsApplicationControlBlock(4581) && !SpeechRuntime.IsApplicationControlBlock(126)
            && !SpeechRuntime.IsApplicationControlBlock(4560), "only App Control errors are reported as Smart App Control blocks");
        // The listener table is a Windows API; the CI runs these checks in a Linux guest.
        if (OperatingSystem.IsWindows())
        {
            var loopbackListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            loopbackListener.Start();
            var listenerPort = ((System.Net.IPEndPoint)loopbackListener.LocalEndpoint).Port;
            Check(PrivateFlow.Windows.LoopbackListeners.OwnerProcessIds(listenerPort).SequenceEqual([Environment.ProcessId]), "the local polish port check finds the process that listens on it");
            loopbackListener.Stop();
            Check(PrivateFlow.Windows.LoopbackListeners.OwnerProcessIds(listenerPort).Count == 0, "a closed local port has no owner, so local polish keeps waiting for llama-server");
        }
    }
}
