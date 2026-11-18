using PrivateFlow.Core;
using static Harness;

static class AudioChecks
{
    public static void VerifySilenceAndLevels()
    {
        // A muted microphone warns once after three seconds; room noise and speech pauses do not warn.
        var muted = new SilenceWatch();
        Check(Enumerable.Range(0, 119).All(_ => !muted.Add(0f)) && muted.Add(0f) && !muted.Add(0f), "digital silence warns once after three seconds");
        var quiet = new SilenceWatch();
        Check(!Enumerable.Range(0, 400).Select(i => quiet.Add(i % 50 == 0 ? 0.001f : 0f)).Any(w => w), "one audible level in the first window prevents the warning");
        // A meeting microphone: silent after ten seconds below the threshold, sounding again at once, announced only the first time.
        var meetingMicrophone = new SilenceMonitor();
        var firstSilence = Enumerable.Range(0, 400).Select(_ => meetingMicrophone.Add(0f)).ToArray();
        Check(firstSilence.Count(warned => warned) == 1 && firstSilence[^1] && !firstSilence[^2] && meetingMicrophone.Silent && meetingMicrophone.SilentSeconds == 10, "a meeting microphone delivering zeros is silent after ten seconds and warned about once");
        Check(!meetingMicrophone.Add(0.01f) && !meetingMicrophone.Silent, "the silence ends at the first audible level");
        Check(!Enumerable.Range(0, 2000).Select(_ => meetingMicrophone.Add(0.00005f)).Any(warned => warned) && meetingMicrophone.Silent && meetingMicrophone.SilentSeconds == 50,
            "muting the microphone again shows it as silent but does not warn a second time");
        var talking = new SilenceMonitor();
        Check(!Enumerable.Range(0, 4000).Select(i => talking.Add(i % 200 == 0 ? 0.002f : 0.0001f)).Any(warned => warned) && !talking.Silent,
            "a word every five seconds keeps a meeting microphone from counting as silent");
        // The meeting wave: one level per 25 ms step, newest in the middle, flat once a channel delivers nothing.
        var ripple = new LevelRipple(9);
        ripple.Push(0.1f); ripple.Push(0.001f); ripple.Advance(LevelRipple.Step);
        Check(ripple[0] == LevelRipple.Loudness(0.1f) && ripple[1] == 0, "the wave shows one level per step, the oldest first");
        ripple.Advance(LevelRipple.Step);
        Check(ripple[0] == LevelRipple.Loudness(0.001f) && ripple[1] == LevelRipple.Loudness(0.1f), "older levels move outwards");
        for (var step = 0; step < 5; step++) ripple.Advance(LevelRipple.Step);
        Check(ripple[0] == 0 && ripple[1] == LevelRipple.Loudness(0.001f), "a channel that stops delivering falls flat instead of freezing on its last level");
        var backlog = new LevelRipple(9);
        for (var i = 0; i < 40; i++) backlog.Push(0.1f);
        backlog.Push(0.2f);
        for (var step = 0; step < 10; step++) backlog.Advance(LevelRipple.Step);
        Check(backlog[0] == 0 && backlog[1] == 0 && backlog[2] == LevelRipple.Loudness(0.2f), "a backlog after a stalled frame is dropped so the wave never lags");
        Check(LevelRipple.Loudness(0f) == 0 && LevelRipple.Loudness(1f) == 1 && Math.Abs(LevelRipple.Loudness(0.0316f) - 20 / 38.0) < 0.01, "loudness spans -50 to -12 dBFS");
        Check(SystemAudioColors.Presets.Count == 8 && SystemAudioColors.Presets[0].Hex == "#3B6FF5" && SystemAudioColors.Presets.Select(preset => preset.Hex).Distinct().Count() == 8
            && SystemAudioColors.Normalize(" #f0524f ") == "#F0524F" && SystemAudioColors.Normalize("red") == SystemAudioColors.Default, "eight distinct preset colours with the accent blue first; anything else falls back to it");
    }

