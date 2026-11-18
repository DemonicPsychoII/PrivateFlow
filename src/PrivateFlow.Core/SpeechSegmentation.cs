namespace PrivateFlow.Core;

public sealed record SpeechSpan(double Start, double End)
{
    public double Seconds => End - Start;
}
// A stretch of speech between pauses with the probability of each allowed language.
public sealed record SpeechPiece(double Start, double End, IReadOnlyDictionary<string, double> Languages)
{
    public double Seconds => End - Start;
}
// Consecutive pieces decoded together in one language.
public sealed record SpeechRun(double Start, double End, string Language, int FirstPiece, int PieceCount);

// Cuts audio at pauses instead of a fixed clock, so no recognition window starts or ends inside a word.
public static class SpeechSegmentation
{
    public const int SampleRate = 16000, FrameSamples = 512;
    public const double FrameSeconds = FrameSamples / (double)SampleRate;

    // Silero's own decision rule: speech starts above the threshold and ends after a pause below threshold - 0.15.
    // Every region gets padding, split halfway where two regions are closer than twice the padding.
    public static IReadOnlyList<SpeechSpan> Regions(IReadOnlyList<float> probabilities, double seconds, double threshold = 0.5, double minimumSilence = 0.1,
        double minimumSpeech = 0.25, double padding = 0.2, double maximumSeconds = 25)
    {
        var raw = new List<(int First, int End)>();
        int? start = null; var lastSpeech = -1;
        var silenceFrames = Math.Max(1, (int)Math.Round(minimumSilence / FrameSeconds));
        for (var frame = 0; frame < probabilities.Count; frame++)
        {
            var probability = probabilities[frame];
            if (probability >= threshold) { start ??= frame; lastSpeech = frame; }
            else if (start is { } first && probability < threshold - 0.15 && frame - lastSpeech >= silenceFrames) { raw.Add((first, lastSpeech + 1)); start = null; }
        }
        if (start is { } open) raw.Add((open, lastSpeech + 1));
        var minimumFrames = minimumSpeech / FrameSeconds;
        var kept = raw.Where(region => region.End - region.First >= minimumFrames).SelectMany(region => SplitLong(probabilities, region.First, region.End, maximumSeconds)).ToList();
        var spans = new List<SpeechSpan>();
        for (var index = 0; index < kept.Count; index++)
        {
            var begin = kept[index].First * FrameSeconds; var end = kept[index].End * FrameSeconds;
            var lowest = index == 0 ? 0 : (kept[index - 1].End * FrameSeconds + begin) / 2;
            var highest = index + 1 < kept.Count ? (end + kept[index + 1].First * FrameSeconds) / 2 : seconds;
            spans.Add(new(Math.Max(lowest, begin - padding), Math.Min(highest, Math.Min(seconds, end + padding))));
        }
        return spans;
    }
    // A region longer than one recognition window is cut at its least speech-like frame in the last third, the latest of equals.
    private static IEnumerable<(int First, int End)> SplitLong(IReadOnlyList<float> probabilities, int first, int end, double maximumSeconds)
    {
        var maximum = (int)(maximumSeconds / FrameSeconds);
        while (end - first > maximum)
        {
            var from = first + maximum * 2 / 3; var to = first + maximum;
            var cut = Enumerable.Range(from, to - from).Aggregate((best, frame) => probabilities[frame] <= probabilities[best] ? frame : best);
            yield return (first, cut); first = cut;
        }
        yield return (first, end);
    }
    public static float[] SpeechOnly(float[] samples, IReadOnlyList<SpeechSpan> regions) =>
        regions.SelectMany(region => samples[Index(region.Start, samples.Length)..Index(region.End, samples.Length)]).ToArray();
    public static int Index(double seconds, int length) => Math.Clamp((int)Math.Round(seconds * SampleRate), 0, length);

    // Scales to speech at -20 dBFS active level, like the reference recordings Whisper was trained on, so a quiet
    // remark is not decoded at the edge of the log-mel range. Frames more than 30 dB below the loudest are not speech.
    public static float Gain(ReadOnlySpan<float> samples, double maximumGain = 100)
    {
        var active = ActivePower(samples);
        if (active <= 1e-12) return 1;
        var peak = 0f; foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return (float)Math.Min(Math.Min(maximumGain, 0.1 / Math.Sqrt(active)), 0.99 / peak);
    }
    // Active level in dBFS, over the frames Gain counts as sound.
    public static double Level(ReadOnlySpan<float> samples) => 10 * Math.Log10(Math.Max(1e-12, ActivePower(samples)));
    private static double ActivePower(ReadOnlySpan<float> samples)
    {
        const int frame = 320;
        var powers = new List<double>();
        for (var first = 0; first + frame <= samples.Length; first += frame)
        {
            double sum = 0; foreach (var sample in samples.Slice(first, frame)) sum += sample * sample;
            powers.Add(sum / frame);
        }
        if (powers.Count == 0 || powers.Max() <= 1e-12) return 0;
        var loudest = powers.Max();
        return powers.Where(power => power >= loudest / 1000).Average();
    }

