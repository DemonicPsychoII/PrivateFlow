using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// A choice saved by name. A name this version does not know, such as one written by a newer version, reads as an undefined
// value, which Load replaces with that setting's default; the standard converter would fail the whole file instead.
internal sealed class NamedChoice<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) ? (T)Enum.ToObject(typeof(T), number)
        : reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), true, out var named) ? named
        : (T)Enum.ToObject(typeof(T), -1);
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

// The settings file exists but cannot be read now: in use by another program, not accessible, or invalid and impossible to set aside.
// Nothing is then started with the defaults or written over the file.
public sealed class SettingsUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class Settings
{
    public const int CurrentVersion = 5;
    public static string Root => Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR") is { Length: > 0 } root
        ? (Path.IsPathFullyQualified(root) ? Path.GetFullPath(root) : throw new InvalidOperationException("PRIVATE_FLOW_DATA_DIR must be an absolute path."))
        : OwnRoot;
    private static string OwnRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateFlow");
    // The diagnostic and evaluation modes write settings, meetings and reports into their data folder, so they run only with an
    // absolute PRIVATE_FLOW_DATA_DIR outside the app's own folder.
    public static bool IsolatedRoot => Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR") is { Length: > 0 } root && Path.IsPathFullyQualified(root)
        && !(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar).StartsWith(OwnRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public string EnginePath { get; set; } = Path.Combine(Root, "runtime", "cpu", "whisper-cli.exe");
    public string ModelPath { get; set; } = Path.Combine(Root, "models", ModelCatalogue.Model(ModelCatalogue.SpeechBase).File);
    // A slower, more accurate model for the meeting and file final pass; empty uses ModelPath. Setup writes it with the engine.
    public string MeetingModelPath { get; set; } = "";
    // "auto" follows the recommended setup for this PC, which Private Flow reapplies once after each update.
    public string SpeechBackend { get; set; } = "auto";
    public string SpeechModel { get; set; } = "auto";
    // Engine or model paths typed in by hand; setup and updates then leave them alone.
    public bool SpeechPathsCustom { get; set; }
    // App version that last brought the speech setup up to date.
    public string SpeechSetupVersion { get; set; } = "";
    public string Language { get; set; } = "auto";
    // Names and terms the speaker uses, comma-separated. Whisper gets them as its initial prompt
    // and Concise as preferred spellings; plain text, never a replacement rule.
    public string Vocabulary { get; set; } = "";
    public string MicrophoneId { get; set; } = "";
    // The output device whose sound meetings record; empty, also in older settings files, follows the Windows default.
    public string PlaybackDeviceId { get; set; } = "";
    public string VadModelPath { get; set; } = Path.Combine(Root, "models", ModelCatalogue.Model(ModelCatalogue.VoiceDetection).File);
    public string LocalRewriteEnginePath { get; set; } = Path.Combine(Root, "runtime", "rewrite", "llama-server.exe");
    public string LocalRewriteModelPath { get; set; } = Path.Combine(Root, "models", ModelCatalogue.Model(ModelCatalogue.PolishModel).File);
    // The model for the Local model notes style, separate from Local polish and run by a GPU build of llama.cpp. GPU layers: -1 lets
    // llama.cpp fit as many as free GPU memory allows, 0 keeps the model on the processor. The context holds prompt and answer together.
    public string MeetingNotesEnginePath { get; set; } = Path.Combine(Root, "runtime", "notes", "llama-server.exe");
    public string MeetingNotesModelPath { get; set; } = Path.Combine(Root, "models", MeetingNotesModel.FileName);
    public int MeetingNotesGpuLayers { get; set; } = -1;
    public int MeetingNotesContextTokens { get; set; } = MeetingNotesModel.ContextTokens;
    [JsonConverter(typeof(NamedChoice<LocalPolishModel>))]
    public LocalPolishModel LocalPolishModel { get; set; } = LocalPolishModel.PolishModel;
    // How long a Local polish dictation waits while meeting notes are written before it pastes Clean (the owner, 2026-11-17: limited,
    // configurable, two minutes by default). Files from before this setting mean two minutes.
    public const int MinimumPolishWaitSeconds = 10, MaximumPolishWaitSeconds = 1800, DefaultPolishWaitSeconds = 120;
    public int PolishWaitSeconds { get; set; } = DefaultPolishWaitSeconds;
    public bool SpeechDetection { get; set; } = true;
    public bool KeepModelWarm { get; set; } = true;
    public bool SpokenFormatting { get; set; }
    public bool TechnicalMode { get; set; }
    public bool LiteralMode { get; set; }
    public string SelectedProfileId { get; set; } = "";
    public List<DictationProfile> Profiles { get; set; } = [];
    public List<PhraseRule> Corrections { get; set; } = [];
    public List<PhraseRule> Snippets { get; set; } = [];
    public bool RetainMeetingAudio { get; set; }
    // The last meeting's choices, offered again for the next one.
    public string MeetingLanguages { get; set; } = SpeechLanguages.Offered;
    // Read only from older settings: Load moves its terms into Vocabulary, which meetings now share with dictation.
    public string MeetingVocabulary { get; set; } = "";
    public int MeetingRetentionDays { get; set; } = 30;
    // Model-written notes use the separate meeting notes model (Qwen3.5-4B) on this PC or the API of Concise; files from before this setting mean Extractive.
    [JsonConverter(typeof(NamedChoice<MeetingNotesStyle>))]
    public MeetingNotesStyle MeetingNotesStyle { get; set; } = MeetingNotesStyle.Extractive;
    public int SettingsVersion { get; set; } = CurrentVersion;
    public bool AutoInsert { get; set; } = true;
    public uint ShortcutModifiers { get; set; } = 3;
    public uint ShortcutKey { get; set; } = 0x20;
    // Starts and stops a meeting; key 0 leaves it unset, which is the default.
    public uint MeetingShortcutModifiers { get; set; }
    public uint MeetingShortcutKey { get; set; }
    // The system-audio half of the meeting wave, one of SystemAudioColors.Presets.
    public string SystemAudioColor { get; set; } = SystemAudioColors.Default;
    [JsonConverter(typeof(NamedChoice<ShortcutBehavior>))]
    public ShortcutBehavior ShortcutBehavior { get; set; } = ShortcutBehavior.Toggle;
    [JsonConverter(typeof(NamedChoice<OutputStyle>))]
    public OutputStyle OutputStyle { get; set; } = OutputStyle.Clean;
    // Concise style sends the transcript to this OpenAI-compatible endpoint. The key is read
    // from an environment variable so it never lands in settings.json.
    public string RewriteEndpoint { get; set; } = "";
    // Haiku answered dictated questions as a chat assistant and dropped sentences in testing;
    // gpt-6-luna applied corrections and kept every point in ~2-5 s.
    public string RewriteModel { get; set; } = "gpt-6-luna";
    public string RewriteKeyVariable { get; set; } = "PRIVATE_FLOW_API_KEY";
    [JsonIgnore] public bool MissingRewriteEndpoint => missingRewriteEndpoint;
    private bool missingRewriteEndpoint;
    // A file in use by another program or not accessible is read again for about a second; then the load fails, so nothing starts
    // with the defaults over an intact file and nothing is renamed.
    private const int ReadAttempts = 10;
    public static Settings Load()
    {
        using var held = Lock();
        var file = Path.Combine(Root, "settings.json");
        string json;
        for (var attempt = 1; ; attempt++)
        {
            try { json = File.ReadAllText(file); break; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ReadAttempts) throw new SettingsUnavailableException($"settings.json is in use by another program or not accessible: {ex.Message}", ex);
                Thread.Sleep(100);
            }
        }
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(json) ?? new();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Unreadable(file, "It holds no settings object.");
            settings.missingRewriteEndpoint = !document.RootElement.TryGetProperty(nameof(RewriteEndpoint), out _);
            var version = document.RootElement.TryGetProperty(nameof(SettingsVersion), out var value) && value.TryGetInt32(out var number) ? number : 0;
            // the owner requested automatic insertion as the default for this private app.
            // After the one-time migration, an explicit opt-out is preserved on restart.
            if (version < 2) settings.AutoInsert = true;
            // Version 3 replaced the whitespace-only toggle with output styles; filler removal
            // is the new default because 1:1 output was reported as too verbose.
            if (version < 3) settings.OutputStyle = OutputStyle.Clean;
            // Version 5 added the speech selectors. Paths setup wrote follow the recommendation from now
            // on; paths elsewhere were chosen by hand and stay as they are.
            if (version < 5) MigrateSpeechChoice(settings, SpeechSetup.HasNvidiaGpu);
            settings.SettingsVersion = CurrentVersion;
            settings.Profiles ??= [];
            settings.Corrections ??= [];
            settings.Snippets ??= [];
            settings.MeetingLanguages = string.Join(",", SpeechLanguages.Parse(settings.MeetingLanguages)) is { Length: > 0 } meeting ? meeting : SpeechLanguages.Offered;
            settings.Vocabulary = PrivateFlow.Core.MeetingVocabulary.MergeIntoShared(settings.Vocabulary, settings.MeetingVocabulary);
            settings.MeetingVocabulary = ""; settings.MeetingModelPath ??= "";
            settings.MeetingRetentionDays = Math.Clamp(settings.MeetingRetentionDays, 1, 3650);
            settings.MeetingNotesEnginePath ??= ""; settings.MeetingNotesModelPath ??= "";
            settings.MeetingNotesGpuLayers = Math.Max(-1, settings.MeetingNotesGpuLayers);
            settings.PolishWaitSeconds = Math.Clamp(settings.PolishWaitSeconds, MinimumPolishWaitSeconds, MaximumPolishWaitSeconds);
            settings.MeetingNotesContextTokens = Math.Clamp(settings.MeetingNotesContextTokens, ModelNotes.MinimumLocalContextTokens, ModelNotes.MaximumLocalContextTokens);
            if (!Enum.IsDefined(settings.OutputStyle)) settings.OutputStyle = OutputStyle.Clean;
            if (!Enum.IsDefined(settings.ShortcutBehavior)) settings.ShortcutBehavior = ShortcutBehavior.Toggle;
            if (!Enum.IsDefined(settings.MeetingNotesStyle)) settings.MeetingNotesStyle = MeetingNotesStyle.Extractive;
            if (!Enum.IsDefined(settings.LocalPolishModel)) settings.LocalPolishModel = LocalPolishModel.PolishModel;
            // A shortcut a stricter rule of this version refuses is cleared and named once, so it neither registers nor blocks saving.
            var dictationShortcut = new ActivationShortcut(settings.ShortcutModifiers, settings.ShortcutKey);
            if (!dictationShortcut.IsValid)
            {
                if (dictationShortcut.Key != 0) settings.DroppedShortcuts.Add(("the dictation shortcut", dictationShortcut));
                settings.ShortcutModifiers = 3; settings.ShortcutKey = 0x20;
            }
            var meetingShortcut = new ActivationShortcut(settings.MeetingShortcutModifiers, settings.MeetingShortcutKey);
            if (meetingShortcut.Key != 0 && !meetingShortcut.IsValid) settings.DroppedShortcuts.Add(("the meeting shortcut", meetingShortcut));
            if (!meetingShortcut.IsValid || meetingShortcut == new ActivationShortcut(settings.ShortcutModifiers, settings.ShortcutKey))
            { settings.MeetingShortcutModifiers = 0; settings.MeetingShortcutKey = 0; }
            foreach (var profile in settings.Profiles.Where(profile => profile.ShortcutKey != 0 && !new ActivationShortcut(profile.ShortcutModifiers, profile.ShortcutKey).IsValid))
            {
                settings.DroppedShortcuts.Add(($"the profile “{profile.Name}”", new(profile.ShortcutModifiers, profile.ShortcutKey)));
                profile.ShortcutModifiers = profile.ShortcutKey = 0;
            }
            settings.SystemAudioColor = SystemAudioColors.Normalize(settings.SystemAudioColor);
            return settings;
        }
        catch (JsonException ex) { return Unreadable(file, ex.Message); }
    }
    // A settings file read completely whose content is not settings, such as one with a mistyped value or cut short, is set aside
    // under a new name, never overwritten, and the app starts with the defaults; without that, the next save would replace it and its
    // profiles, corrections and vocabulary would be lost unnoticed. A file that cannot be set aside fails the load like one in use.
    public sealed record UnreadableFile(string Reason, string KeptAs);
    [JsonIgnore] public UnreadableFile? Unread => unread;
    private UnreadableFile? unread;
    private static Settings Unreadable(string file, string reason)
    {
        var kept = $"{file}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        try { File.Move(file, kept, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new SettingsUnavailableException($"settings.json holds no valid settings ({reason}) and could not be set aside: {ex.Message}", ex); }
        return new() { unread = new(reason, kept) };
    }
    // settings.json of a data folder has one writer at a time: whoever reads and adopts it or writes it holds settings.lock open without
    // sharing, from the read to the rename, as scripts/settings-file.ps1 does. Windows releases the handle when its process ends, it is the
    // same file whatever path, junction or session reaches the folder, and the file itself stays. About 30 s of waiting, then unavailable.
    internal static int LockAttempts = 600;
    internal static IDisposable Lock() => OpenExclusive("settings.lock", LockAttempts, "Another program kept the settings locked");
    // Held open by a running Private Flow for its lifetime, taken before its first load: a setup script run outside it finds the file in
    // use and leaves settings.json alone. A script's own brief check of the file is waited out.
    public static FileStream ClaimRunningInstance() => OpenExclusive("instance.lock", 20, "Another Private Flow runs on this data folder");
    private static FileStream OpenExclusive(string name, int attempts, string refusal)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.CreateDirectory(Root); return new FileStream(Path.Combine(Root, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < attempts) { Thread.Sleep(50); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new SettingsUnavailableException($"{refusal}: {ex.Message}", ex); }
        }
    }
    // Before version 5 the update dialog could keep a deliberate setup, such as the CPU engine on an NVIDIA PC.
    // Whatever Automatic would not pick on this PC stays an explicit choice.
    internal static void MigrateSpeechChoice(Settings settings, bool hasNvidiaGpu)
    {
        var model = Path.GetFileNameWithoutExtension(settings.ModelPath) is var name && name.StartsWith("ggml-") ? name[5..] : name;
        var backend = new[] { "cuda12", "cpu" }.FirstOrDefault(folder => IsSetupPath(settings.EnginePath, Path.Combine("runtime", folder)));
        if (backend is null || !IsSetupPath(settings.ModelPath, "models") || model is not ("base" or "medium" or "large-v3-turbo-q8_0")) { settings.SpeechPathsCustom = true; return; }
        if (!File.Exists(settings.EnginePath)) return;
        if (backend != (hasNvidiaGpu ? "cuda12" : "cpu")) settings.SpeechBackend = backend;
        // Medium was the GPU recommendation before large-v3-turbo, so it keeps following the recommendation.
        if (backend == "cuda12" ? model == "base" : model != "base") settings.SpeechModel = model;
    }
    private static bool IsSetupPath(string path, string folder) =>
        Path.IsPathFullyQualified(path) && Path.GetFullPath(path).StartsWith(Path.Combine(Root, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    // Paths typed in by hand stay as they are when Private Flow updates the recommended setup. The meeting model belongs to the
    // setup that installed it, so meetings then use the typed model too, not large-v3 on a processor it was not chosen for.
    public void UsePathsEnteredByHand(string enginePath, string modelPath)
    {
        if (enginePath == EnginePath && modelPath == ModelPath) return;
        EnginePath = enginePath; ModelPath = modelPath; MeetingModelPath = ""; SpeechPathsCustom = true;
    }
    // Shortcuts Load cleared because this version refuses them, with whose they were; the window names them once and stores the cleared ones.
    [JsonIgnore] public List<(string Owner, ActivationShortcut Shortcut)> DroppedShortcuts { get; } = [];
    // The one way to change what the app runs with: next is written to settings.json first, under the settings lock, and only once that
    // succeeded does this object take over its values, before anything else can fail. The object the whole app shares therefore always
    // holds exactly what is stored; a failed write leaves it as it was.
    public void SaveAs(Settings next)
    {
        var copy = next.Snapshot();
        using (Lock())
        {
            Write(copy);
            foreach (var property in Stored) property.SetValue(this, property.GetValue(copy));
        }
        Saved?.Invoke();
    }
    // Each save writes its own uniquely named file beside settings.json, flushed to the disk so a power loss cannot leave it cut short,
    // and renames that over settings.json. One left by a crash is never read, and the window removes it at the next start.
    private static string OwnPartial() => Path.Combine(Root, $"settings.json.{Guid.NewGuid():N}.partial");
    internal static bool IsOwnPartial(string path) =>
        Path.GetFileName(path) is var name && name.Length == "settings.json.".Length + 32 + ".partial".Length
        && name.StartsWith("settings.json.", StringComparison.Ordinal) && name.EndsWith(".partial", StringComparison.Ordinal)
        && name["settings.json.".Length..^".partial".Length].All(Uri.IsHexDigit);
    // At start, under the settings lock, so a setup script's write in progress is never removed: the temporary files of saves and of
    // scripts run outside the app, and the result files of setups, which only a crash leaves. The lock files stay.
    private static readonly System.Text.RegularExpressions.Regex Leftover = new(@"^(?:settings\.json\.setup-[0-9a-f]{32}\.partial|setup-result-[0-9a-f]{32}\.json)$");
    public static void RemoveLeftoverWrites()
    {
        if (!Directory.Exists(Root)) return;
        using var held = Lock();
        foreach (var leftover in Directory.EnumerateFiles(Root).Where(path => IsOwnPartial(path) || Leftover.IsMatch(Path.GetFileName(path))))
            try { File.Delete(leftover); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static void Write(Settings settings)
    {
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, "settings.json");
        var partial = OwnPartial();
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })));
                stream.Flush(true);
            }
            File.Move(partial, file, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    private static readonly System.Reflection.PropertyInfo[] Stored =
        typeof(Settings).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Where(property => property.CanWrite).ToArray();
    // The stored values in which two settings objects differ, by name, among the named ones or all; for the checks of the shared object
    // and the unsaved sections.
    public static List<string> Differences(Settings first, Settings second, IReadOnlySet<string>? among = null) =>
        Stored.Where(property => (among is null || among.Contains(property.Name)) && JsonSerializer.Serialize(property.GetValue(first)) != JsonSerializer.Serialize(property.GetValue(second)))
            .Select(property => property.Name).ToList();
    // Raised once the stored values changed, so views that show them refresh.
    public event Action? Saved;
    public Settings Snapshot() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
    public void ApplyProfile(DictationProfile profile, bool explicitSelection)
    {
        Language = profile.Language; Vocabulary = profile.Vocabulary;
        // Application matching cannot provide the explicit consent needed for API rewriting.
        OutputStyle = profile.OutputStyle == OutputStyle.Concise && !explicitSelection ? OutputStyle.Clean : profile.OutputStyle;
        // Automatic matching must preserve an explicit insertion opt-out.
        AutoInsert = explicitSelection ? profile.AutoInsert : AutoInsert && profile.AutoInsert;
        SpokenFormatting = profile.Formatting;
        TechnicalMode = profile.Technical; LiteralMode = profile.Literal;
        SelectedProfileId = profile.Id;
    }
}