    public static void VerifySpeechSegmentation()
    {
        float[] Probabilities(int frames, params (int First, int End)[] speech) => Enumerable.Range(0, frames).Select(frame => speech.Any(span => frame >= span.First && frame < span.End) ? 0.9f : 0.05f).ToArray();
        var regions = SpeechSegmentation.Regions(Probabilities(200, (10, 40), (60, 100), (150, 152)), 200 * SpeechSegmentation.FrameSeconds);
        Check(regions.Count == 2 && Math.Abs(regions[0].Start - (10 * 0.032 - 0.2)) < 1e-9 && Math.Abs(regions[0].End - (40 * 0.032 + 0.2)) < 1e-9 && Math.Abs(regions[1].Start - (60 * 0.032 - 0.2)) < 1e-9,
            "speech regions start and end at the detector's pauses with 200 ms padding; a 64 ms blip is not speech");
        var touching = SpeechSegmentation.Regions(Probabilities(100, (10, 40), (45, 80)), 100 * SpeechSegmentation.FrameSeconds);
        Check(touching.Count == 2 && touching[0].End == touching[1].Start && touching[0].End > 40 * 0.032 && touching[1].Start < 45 * 0.032, "padding of close regions meets halfway instead of overlapping");
        Check(SpeechSegmentation.Regions(Probabilities(100, (0, 100)), 3.2).Single() is { Start: 0 } whole && Math.Abs(whole.End - 3.2) < 1e-9, "speech to the edges stays inside the recording");
        var continuous = SpeechSegmentation.Regions(Probabilities(2000, (5, 1995)), 64);
        Check(continuous.Count == 3 && continuous.All(region => region.Seconds <= 25.2) && continuous.Zip(continuous.Skip(1)).All(pair => Math.Abs(pair.First.End - pair.Second.Start) < 1e-9), "continuous speech is cut into adjacent pieces of at most one window");
        var pausing = Probabilities(1000, (0, 1000)); pausing[700] = 0.3f;
        Check(SpeechSegmentation.Regions(pausing, 32)[0] is var cut && Math.Abs(cut.End - 700 * 0.032) < 1e-9, "a long stretch is cut at its least speech-like frame");
        static SpeechPiece Piece(double start, double end, double english) => new(start, end, new Dictionary<string, double> { ["en"] = english, ["es"] = 1 - english });
        var pieces = new[] { Piece(0, 3, 0.9), Piece(3.2, 3.6, 0.3), Piece(4, 7, 0.95), Piece(8, 11, 0.1) };
        Check(SpeechSegmentation.ChooseLanguages(pieces).SequenceEqual(["en", "es", "en", "es"]), "without a switch penalty each piece takes its most likely language");
        Check(SpeechSegmentation.ChooseLanguages(pieces, 1.0).SequenceEqual(["en", "en", "en", "es"]), "a short unsure piece follows its neighbours when switching costs");
        var runs = SpeechSegmentation.Runs(pieces, ["en", "en", "en", "es"]);
        Check(runs.Count == 2 && runs[0] is { Start: 0, End: 7, Language: "en", FirstPiece: 0, PieceCount: 3 } && runs[1] is { Start: 8, Language: "es" }, "neighbouring pieces in one language are decoded together");
        Check(SpeechSegmentation.Runs([Piece(0, 3, 1), Piece(6, 9, 1), Piece(9.5, 32, 1)], ["en", "en", "en"]).Count == 3, "a long pause or the window length starts a new run");
        var pausedTone = Enumerable.Range(0, 320000).Select(sample => sample is >= 300000 and < 304000 ? 0f : (float)Math.Sin(sample * 0.05) * 0.3f).ToArray();
        Check(SpeechSegmentation.CutPoint(pausedTone, 224000, 320000) is >= 300000 and < 304000, "a window is cut in the quietest 200 ms of its last seconds");
        Check(SpeechSegmentation.RecordingWindows(pausedTone, 320000, 96000) is [(0, var first), (var second, var rest)] && first is >= 300000 and < 304000 && second == first && rest == 320000 - first, "recording windows join without gaps or overlap");
        var soft = Enumerable.Range(0, 16000).Select(sample => (float)Math.Sin(sample * 0.05) * 0.01f).ToArray();
        Check(Math.Abs(SpeechSegmentation.Gain(soft) * 0.01 / Math.Sqrt(2) - 0.1) < 0.01 && SpeechSegmentation.Gain(new float[1600]) == 1, "quiet speech is raised to the reference level; silence is left alone");
        Check(SpeechSegmentation.Gain(soft.Select(sample => sample * 0.01f).ToArray()) == 100, "the gain stays bounded");
        Check(Math.Abs(SpeechSegmentation.Level(soft) - 20 * Math.Log10(0.01 / Math.Sqrt(2))) < 0.1 && SpeechSegmentation.Level(new float[1600]) == -120, "the active level of a tone is its RMS in dBFS; silence is -120 dBFS");
    }