    // The quietest 200 ms between earliest and latest (sample indices), measured in 10 ms steps; the cut lies at its centre.
    public static int CutPoint(ReadOnlySpan<float> samples, int earliest, int latest)
    {
        const int step = 160, width = 20;
        latest = Math.Min(latest, samples.Length); earliest = Math.Clamp(earliest, 0, latest);
        var steps = (latest - earliest) / step;
        if (steps <= width) return latest;
        var energy = new double[steps];
        for (var index = 0; index < steps; index++) { double sum = 0; foreach (var sample in samples.Slice(earliest + index * step, step)) sum += sample * sample; energy[index] = sum; }
        double window = 0; for (var index = 0; index < width; index++) window += energy[index];
        var best = 0; var lowest = window;
        for (var index = width; index < steps; index++)
        {
            window += energy[index] - energy[index - width];
            // Later cuts win ties, so silence gives full windows.
            if (window <= lowest) { lowest = window; best = index - width + 1; }
        }
        return earliest + (best + width / 2) * step;
    }
    // The windows a recording is cut into while it is captured: at most maximum samples, cut at the quietest point of the
    // last search samples. A remainder must hold at least 10 ms.
    public static IReadOnlyList<(int Start, int Length)> RecordingWindows(float[] samples, int maximum, int search)
    {
        var windows = new List<(int Start, int Length)>(); var start = 0;
        while (samples.Length - start >= maximum)
        {
            var cut = CutPoint(samples.AsSpan(start, maximum), maximum - search, maximum);
            windows.Add((start, cut)); start += cut;
        }
        if (samples.Length - start >= 160) windows.Add((start, samples.Length - start));
        return windows;
    }

    // Chooses one allowed language per piece. A switch between neighbouring pieces costs switchPenalty, and a piece's own
    // evidence counts in proportion to its length up to one second, so a one-word remark follows its neighbours
    // unless Whisper is sure about it.
    public static IReadOnlyList<string> ChooseLanguages(IReadOnlyList<SpeechPiece> pieces, double switchPenalty = 0)
    {
        if (pieces.Count == 0) return [];
        var languages = pieces[0].Languages.Keys.ToArray();
        double Cost(int piece, int language) => -Math.Min(1, pieces[piece].Seconds) * Math.Log(Math.Max(1e-6, pieces[piece].Languages.GetValueOrDefault(languages[language])));
        var cost = languages.Select((_, language) => Cost(0, language)).ToArray();
        var from = new int[pieces.Count, languages.Length];
        for (var piece = 1; piece < pieces.Count; piece++)
        {
            var next = new double[languages.Length];
            for (var language = 0; language < languages.Length; language++)
            {
                var best = 0; var lowest = double.MaxValue;
                for (var previous = 0; previous < languages.Length; previous++)
                {
                    var total = cost[previous] + (previous == language ? 0 : switchPenalty);
                    if (total < lowest) { lowest = total; best = previous; }
                }
                next[language] = lowest + Cost(piece, language); from[piece, language] = best;
            }
            cost = next;
        }
        var chosen = new string[pieces.Count];
        var state = Array.IndexOf(cost, cost.Min());
        for (var piece = pieces.Count - 1; piece >= 0; piece--) { chosen[piece] = languages[state]; if (piece > 0) state = from[piece, state]; }
        return chosen;
    }
    // Neighbouring pieces in the same language form one run up to maximumSeconds, unless a pause longer than maximumGap separates them.
    public static IReadOnlyList<SpeechRun> Runs(IReadOnlyList<SpeechPiece> pieces, IReadOnlyList<string> languages, double maximumGap = 2, double maximumSeconds = 25)
    {
        var runs = new List<SpeechRun>();
        for (var piece = 0; piece < pieces.Count; piece++)
        {
            if (runs.Count > 0 && runs[^1] is var last && last.Language == languages[piece] && pieces[piece].Start - last.End <= maximumGap && pieces[piece].End - last.Start <= maximumSeconds)
                runs[^1] = last with { End = pieces[piece].End, PieceCount = last.PieceCount + 1 };
            else runs.Add(new(pieces[piece].Start, pieces[piece].End, languages[piece], piece, 1));
        }
        return runs;
    }

    // Show audio that the microphone hears from the loudspeakers. An echo is said at the same moment as on the system channel
    // (loudspeaker to microphone takes milliseconds) and is quieter than the person at the microphone; a reply comes after it, at
    // that person's level. So a microphone segment is echo only with both kinds of evidence: at least three words, most of them
    // said on the system channel while the segment lasts, and a level at least quieterBy dB below the microphone's speech (the
    // loudest tenth of its segments). Without both, as with headphones or when nobody spoke into the microphone, the segment stays:
    // a duplicated show line costs less than a lost reply. Microphone segments come with their level in dBFS (Level).
    public static IReadOnlySet<int> Echoes(IReadOnlyList<(TranscriptSegment Segment, double Level)> microphone, IReadOnlyList<TranscriptSegment> system,
        double share = 0.6, double tolerance = 0.5, double quieterBy = 10)
    {
        if (microphone.Count == 0) return new HashSet<int>();
        var levels = microphone.Select(entry => entry.Level).Order().ToArray();
        var speech = levels[(int)Math.Ceiling(0.9 * levels.Length) - 1];
        return Enumerable.Range(0, microphone.Count)
            .Where(index => microphone[index].Level <= speech - quieterBy && SaysSystemWords(microphone[index].Segment, system, share, tolerance)).ToHashSet();
    }
    // System words take times spread evenly over their segment, so a long system segment does not lend the microphone words said
    // many seconds away.
    private static bool SaysSystemWords(TranscriptSegment microphone, IEnumerable<TranscriptSegment> system, double share, double tolerance)
    {
        var words = TranscriptScoring.Words(microphone.Text);
        if (words.Count < 3) return false;
        var heard = system.Where(segment => segment.End >= microphone.Start - tolerance && segment.Start <= microphone.End + tolerance)
            .SelectMany(segment =>
            {
                var spoken = TranscriptScoring.Words(segment.Text);
                return spoken.Where((_, index) => segment.Start + (index + 0.5) / spoken.Count * (segment.End - segment.Start) is var time
                    && time >= microphone.Start - tolerance && time <= microphone.End + tolerance);
            }).ToHashSet();
        return words.Count(heard.Contains) >= share * words.Count;
    }
}
