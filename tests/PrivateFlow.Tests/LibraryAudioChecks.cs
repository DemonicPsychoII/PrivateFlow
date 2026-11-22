using PrivateFlow.Core;
using static Harness;

// The test build's audio library and the windows a microphone or a clip is cut into.
static class LibraryAudioChecks
{
    public static void VerifyAudioLibrary()
    {
        var library = Path.Combine(Path.GetTempPath(), "PrivateFlow-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Clip(string id, string manifest, string audio = "clip.wav")
            {
                var folder = Path.Combine(library, id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, AudioLibrary.ManifestName), manifest);
                if (audio.Length > 0) File.WriteAllBytes(Path.Combine(folder, audio), [0]);
                return folder;
            }
            Clip("b-meeting-time", """{ "schema": 1, "audio": "clip.wav", "language": "en", "reference": "We meet at [12:30], [not] on Friday.", "tags": ["technical", "quiet"], "source": "piper" }""");
            Clip("a-german", """{ "schema": 1, "audio": "clip.wav", "language": "de", "reference": "Bitte [nicht] löschen.", "expectedStyle": "clean", "expected": "Bitte nicht löschen." }""");
            Clip("c-noise", """{ "schema": 1, "audio": "clip.wav", "language": "es", "reference": "", "tags": ["noisy"] }""");
            Directory.CreateDirectory(Path.Combine(library, "not-a-clip"));
            var clips = AudioLibrary.Load(library);
            Check(clips.Select(clip => clip.Id).SequenceEqual(["a-german", "b-meeting-time", "c-noise"]), "a library lists its clips by folder name, and folders without a manifest are not clips");
            Check(clips[1] is { Language: "en", Source: "piper", ExpectedStyle: null } && clips[1].CriticalItems.SequenceEqual(["12:30", "not"]) && clips[1].Tags.SequenceEqual(["technical", "quiet"])
                && clips[1].AudioPath == Path.Combine(library, "b-meeting-time", "clip.wav"), "a clip carries its language, the critical items marked in its reference, its tags, its source and its audio file");
            Check(clips[0] is { ExpectedStyle: OutputStyle.Clean, ExpectedText: "Bitte nicht löschen.", Source: "" } && clips[2] is { Reference: "" } && clips[2].CriticalItems.Count == 0,
                "an expected style and text are read; a clip without speech has an empty reference and no critical items");
            foreach (var (id, manifest, audio, reason) in new[]
            {
                ("broken", "{ schema", "clip.wav", "is not valid JSON"),
                ("old-schema", """{ "schema": 2, "audio": "clip.wav", "language": "en", "reference": "" }""", "clip.wav", "schema 2 is not 1"),
                ("french", """{ "schema": 1, "audio": "clip.wav", "language": "fr", "reference": "" }""", "clip.wav", "language must be en, de or es"),
                ("no-reference", """{ "schema": 1, "audio": "clip.wav", "language": "en" }""", "clip.wav", "reference is missing"),
                ("outside", """{ "schema": 1, "audio": "../b-meeting-time/clip.wav", "language": "en", "reference": "" }""", "", "audio must be a file name in the clip's folder"),
                ("rooted", """{ "schema": 1, "audio": "/tmp/clip.wav", "language": "en", "reference": "" }""", "", "audio must be a file name in the clip's folder"),
                ("missing-audio", """{ "schema": 1, "audio": "clip.wav", "language": "en", "reference": "" }""", "", "clip.wav is missing"),
                ("bad-tag", """{ "schema": 1, "audio": "clip.wav", "language": "en", "reference": "", "tags": ["Noisy"] }""", "clip.wav", "tags are lower-case words"),
                ("bad-style", """{ "schema": 1, "audio": "clip.wav", "language": "en", "reference": "", "expectedStyle": "7" }""", "clip.wav", "expectedStyle must be"),
                ("expected-alone", """{ "schema": 1, "audio": "clip.wav", "language": "en", "reference": "", "expected": "x" }""", "clip.wav", "expected needs an expectedStyle"),
            })
            {
                try { AudioLibrary.Read(Clip("damaged-" + id, manifest, audio)); Check(false, $"a damaged manifest ({id}) is refused"); }
                catch (InvalidDataException error) { Check(error.Message.StartsWith($"Clip damaged-{id}: ") && error.Message.Contains(reason), $"a damaged manifest ({id}) is refused with its clip and reason (got \"{error.Message}\")"); }
            }
            var empty = Path.Combine(library, "empty"); Directory.CreateDirectory(empty);
            try { AudioLibrary.Load(empty); Check(false, "a library without clips is refused"); }
            catch (InvalidDataException) { Check(true, "a library without clips is refused"); }

            string Draws(ClipPicker picker, int count) => string.Join(",", Enumerable.Range(0, count).Select(_ => picker.Next().Id));
            var first = Draws(new ClipPicker(clips, 42), 30);
            Check(first == Draws(new ClipPicker(clips, 42), 30) && first != Draws(new ClipPicker(clips, 43), 30) && clips.All(clip => first.Contains(clip.Id)),
                "the same seed draws the same clips in the same order, another seed others, and every clip comes up");
            var tagged = new ClipPicker(clips, 7);
            var plain = new ClipPicker(clips, 7);
            var taggedDraws = new[] { tagged.Next().Id, tagged.Next("noisy").Id, tagged.Next().Id };
            var plainDraws = new[] { plain.Next().Id, plain.Next().Id, plain.Next().Id };
            Check(taggedDraws[1] == "c-noise" && taggedDraws[0] == plainDraws[0] && taggedDraws[2] == plainDraws[2] && tagged.Drawn == 3,
                "a draw with a tag gives a clip with that tag and leaves the draws after it where they were");
            try { new ClipPicker(clips, 1).Next("long"); Check(false, "a tag no clip has is refused"); }
            catch (InvalidOperationException) { Check(true, "a tag no clip has is refused"); }
            var written = Path.Combine(library, "written");
            Directory.CreateDirectory(written);
            File.WriteAllBytes(Path.Combine(written, "audio.wav"), [0]);
            File.WriteAllText(Path.Combine(written, AudioLibrary.ManifestName), AudioLibrary.ManifestJson("audio.wav", "es", "Nos vemos a las [nueve].", ["code-switching"], "piper", OutputStyle.Clean, "Nos vemos a las [nueve]."));
            Check(AudioLibrary.Read(written) is { Language: "es", Source: "piper", ExpectedStyle: OutputStyle.Clean, ExpectedText: "Nos vemos a las [nueve]." } read && read.Tags.SequenceEqual(["code-switching"]) && read.CriticalItems.SequenceEqual(["nueve"]),
                "a manifest the library builder writes reads back unchanged");
            float[] tone = [.. Enumerable.Range(0, 16000).Select(sample => (float)Math.Sin(sample * 0.05) * 0.5f)];
            double Decibels(float[] samples) => 20 * Math.Log10(Math.Sqrt(samples.Average(sample => (double)sample * sample)));
            var quiet = LibraryVariants.Scaled(tone, -25);
            var noisy = LibraryVariants.WithNoise(new float[16000], -45, 3);
            var joined = LibraryVariants.Joined(tone, quiet, 0.5);
            Check(Math.Abs(Decibels(quiet) - Decibels(tone) + 25) < 0.01 && Math.Abs(Decibels(noisy) + 45) < 0.5 && noisy.SequenceEqual(LibraryVariants.WithNoise(new float[16000], -45, 3))
                && joined.Length == 40000 && joined.Take(16000).SequenceEqual(tone) && joined.Skip(16000).Take(8000).All(sample => sample == 0) && joined.Skip(24000).SequenceEqual(quiet),
                $"the harder versions of a clip: 25 dB quieter, room noise at -45 dBFS ({Decibels(noisy):0.0}), the same for the same seed, and two clips joined after a pause");
        }
        finally { Directory.Delete(library, true); }
    }

    public static async Task VerifyAudioWindows()
    {
        // 45 s of a tone that pauses for 300 ms every 4 s, as a microphone delivers it.
        var windows = new AudioWindows();
        var samples = Enumerable.Range(0, 45 * 16000).Select(sample => sample % 64000 < 4800 ? 0f : (float)Math.Sin(sample * 0.05) * 0.3f).ToArray();
        foreach (var sample in samples) windows.Add(sample);
        var loud = windows.TryReadLevel(out var level) && level > 0.1f;
        windows.Finish(null);
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in windows.Chunks.ReadAllAsync()) chunks.Add(chunk);
        var joined = chunks.SelectMany(chunk => chunk.Samples).ToArray();
        Check(chunks.Count == 3 && chunks.All(chunk => chunk.Samples.Length <= AudioWindows.ChunkSamples) && chunks.Zip(chunks.Skip(1)).All(pair => Math.Abs(pair.First.Offset + pair.First.Samples.Length / 16000.0 - pair.Second.Offset) < 1e-9)
            && joined.SequenceEqual(samples) && windows.Seconds == 45 && loud && windows.Stopped.IsCompletedSuccessfully,
            $"45 s of audio arrive as windows of at most 20 s that follow each other without a gap or an overlap, with levels every 25 ms ({chunks.Count} windows)");
        var pauses = chunks.Take(2).Select(chunk => (int)Math.Round((chunk.Offset * 16000 + chunk.Samples.Length) % 64000)).ToArray();
        Check(pauses.All(position => position < 4800), $"a window ends in a pause of the speech, never in the middle of a word (ends at {string.Join(", ", pauses)} samples into a 4 s cycle)");

        // A clip played as a microphone: never ahead of real time, then silence until the dictation stops.
        var clip = Enumerable.Range(0, 16000).Select(sample => (float)Math.Sin(sample * 0.05) * 0.3f).ToArray();
        using var source = new LibraryAudioSource(clip);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        source.Start();
        await Task.Delay(500);
        var early = source.Seconds; var earlyClock = clock.Elapsed.TotalSeconds;
        var heard = false;
        while (source.TryReadLevel(out var playing)) heard |= playing > 0.1f;
        await Task.Delay(1000);
        await source.StopAsync();
        var played = source.Seconds; var playedClock = clock.Elapsed.TotalSeconds;
        var delivered = new List<float>();
        await foreach (var chunk in source.Chunks.ReadAllAsync()) delivered.AddRange(chunk.Samples);
        Check(early <= earlyClock + 0.02 && played <= playedClock + 0.02 && played >= 1.2 && heard,
            $"a clip plays in real time with its levels: {early:0.00} s after {earlyClock:0.00} s, {played:0.00} s after {playedClock:0.00} s");
        Check(delivered.Count == (int)Math.Round(played * 16000) && delivered.Take(clip.Length).SequenceEqual(clip) && delivered.Skip(clip.Length).All(sample => sample == 0f),
            "the windows hold the clip and then silence until the stop, as a microphone after the speaker has finished");
        // Keep dictations as test clips: the windows of one dictation put back together, nothing at all past ten minutes.
        var kept = new KeptAudio();
        Check(kept.Samples is null, "nothing is kept before a dictation delivers audio");
        kept.Add([0.1f, 0.2f]); kept.Add([0.3f]);
        Check(kept.Samples is [0.1f, 0.2f, 0.3f], "a kept dictation is its windows in order");
        var tooLong = new KeptAudio();
        tooLong.Add(new float[KeptAudio.MaximumSamples]); var atLimit = tooLong.Samples?.Length;
        tooLong.Add([0.5f]); tooLong.Add([0.5f]);
        Check(atLimit == KeptAudio.MaximumSamples && tooLong.Samples is null, "ten minutes are kept; a longer dictation keeps nothing, not its first ten minutes");
    }
}
