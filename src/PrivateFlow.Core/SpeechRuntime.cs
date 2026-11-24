namespace PrivateFlow.Core;

public static class SpeechRuntime
{
    // ggml scores each x86 CPU variant by a fixed sum of feature bits and rejects variants the
    // processor cannot run, so the first variant that loads in this order scores highest, as in
    // ggml_backend_load_all. Ordered by the CMake feature sets of the pinned whisper.cpp v1.9.2; its
    // MSVC builds score icelake/cascadelake and cannonlake/skylakex equal, and ggml takes either.
    public static readonly IReadOnlyList<string> CpuBackendFiles =
    [
        "ggml-cpu-icelake.dll", "ggml-cpu-cascadelake.dll", "ggml-cpu-cannonlake.dll", "ggml-cpu-skylakex.dll",
        "ggml-cpu-alderlake.dll", "ggml-cpu-haswell.dll", "ggml-cpu-sandybridge.dll", "ggml-cpu-sse42.dll", "ggml-cpu-x64.dll",
    ];

    // Win32 errors for files blocked by Smart App Control or another App Control policy.
    public static bool IsApplicationControlBlock(int win32Error) => win32Error is 4551 or (>= 4556 and <= 4559) or (>= 4580 and <= 4583);

    // whisper_full's result codes in the pinned v1.9.2: -7 is the decoder cache that beam search allocates on its first use,
    // which fails when another program holds the GPU memory it needs.
    public static string ProcessingFailedMessage(int code) => code == -7
        ? "Local speech processing failed: the decoder memory could not be allocated. Another program may be using the GPU memory."
        : $"Local speech processing failed (whisper code {code}).";

    // GPU memory a model takes in use, from its file: measured on an 8 GB NVIDIA GPU with the pinned runtime, large-v3-turbo-q8_0 took 1.38
    // and large-v3 with its eight-beam decoder cache 1.46 times its file size. Dictation decodes greedily and takes less.
    public static long GpuMemoryNeeded(long modelBytes) => modelBytes * 3 / 2;

    // A final pass, which can wait or fall back to a smaller model, refuses a model that does not fit the free memory. Dictation has
    // no smaller model, and a load that only spills over may still work, so it loads anyway with a warning unless the model is larger
    // than the whole GPU.
    public static GpuMemoryDecision DecideGpuLoad(GpuMemory memory, bool canFallBack) =>
        memory.Total <= 0 || memory.Free >= memory.Needed ? GpuMemoryDecision.Load
        : !memory.CanFree || canFallBack ? GpuMemoryDecision.Refuse : GpuMemoryDecision.LoadWithWarning;

    // Who holds the memory is unknown: another program, or Private Flow's own notes model.
    public static string GpuMemoryShortMessage(string model, GpuMemory memory) =>
        $"Not enough free GPU memory for {model}: {Gigabytes(memory.Free)} of {Gigabytes(memory.Total)} GB free, about {Gigabytes(memory.Needed)} GB needed. " +
        (memory.CanFree ? "Other GPU work, such as a game, a local language model or meeting notes being written, is using the rest." : "The GPU has too little memory for this model.");

    // The step a final pass shows while it waits for GPU memory, for the meeting notes being written on the GPU or for other work.
    public static string MemoryWaitStep(string model, GpuMemory memory, bool forNotes)
    {
        var amounts = $"{Gigabytes(memory.Free)} of {Gigabytes(memory.Total)} GB free, about {Gigabytes(memory.Needed)} GB needed";
        return forNotes ? $"Waiting for the meeting notes being written on the GPU to finish ({amounts} for {model})"
            : $"Waiting for GPU memory for {model}: {amounts}; other GPU work is using the rest";
    }
    // Shown while a dictation's speech model loads into too little free GPU memory, which makes the load and recognition slower.
    public static string GpuMemoryLowLoading(GpuMemory memory) =>
        $"GPU memory is low ({Gigabytes(memory.Free)} of {Gigabytes(memory.Total)} GB free, about {Gigabytes(memory.Needed)} GB needed), so the speech model loads slowly";
    public static string GpuMemoryLowNote(GpuMemory memory) =>
        $"GPU memory was low ({Gigabytes(memory.Free)} of {Gigabytes(memory.Total)} GB free, about {Gigabytes(memory.Needed)} GB needed), so recognition may have been slower.";