    public static void VerifyEchoRemoval()
    {
        static (TranscriptSegment Segment, double Level) Heard(double start, double end, string text, double level) => (new(start, end, text), level);
        (TranscriptSegment Segment, double Level)[] ownSpeech = [Heard(0, 3, "Okay, let us start the episode again.", -20), Heard(4, 6, "I will write down the times.", -21)];
        bool KeptAsSaid(TranscriptSegment[] system, (TranscriptSegment Segment, double Level) microphone) => SpeechSegmentation.Echoes([.. ownSpeech, microphone], system).Count == 0;
        TranscriptSegment[] meetTomorrow = [new(10, 13, "So we should meet tomorrow at 12:30.")];
        Check(KeptAsSaid(meetTomorrow, Heard(13.4, 14.4, "At 12:30?", -20)) && KeptAsSaid(meetTomorrow, Heard(13.4, 14.4, "Tomorrow at 12:30, okay.", -20)) && KeptAsSaid(meetTomorrow, Heard(13.4, 14.4, "12:30 tomorrow, yes.", -22))
            && KeptAsSaid([new(20, 23, "Can you send me the report by Friday?")], Heard(23.3, 24.3, "By Friday, okay.", -20))
            && KeptAsSaid([new(30, 33.5, "Bueno, la reunión empieza a las siete y cuarenta.")], Heard(33.8, 35.6, "A las siete y cuarenta, vale.", -20)),
            "a reply on the microphone that repeats what the system channel just said is kept, in English and Spanish");
        Check(KeptAsSaid(meetTomorrow, Heard(13.4, 14.4, "Tomorrow at 12:30, okay.", -36)), "a quiet reply is kept too: it comes after the system channel, an echo comes at the same moment");
        Check(KeptAsSaid(meetTomorrow, Heard(10, 13, "So we should meet tomorrow at 12:30.", -21)), "with headphones, words said at the same moment at the person's own level are not echo");
        TranscriptSegment[] rain = [new(9.5, 14, "Heavy rain is expected in the north tonight, with up to 40 millimetres")];
        Check(SpeechSegmentation.Echoes([.. ownSpeech, Heard(10, 13, "Heavy rain is expected in the north tonight", -45)], rain).SetEquals([2])
            && SpeechSegmentation.Echoes([Heard(10, 13, "Heavy rain is expected in the north tonight", -45)], rain).Count == 0
            && KeptAsSaid([new(30, 34, "Heavy rain is expected in the north tonight")], Heard(10, 13, "Heavy rain is expected in the north tonight", -45))
            && KeptAsSaid([new(10, 11, "twenty thousand euros")], Heard(10, 11, "Twenty thousand", -45)),
            "the show heard 25 dB below the person at the same moment is echo; without the person's speech, at another time or as a short remark it stays");
        Check(KeptAsSaid([new(56, 66, "In sport the final starts tomorrow at 8.15. Tickets sold out in 12 minutes. Now the weather for the weekend, the evening film is on.")],
            Heard(62.7, 66.3, "The meeting in the film starts at 12.20.", -45)), "a long system segment does not lend its distant words to a remark on the microphone");
        Check(SpeechLanguages.Parse(" ES, en,xx,en,auto") is ["es", "en"] && SpeechLanguages.Parse(null).Count == 0, "meeting languages keep offered languages once, in order");
    }
}
