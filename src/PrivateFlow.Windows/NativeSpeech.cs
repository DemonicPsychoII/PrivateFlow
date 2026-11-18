using System.Runtime.InteropServices;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// The layouts below are the pinned whisper.cpp v1.9.2 C ABI on Windows x64.
// Run in a disposable child: a native crash cannot corrupt capture, settings or insertion.
internal sealed class NativeSpeech : IDisposable
{
    private readonly nint library;
    private readonly nint backendLibrary;
    private nint context;
    private nint vadContext;
    private string? vadContextPath;
    private readonly LogCallback quietLog = (_, _, _) => { };
    private readonly ContextFree free;
    private readonly Full full;
    private readonly SegmentCount count;
    private readonly SegmentText text;
    private readonly SegmentTime start, end;
    private readonly DefaultFull defaultFull;
    private readonly ContextFree freeParams;

    // The GPU memory when the model did not fit the free memory and loaded anyway; null otherwise.
    public GpuMemory? LowMemory { get; private set; }

    // canFallBack: the caller can wait or use a smaller model, so a model that does not fit the free GPU memory is refused.
    // loadingWithLowMemory hears, before the slow load starts, that the model loads anyway into too little free GPU memory.
    public NativeSpeech(string engine, string model, bool canFallBack, Action<GpuMemory>? loadingWithLowMemory = null)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(engine))!;
        if (!System.IO.File.Exists(model)) throw new InvalidOperationException("Speech model missing.");
        // This process only hosts speech. A blocked runtime file, including a dependency, must fail
        // the load instead of opening a modal "Bad Image" box nobody expects from a background worker.
        SetErrorMode(GetErrorMode() | FailCriticalErrors);
        SetDllDirectory(directory);
        backendLibrary = LoadRuntimeLibrary(System.IO.Path.Combine(directory, "ggml.dll"));
        var loadBackend = Marshal.GetDelegateForFunctionPointer<LoadBackend>(NativeLibrary.GetExport(backendLibrary, "ggml_backend_load"));
        // ggml_backend_load_all_from_path maps every CPU variant just to score it, and Smart App Control
        // rates each unsigned file separately, so one unused variant can be blocked. Load only what runs.
        TryLoadBackend(directory, "ggml-cuda.dll", loadBackend, out _);
        LoadCpuBackend(directory, loadBackend);
        library = LoadRuntimeLibrary(System.IO.Path.Combine(directory, "whisper.dll"));
        var version = Marshal.PtrToStringUTF8(Export<Version>("whisper_version")());
        if (version != "1.9.2") throw new InvalidOperationException("Native speech runtime version is not supported.");
        Export<SetLog>("whisper_log_set")(quietLog, 0);
        free = Export<ContextFree>("whisper_free");
        full = Export<Full>("whisper_full");
        count = Export<SegmentCount>("whisper_full_n_segments");
        text = Export<SegmentText>("whisper_full_get_segment_text");
        start = Export<SegmentTime>("whisper_full_get_segment_t0");
        end = Export<SegmentTime>("whisper_full_get_segment_t1");
        defaultFull = Export<DefaultFull>("whisper_full_default_params_by_ref");
        freeParams = Export<ContextFree>("whisper_free_params");
        var defaults = Export<DefaultContext>("whisper_context_default_params_by_ref")();
        try
        {
            var parameters = Marshal.PtrToStructure<ContextParameters>(defaults);
            parameters.UseGpu = directory.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
            if (parameters.UseGpu == 1) CheckGpuMemory(directory, model, canFallBack);
            if (LowMemory is { } spilling) loadingWithLowMemory?.Invoke(spilling);
            context = Export<ContextInit>("whisper_init_from_file_with_params")(model, parameters);
            if (context == 0 && LowMemory is { } low) throw new GpuMemoryException(SpeechRuntime.GpuMemoryShortMessage(SpeechSetup.ModelName(model), low), low);
            if (context == 0) throw new InvalidOperationException("Speech model could not load. Check memory and the selected backend.");
        }
        finally { Export<ContextFree>("whisper_free_context_params")(defaults); }
    }

    public WorkerReply Handle(float[] samples, SpeechRequest request) => request.Operation switch
    {
        SpeechOperation.Transcribe => new(Transcribe(samples, request), null),
        SpeechOperation.DetectLanguage => new(null, null, LanguageProbabilities: DetectLanguage(samples, SpeechLanguages.Parse(request.Languages))),
        SpeechOperation.SpeechProbabilities => new(null, null, SpeechProbabilities: SpeechProbabilities(samples, request.VadPath, request.ResetSpeechState)),
        _ => throw new InvalidOperationException("Unknown speech operation.")
    };

    // Whisper's own detection chooses among all 99 languages, so quiet English can come out as Welsh.
    // The probabilities restricted to the allowed languages are renormalized to sum to one.
    private Dictionary<string, double> DetectLanguage(float[] samples, IReadOnlyList<string> allowed)
    {
        if (samples.Length is < 160 or > 480000) throw new InvalidOperationException("Invalid speech chunk length.");
        if (allowed.Count == 0) throw new InvalidOperationException("No language allowed.");
        var probabilities = new float[Export<LanguageCount>("whisper_lang_max_id")() + 1];
        var pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        var output = GCHandle.Alloc(probabilities, GCHandleType.Pinned);
        try
        {
            var threads = Math.Min(8, Environment.ProcessorCount);
            if (Export<PcmToMel>("whisper_pcm_to_mel")(context, pinned.AddrOfPinnedObject(), samples.Length, threads) != 0
                || Export<LanguageDetect>("whisper_lang_auto_detect")(context, 0, threads, output.AddrOfPinnedObject()) < 0)
                throw new InvalidOperationException("Local language detection failed.");
        }
        finally { pinned.Free(); output.Free(); }
        var languageId = Export<LanguageId>("whisper_lang_id");
        var restricted = allowed.ToDictionary(language => language, language => (double)probabilities[languageId(language)]);
        var total = restricted.Values.Sum();
        return restricted.ToDictionary(pair => pair.Key, pair => total > 0 ? pair.Value / total : 1.0 / restricted.Count);
    }

    // Silero speech probability per 512-sample frame. A caller streams a long recording in blocks and
    // resets the detector's memory only at the start, so block edges do not change the result.
    private float[] SpeechProbabilities(float[] samples, string vadPath, bool reset)
    {
        if (samples.Length is < 512 or > 480000 || samples.Length % SpeechSegmentation.FrameSamples != 0) throw new InvalidOperationException("Invalid speech block length.");
        if (!System.IO.File.Exists(vadPath)) throw new InvalidOperationException("Speech detection model missing. Run speech setup first.");
        if (vadContext == 0 || vadContextPath != vadPath)
        {
            if (vadContext != 0) { Export<ContextFree>("whisper_vad_free")(vadContext); vadContext = 0; }
            vadContext = Export<VadInit>("whisper_vad_init_from_file_with_params")(vadPath, new VadContextParameters { Threads = Math.Min(4, Environment.ProcessorCount), UseGpu = 0, GpuDevice = 0 });
            if (vadContext == 0) throw new InvalidOperationException("Speech detection model could not load.");
            vadContextPath = vadPath; reset = true;
        }
        if (reset) Export<ContextFree>("whisper_vad_reset_state")(vadContext);
        var pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try { if (!Export<VadDetect>("whisper_vad_detect_speech_no_reset")(vadContext, pinned.AddrOfPinnedObject(), samples.Length)) throw new InvalidOperationException("Local speech detection failed."); }
        finally { pinned.Free(); }
        var count = Export<SegmentCount>("whisper_vad_n_probs")(vadContext);
        var result = new float[count];
        Marshal.Copy(Export<VadProbabilities>("whisper_vad_probs")(vadContext), result, 0, count);
        return result;
    }

    private SpeechResult Transcribe(float[] samples, SpeechRequest request)
    {
        if (samples.Length is < 160 or > 480000) throw new InvalidOperationException("Invalid speech chunk length.");
        if (request.DetectSpeech && !System.IO.File.Exists(request.VadPath))
            throw new InvalidOperationException("Speech detection model missing. Run speech setup, or explicitly disable detection.");
        var chosen = request.Language;
        var allowed = SpeechLanguages.Parse(request.Languages);
        if (chosen == "auto" && allowed.Count == 1) chosen = allowed[0];
        else if (chosen == "auto" && allowed.Count > 1)
        {
            var speech = samples;
            if (request.DetectSpeech)
            {
                var frames = samples.Length / SpeechSegmentation.FrameSamples * SpeechSegmentation.FrameSamples;
                var probabilities = frames == 0 ? [] : SpeechProbabilities(samples[..frames], request.VadPath, true);
                speech = SpeechSegmentation.SpeechOnly(samples, SpeechSegmentation.Regions(probabilities, samples.Length / 16000.0));
            }
            if (speech.Length < 1600) return new([], samples.Length / 16000.0, allowed[0]);
            chosen = DetectLanguage(speech, allowed).MaxBy(pair => pair.Value).Key;
        }
        var defaults = defaultFull(request.BeamSize > 1 ? 1 : 0);
        var language = Marshal.StringToCoTaskMemUTF8(chosen);
        var prompt = Marshal.StringToCoTaskMemUTF8(request.Vocabulary);
        var vad = Marshal.StringToCoTaskMemUTF8(request.VadPath);
        var pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            var parameters = Marshal.PtrToStructure<FullParameters>(defaults);
            parameters.Threads = Math.Min(8, Environment.ProcessorCount);
            parameters.NoContext = 1;
            parameters.PrintSpecial = parameters.PrintProgress = parameters.PrintRealtime = parameters.PrintTimestamps = 0;
            parameters.Language = language;
            parameters.InitialPrompt = prompt;
            parameters.Vad = request.DetectSpeech ? (byte)1 : (byte)0;
            parameters.VadPath = vad;
            parameters.SuppressNonSpeechTokens = 1;
            if (request.BeamSize > 1) parameters.BeamSize = request.BeamSize;
            var code = full(context, parameters, pinned.AddrOfPinnedObject(), samples.Length);
            if (code != 0) throw new InvalidOperationException(SpeechRuntime.ProcessingFailedMessage(code));
            var segments = new List<TranscriptSegment>();
            for (var i = 0; i < count(context); i++)
            {
                var content = Marshal.PtrToStringUTF8(text(context, i))?.Trim() ?? "";
                if (content.Length > 0) segments.Add(new(start(context, i) / 100.0, end(context, i) / 100.0,
                    TextCleanup.JoinSegments(content)));
            }
            var detected = Marshal.PtrToStringUTF8(Export<LanguageName>("whisper_lang_str")(Export<SegmentCount>("whisper_full_lang_id")(context))) ?? chosen;
            return new(segments, samples.Length / 16000.0, detected);
        }
        finally
        {
            pinned.Free();
            Marshal.FreeCoTaskMem(language); Marshal.FreeCoTaskMem(prompt); Marshal.FreeCoTaskMem(vad);
            freeParams(defaults);
        }
    }

    // With too little free GPU memory, the driver moves what does not fit to system memory and every step runs many times slower,
    // until the host's timeouts give up. A caller that can fall back gets a refusal that says at once what is wrong; others load
    // anyway with a warning. The CUDA backend reports the free memory of the device left by all programs.
    private void CheckGpuMemory(string directory, string model, bool canFallBack)
    {
        var device = Marshal.GetDelegateForFunctionPointer<DeviceByType>(NativeLibrary.GetExport(backendLibrary, "ggml_backend_dev_by_type"))(GpuDeviceType);
        if (device == 0) return;
        var baseLibrary = LoadRuntimeLibrary(System.IO.Path.Combine(directory, "ggml-base.dll"));
        nuint available, total;
        try { Marshal.GetDelegateForFunctionPointer<DeviceMemory>(NativeLibrary.GetExport(baseLibrary, "ggml_backend_dev_memory"))(device, out available, out total); }
        finally { NativeLibrary.Free(baseLibrary); }
        var memory = new GpuMemory((long)available, (long)total, SpeechRuntime.GpuMemoryNeeded(new System.IO.FileInfo(model).Length));
        switch (SpeechRuntime.DecideGpuLoad(memory, canFallBack))
        {
            case GpuMemoryDecision.Refuse: throw new GpuMemoryException(SpeechRuntime.GpuMemoryShortMessage(SpeechSetup.ModelName(model), memory), memory);
            case GpuMemoryDecision.LoadWithWarning: LowMemory = memory; break;
        }
    }
    // GGML_BACKEND_DEVICE_TYPE_GPU in the pinned v1.9.2 ggml-backend.h.
    private const int GpuDeviceType = 1;
    private static void LoadCpuBackend(string directory, LoadBackend loadBackend)
    {
        string? blocked = null;
        foreach (var file in SpeechRuntime.CpuBackendFiles)
        {
            if (TryLoadBackend(directory, file, loadBackend, out var error)) return;
            if (SpeechRuntime.IsApplicationControlBlock(error)) blocked ??= file;
        }
        throw new InvalidOperationException(blocked is null ? "No CPU speech backend in the runtime supports this processor." : SpeechRuntime.BlockedMessage(blocked));
    }
    private static bool TryLoadBackend(string directory, string file, LoadBackend loadBackend, out int error)
    {
        error = 0;
        var path = System.IO.Path.Combine(directory, file);
        if (!System.IO.File.Exists(path)) return false;
        // ggml_backend_load does not say why a load failed, and it reads its path in the ANSI code page.
        // Loading by the full path first reveals a block; ggml then gets the already loaded module by name.
        var probe = TryLoadLibrary(path, out error);
        if (probe == 0) return false;
        try { return loadBackend(file) != 0; }
        finally { FreeLibrary(probe); }
    }
    private static nint LoadRuntimeLibrary(string path)
    {
        var handle = TryLoadLibrary(path, out var error);
        if (handle != 0) return handle;
        var file = System.IO.Path.GetFileName(path);
        throw new InvalidOperationException(SpeechRuntime.IsApplicationControlBlock(error) ? SpeechRuntime.BlockedMessage(file)
            : $"Could not load {file} from the speech runtime: {new System.ComponentModel.Win32Exception(error).Message}");
    }
    private static nint TryLoadLibrary(string path, out int error)
    {
        var handle = LoadLibraryEx(path, 0, LoadWithAlteredSearchPath);
        error = handle == 0 ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }
    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public void Dispose()
    {
        if (vadContext != 0) { Export<ContextFree>("whisper_vad_free")(vadContext); vadContext = 0; }
        if (context != 0) { free(context); context = 0; }
        NativeLibrary.Free(library); NativeLibrary.Free(backendLibrary);
    }
    private const uint LoadWithAlteredSearchPath = 0x8, FailCriticalErrors = 0x1;
    [DllImport("kernel32.dll")] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DeviceByType(int type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeviceMemory(nint device, out nuint available, out nuint total);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint LanguageName(int id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint LoadBackend([MarshalAs(UnmanagedType.LPStr)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LogCallback(int level, nint message, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLog(LogCallback callback, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DefaultContext();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint DefaultFull(int strategy);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ContextInit([MarshalAs(UnmanagedType.LPUTF8Str)] string model, ContextParameters parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ContextFree(nint pointer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Full(nint context, FullParameters parameters, nint samples, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SegmentCount(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint SegmentText(nint context, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long SegmentTime(nint context, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LanguageCount();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LanguageId([MarshalAs(UnmanagedType.LPUTF8Str)] string language);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PcmToMel(nint context, nint samples, int count, int threads);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LanguageDetect(nint context, int offsetMilliseconds, int threads, nint probabilities);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VadInit([MarshalAs(UnmanagedType.LPUTF8Str)] string model, VadContextParameters parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool VadDetect(nint context, nint samples, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint VadProbabilities(nint context);

    [StructLayout(LayoutKind.Sequential)] private struct VadContextParameters
    {
        public int Threads;
        public byte UseGpu;
        public int GpuDevice;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ContextParameters
    {
        public byte UseGpu, FlashAttention;
        public int GpuDevice;
        public byte DtwTimestamps;
        public int DtwPreset, DtwTop;
        public nuint HeadCount;
        public nint Heads;
        public nuint DtwMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct VadParameters
    {
        public float Threshold;
        public int MinimumSpeech, MinimumSilence;
        public float MaximumSpeech;
        public int Padding;
        public float Overlap;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FullParameters
    {
        public int Strategy, Threads, MaxContext, Offset, Duration;
        public byte Translate, NoContext, NoTimestamps, SingleSegment, PrintSpecial, PrintProgress, PrintRealtime, PrintTimestamps;
        public byte TokenTimestamps;
        public float TimestampThreshold, TimestampSumThreshold;
        public int MaxLength;
        public byte SplitOnWord;
        public int MaxTokens;
        public byte Debug;
        public int AudioContext;
        public byte Diarize;
        public nint SuppressRegex, InitialPrompt;
        public byte CarryPrompt;
        public nint PromptTokens;
        public int PromptTokenCount;
        public nint Language;
        public byte DetectLanguage, SuppressBlank, SuppressNonSpeechTokens;
        public float Temperature, MaxInitialTime, LengthPenalty, TemperatureIncrement, EntropyThreshold, LogProbabilityThreshold, NoSpeechThreshold;
        public int GreedyBestOf, BeamSize;
        public float BeamPatience;
        public nint SegmentCallback, SegmentUser, ProgressCallback, ProgressUser, EncoderCallback, EncoderUser, AbortCallback, AbortUser, LogitsCallback, LogitsUser;
        public nint GrammarRules;
        public nuint GrammarRuleCount, GrammarStart;
        public float GrammarPenalty;
        public byte Vad;
        public nint VadPath;
        public VadParameters VadParameters;
    }
}
