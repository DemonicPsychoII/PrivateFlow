namespace PrivateFlow.Core;

// A muted microphone, a disabled input channel or a wrong interface input delivers digital
// zeros; dictation then ends with "No text recognized" after the user has spoken for minutes.
// Warn once, early. Room noise on a working microphone stays far above the threshold, and
// pauses in speech are not silence at this level.
public sealed class SilenceWatch(int levelsPerSecond = 40, double seconds = 3, float threshold = SilenceWatch.Threshold)
{
    // About -74 dBFS: below the noise floor of any working microphone.
    public const float Threshold = 0.0002f;
    private readonly int needed = (int)(levelsPerSecond * seconds);
    private int seen;
    private float peak;
    private bool warned;
    // Feeds one RMS level; true exactly once, when the first window held no audible input.
    public bool Add(float level)
    {
        if (warned || seen >= needed) return false;
        peak = Math.Max(peak, level);
        if (++seen < needed || peak >= threshold) return false;
        return warned = true;
    }
}

// A meeting microphone that delivers digital or near silence. Unlike dictation, a meeting has long stretches in which the user
// only listens, but a working microphone still carries room noise then. Silent while the level stayed below the threshold for
// the given time, sounding again at the first audible level. The warning is given once per meeting, so muting the microphone on
// purpose, again and again, is not announced each time.
public sealed class SilenceMonitor(int levelsPerSecond = 40, double seconds = 10, float threshold = SilenceWatch.Threshold)
{
    private readonly int needed = (int)(levelsPerSecond * seconds);
    private int quiet;
    private bool warned;
    public bool Silent => quiet >= needed;
    public double SilentSeconds => quiet / (double)levelsPerSecond;
    // Feeds one RMS level; true exactly once per meeting, when the channel first becomes silent.
    public bool Add(float level)
    {
        if (level >= threshold) { quiet = 0; return false; }
        if (quiet < int.MaxValue) quiet++;
        if (quiet != needed || warned) return false;
        return warned = true;
    }
}

// Targets read the clipboard asynchronously after Ctrl+V; restoring the previous clipboard too
// early pastes the old content. Office reads late, and remote-desktop clients first copy the
// clipboard to the remote session. The restore is skipped anyway once anyone copies again.
public static class PasteTiming
{
    private static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
    { "outlook", "olk", "winword", "excel", "powerpnt", "onenote", "ms-teams", "teams" };
    private static readonly HashSet<string> Remote = new(StringComparer.OrdinalIgnoreCase)
    { "mstsc", "msrdc", "wfica32", "cdviewer", "vmconnect", "vmware-view", "remoteviewer", "anydesk", "teamviewer" };
    public static TimeSpan RestoreDelay(string? processName) =>
        processName is null ? TimeSpan.FromMilliseconds(400)
        : Remote.Contains(processName) ? TimeSpan.FromSeconds(5)
        : Office.Contains(processName) ? TimeSpan.FromSeconds(3)
        : TimeSpan.FromMilliseconds(400);
}

// The recent loudness of one channel for a waveform. Levels arrive in bursts with each audio buffer and are released one per
// step, so the newest sits at distance 0 and older ones move outwards.
public sealed class LevelRipple(int length)
{
    // Each recorder level covers 25 ms.
    public const double Step = .025;
    // A channel that stops delivering, such as loopback while nothing plays, falls flat after 100 ms instead of freezing on its
    // last level.
    private const int PatienceSteps = 4;
    private readonly double[] history = new double[length];
    private readonly Queue<double> pending = new();
    private double due;
    private int idleSteps;
    public double this[int distance] => history[distance];
    public void Push(float rms) => pending.Enqueue(Loudness(rms));
    public void Advance(double seconds)
    {
        // Drop a backlog after a stalled frame so the bars never lag the voice.
        while (pending.Count > 4) pending.Dequeue();
        for (due += seconds; due >= Step; due -= Step)
        {
            if (pending.Count > 0) { idleSteps = 0; Shift(pending.Dequeue()); }
            else if (++idleSteps > PatienceSteps) Shift(0);
        }
    }
    private void Shift(double level)
    {
        Array.Copy(history, 0, history, 1, history.Length - 1);
        history[0] = level;
    }
    // Decibels match how loud speech sounds: about -50 dBFS (room noise) to -12 dBFS (close,
    // loud voice) spans the bar, so quiet and loud syllables differ instead of both clipping.
    public static double Loudness(float rms) => Math.Clamp((20 * Math.Log10(Math.Max(rms, 1e-6)) + 50) / 38, 0, 1);
}

// One bar of a waveform moving towards its level: it rises within ~15 ms and falls within ~80 ms, as the dictation pill does.
public static class WaveBars
{
    // Below a thousandth of the height no bar moves by a visible amount, so the bar takes its goal and stops moving.
    private const double Settled = .001;
    // True when the bar moved, so a wave whose bars all rest needs no redraw.
    public static bool Approach(double[] heights, int index, double goal, double seconds)
    {
        var height = heights[index];
        if (height == goal) return false;
        height += (goal - height) * (1 - Math.Exp(-seconds * (goal > height ? 65 : 12)));
        heights[index] = Math.Abs(goal - height) < Settled ? goal : height;
        return true;
    }
}

// Colours offered for the system-audio half of the meeting wave; the microphone half is always white.
public static class SystemAudioColors
{
    public const string Default = "#3B6FF5";
    public static IReadOnlyList<(string Name, string Hex)> Presets { get; } =
        [("Blue", Default), ("Cyan", "#22C3E6"), ("Green", "#3FB950"), ("Yellow", "#E3B341"), ("Orange", "#F0883E"), ("Red", "#F0524F"), ("Pink", "#DB61A2"), ("Purple", "#A371F7")];
    // A colour outside the presets, such as one edited into settings.json by hand, falls back to the default.
    public static string Normalize(string? hex) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Hex, hex?.Trim(), StringComparison.OrdinalIgnoreCase)).Hex ?? Default;
}