    // How long one request to the speech worker may take before the worker is stopped as hung. Only a final pass attempt that another
    // model can still finish (otherModelLeft: the meeting model, before the speech model) gets a shorter limit, so a pass slowed down by
    // other GPU work tries once more and then finishes with the speech model sooner. Every other request, such as a dictation's or the
    // last model's, keeps five minutes, because stopping it leaves no text: a dictation whose model loaded into too little GPU memory
    // runs many times slower and still finishes. Whisper encodes a whole 30 s window however short the request (AudioContext is not set)
    // and decodes in step with the audio, and both take longer the larger the model, so the shorter limit grows with the model file times
    // the window plus the audio. At full speed this took about 0.2 s per GB of model and second on a fast processor (20 s of speech:
    // medium 14 s, base 1.6 s) and about 0.013 s on an 8 GB NVIDIA GPU (large-v3 with eight beams decoded 15.5 s in 1.8 s). The processor
    // gets 100 times that, for slow processors, a busy PC (a test VM running a CI job took six times as long for a desktop step: 101.9 s)
    // and the eight-beam final pass; base gets 1.5 to 3 minutes, medium and larger models the full five. The GPU gets 25 times that: about
    // a minute for 30 s with large-v3, whose decode other work holding the GPU memory once slowed to 170 s, so such a pass falls back after
    // about a minute instead of five. Every request gets at least half a minute and at most five minutes.
    public static TimeSpan SpeechRequestLimit(double audioSeconds, long modelBytes, bool onGpu, bool otherModelLeft)
    {
        if (!otherModelLeft) return TimeSpan.FromMinutes(5);
        const double WindowSeconds = 30, ProcessorSecondsPerGigabyte = 20, GpuSecondsPerGigabyte = 0.33;
        var seconds = (onGpu ? GpuSecondsPerGigabyte : ProcessorSecondsPerGigabyte) * modelBytes / 1e9 * (WindowSeconds + Math.Max(0, audioSeconds));
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 30, 300));
    }

    public static string Gigabytes(long bytes) => (bytes / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    // whisper.cpp's DLLs need the Microsoft Visual C++ 2015-2022 runtime (x64), which a fresh Windows lacks; Private Flow does not bring
    // it. It is installed when its documented registry value Installed is 1 or its three DLLs are in System32.
    public const string VisualCppDownload = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    public const string VisualCppRuntimeMissing = "Speech recognition needs the Microsoft Visual C++ Redistributable 2015-2022 (x64), which this PC does not have. "
        + "Install it from Microsoft, " + VisualCppDownload + ", then try again.";
    public static bool VisualCppRuntimePresent(object? registryInstalled, bool filesPresent) => registryInstalled is 1 || filesPresent;
    // ERROR_MOD_NOT_FOUND (126) while loading the runtime: a DLL it needs is missing, on a fresh Windows the Visual C++ runtime.
    public static string LoadFailedMessage(string file, int win32Error, string systemMessage, bool visualCppPresent) =>
        win32Error == 126 && !visualCppPresent ? VisualCppRuntimeMissing : $"Could not load {file} from the speech runtime: {systemMessage}";

    public static string BlockedMessage(string file) =>
        $"Smart App Control blocked the local speech runtime while loading {file}. Windows rates unsigned files by cloud reputation, " +
        "which can change; try again later. See README, Smart App Control.";
}

public enum GpuMemoryDecision { Load, LoadWithWarning, Refuse }
