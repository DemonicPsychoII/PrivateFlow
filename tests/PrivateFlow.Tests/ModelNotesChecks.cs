using PrivateFlow.Core;
using static Fixtures;
using static Harness;

static class ModelNotesChecks
{
    public static async Task VerifyModelNotes()
    {
        // Model-written meeting notes: only points citing rows of the transcript survive, and every failure shows the extractive notes with its reason.
        {
            var planning = new MeetingDocument { Title = "Planning", Languages = "en,es", Vocabulary = "Lumen",
                Segments = [new() { Start = 3725, Speaker = "Ben", Text = "I will send the report by 12:30." }, new() { Start = 5.4, Speaker = "Ana", Text = "We decided to ship Lumen on Friday." },
                    new() { Start = 5.9, Speaker = "Remote speaker", Text = "Agreed." }, new() { Start = 65, Speaker = "Carla", Text = "¿Quién revisa el presupuesto?" }] };
            var request = ModelNotes.Request(planning);
            Check(planning.Segments.All(segment => request.Contains($"[{ModelNotes.Timestamp(segment)}] {segment.Speaker}: {segment.Text}\n")) && request.IndexOf("[01:05] Carla:") < request.IndexOf("[62:05] Ben:")
                && request.Contains("Languages spoken: English, Spanish") && request.Contains("Vocabulary: Lumen"), "the notes request contains every row in order with its timestamp, the languages and the vocabulary");
            string Citation(int index) => new EvidenceHit(planning.Id, planning.Title, planning.Segments[index], 0).Citation;
            var written = ModelNotes.Read("""
                Here are the notes:
                ```json
                {"summary": ["The team planned the Lumen release.", " ", "Ben reports at 12:30."],
                 "decisions": [{"text": "Ship Lumen on Friday.", "citations": ["[00:05]"]}, {"text": "Hire two people.", "citations": ["09:99"]}, {"text": "Invented.", "citations": ["10:00"]}],
                 "actionItems": [{"text": "Send the report.", "owner": "Ben", "due": "12:30", "citations": ["62:05"]}, {"text": "No source.", "citations": []}, null],
                 "openQuestions": [{"text": "Who reviews the budget?", "citations": ["1:05", "10:00"]}],
                 "timesMentioned": [{"time": "12:30", "text": "Report due.", "citations": ["62:05"]}]}
                ```
                """, planning, "test-model");
            Check(written.Dropped == 5 && written.Text.StartsWith("Written by the model test-model from the transcript. Check every point against the transcript before relying on it.\n5 points without a matching transcript row were left out.\n\nSummary\nThe team planned the Lumen release. Ben reports at 12:30.\n\n")
                && !new[] { "Hire two people", "Invented", "No source", "Who reviews" }.Any(written.Text.Contains), "points citing no row, an invented or malformed timestamp are dropped and counted");
            Check(written.Text.Contains($"Decisions\n- Ship Lumen on Friday. {Citation(1)} {Citation(2)}\n\n") && written.Text.Contains($"Action items — check owner and due date against the cited rows\n- Send the report. · Owner: Ben · Due: 12:30 {Citation(0)}\n\n")
                && written.Text.Contains("Open questions\nNone identified.\n\n") && written.Text.EndsWith($"Times mentioned\n- 12:30 · Report due. {Citation(0)}"), "kept points carry the app's citation of every row at their timestamp");
            void Unusable(string answer, string reason, string description)
            { try { ModelNotes.Read(answer, planning, "test-model"); Check(false, description); } catch (InvalidOperationException ex) { Same(ex.Message, reason, description); } }
            Unusable("I cannot help with that.", "The model's answer was not JSON.", "an answer without JSON is rejected");
            Unusable("{\"summary\": \"one string\"}", "The model's answer was not the requested JSON.", "JSON in another shape is rejected");
            Unusable("{\"summary\": [\"Made up.\"], \"decisions\": [{\"text\": \"Made up.\", \"citations\": [\"99:00\"]}], \"actionItems\": [], \"openQuestions\": [], \"timesMentioned\": []}", "None of the model's points cited a row of the transcript (1 dropped).", "an answer whose every point is unsupported is rejected");
            Unusable("{}", "The model's answer left out sections of the notes.", "an empty answer is rejected");
            Unusable("{\"summary\": [\"I cannot produce the requested notes.\"]}", "The model's answer left out sections of the notes.", "an answer with only a summary is rejected instead of passing as notes");
            var nothingAtAll = ModelNotes.Read("{\"summary\": [], \"decisions\": [], \"actionItems\": [], \"openQuestions\": [], \"timesMentioned\": []}", planning, "test-model");
            var summaryOnly = ModelNotes.Read("{\"summary\": [\"Sounds plausible.\"], \"decisions\": [], \"actionItems\": [], \"openQuestions\": [], \"timesMentioned\": []}", planning, "test-model");
            Check(nothingAtAll == new WrittenNotes("", 0, 0, true) && summaryOnly == new WrittenNotes("", 0, 0, true),
                "an answer that lists no point and drops none found nothing to note: a result, not a failure, and its unchecked summary alone is not kept as notes");
            Same(ModelNotes.Fallback(planning, "API timed out."), "Model-written notes unavailable: API timed out. Showing the extractive notes.\n\n" + MeetingArchive.Notes(planning), "the fallback is the extractive notes headed by the reason");
            var unsetKey = new PrivateFlow.Windows.Settings { MeetingNotesStyle = MeetingNotesStyle.Api, RewriteEndpoint = "https://example.invalid/v1", RewriteKeyVariable = "PRIVATE_FLOW_TEST_KEY_" + Guid.NewGuid().ToString("N") };
            var withoutKey = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(planning, unsetKey, CancellationToken.None);
            Check(!withoutKey.Written && withoutKey.FallbackReason == "API key variable is not set." && withoutKey.Text == ModelNotes.Fallback(planning, "API key variable is not set."), "without a key the notes writer returns the extractive notes and the reason instead of throwing");
            unsetKey.RewriteEndpoint = "";
            var withoutEndpoint = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(planning, unsetKey, CancellationToken.None);
            Check(!withoutEndpoint.Written && withoutEndpoint.FallbackReason!.StartsWith("No API base URL configured.") && withoutEndpoint.Text == ModelNotes.Fallback(planning, withoutEndpoint.FallbackReason), "without an endpoint API notes use the extractive fallback and explain why without making a request");
            var tooLong = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(new MeetingDocument { Segments = [new() { Text = new string('a', ModelNotes.MaximumTranscriptCharacters) }] }, unsetKey, CancellationToken.None);
            Check(!tooLong.Written && tooLong.FallbackReason!.StartsWith("The transcript is too long for one request") && tooLong.Text.Contains(ExtractiveNotes.Header), "a transcript too long for one request falls back before anything is sent");
            var empty = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(new MeetingDocument(), unsetKey, CancellationToken.None);
            Check(!empty.Written && empty.FallbackReason == "The meeting has no transcript yet.", "a meeting without transcript falls back before anything is sent");
            Check(withoutKey.Notice == MeetingArchive.ExtractiveNoticeText && tooLong.Notice == MeetingArchive.ExtractiveNoticeText && empty.Notice is null, "a fallback to the extractive notes carries the notice unless there is no transcript");
            var extractive = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(planning, new PrivateFlow.Windows.Settings(), CancellationToken.None);
            Check(extractive == new PrivateFlow.Windows.MeetingNotesResult(MeetingArchive.Notes(planning), false, 0, null, MeetingArchive.ExtractiveNoticeText)
                && (await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(new MeetingDocument(), new PrivateFlow.Windows.Settings(), CancellationToken.None)).Notice is null,
                "the default style shows the extractive notes with the notice, without a fallback reason, and no notice before there is a transcript");
            var missingFolder = Path.Combine(Path.GetTempPath(), "PrivateFlow-missing-" + Guid.NewGuid().ToString("N"));
            var localNotInstalled = new PrivateFlow.Windows.Settings { MeetingNotesStyle = MeetingNotesStyle.LocalModel, MeetingNotesEnginePath = Path.Combine(missingFolder, "llama-server.exe"), MeetingNotesModelPath = Path.Combine(missingFolder, "model.gguf") };
            var localMissing = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(planning, localNotInstalled, CancellationToken.None);
            Check(!localMissing.Written && localMissing.FallbackReason == "The meeting notes model is not installed." && localMissing.Text == ModelNotes.Fallback(planning, localMissing.FallbackReason) && localMissing.Notice == MeetingArchive.ExtractiveNoticeText,
                "the local style without the notes model shows the extractive notes with the reason and the notice");
            Check(!PrivateFlow.Windows.MeetingNotesModel.IsInstalled(localNotInstalled), "the interface sees that no notes model is installed");
            Check(ModelNotes.UnsuitableLocalModel(new PrivateFlow.Windows.Settings().MeetingNotesModelPath, ModelCatalogue.Model(ModelCatalogue.NotesModel).Bytes) is null
                && PrivateFlow.Windows.MeetingNotesModel.DownloadBytes > ModelCatalogue.Model(ModelCatalogue.NotesModel).Bytes
                && ModelNotes.MaximumLocalTranscriptCharacters(new PrivateFlow.Windows.Settings().MeetingNotesContextTokens) >= 80_000,
                "the pinned notes model is not refused, its download includes the runtime, and its default context holds a meeting of more than an hour");
            var stockLocal = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(planning, new PrivateFlow.Windows.Settings { MeetingNotesStyle = MeetingNotesStyle.LocalModel,
                MeetingNotesEnginePath = Path.Combine(missingFolder, "llama-server.exe"), MeetingNotesModelPath = Path.Combine(missingFolder, "Qwen3-4B-Q4_K_M.gguf") }, CancellationToken.None);
            Check(!stockLocal.Written && stockLocal.FallbackReason == ModelNotes.UnsuitableLocalModel("Qwen3-4B-Q4_K_M.gguf", null) && stockLocal.FallbackReason!.StartsWith("The Local polish model (Qwen3-4B) is not used for meeting notes")
                && stockLocal.Text.StartsWith("Model-written notes unavailable: The Local polish model") && ModelNotes.UnsuitableLocalModel(Path.Combine(missingFolder, "Qwen3-14B-Q4_K_M.gguf"), null) is null,
                "the model Local polish installs is refused for notes with the reason shown; another local model is tried");
            var renamedStock = Path.Combine(Path.GetTempPath(), "PrivateFlow-renamed-" + Guid.NewGuid().ToString("N") + ".gguf");
            using (var sparse = File.Create(renamedStock)) sparse.SetLength(ModelNotes.LocalPolishModelBytes);
            try
            {
                var renamed = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(planning, new PrivateFlow.Windows.Settings { MeetingNotesStyle = MeetingNotesStyle.LocalModel,
                    MeetingNotesEnginePath = Path.Combine(missingFolder, "llama-server.exe"), MeetingNotesModelPath = renamedStock }, CancellationToken.None);
                Check(!renamed.Written && renamed.FallbackReason == ModelNotes.UnsuitableLocalModel("", ModelNotes.LocalPolishModelBytes) && ModelNotes.UnsuitableLocalModel(renamedStock, ModelNotes.LocalPolishModelBytes + 1) is null,
                    "a renamed copy of the Local polish model is refused by the size of the pinned download");
            }
            finally { File.Delete(renamedStock); }
            using (var schema = System.Text.Json.JsonDocument.Parse(ModelNotes.AnswerSchema))
                Check(schema.RootElement.GetProperty("required").EnumerateArray().Select(name => name.GetString()).SequenceEqual(["summary", "decisions", "actionItems", "openQuestions", "timesMentioned"])
                    && System.Text.RegularExpressions.Regex.IsMatch("62:05", schema.RootElement.GetProperty("$defs").GetProperty("citations").GetProperty("items").GetProperty("pattern").GetString()!)
                    && !System.Text.RegularExpressions.Regex.IsMatch("Ben: 'I will send it.'", schema.RootElement.GetProperty("$defs").GetProperty("citations").GetProperty("items").GetProperty("pattern").GetString()!),
                    "the local answer schema requires every section the notes read and allows only timestamps as citations");
            var localLimit = ModelNotes.MaximumLocalTranscriptCharacters(localNotInstalled.MeetingNotesContextTokens);
            var localTooLong = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(new MeetingDocument { Segments = [new() { Text = new string('a', localLimit) }] }, localNotInstalled, CancellationToken.None);
            var localFits = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(new MeetingDocument { Segments = [new() { Text = new string('a', localLimit - 40) }] }, localNotInstalled, CancellationToken.None);
            Check(localTooLong.FallbackReason!.StartsWith("The transcript is too long for the local model") && localFits.FallbackReason == "The meeting notes model is not installed.",
                "a transcript beyond the notes model's context falls back before the model starts; one that fits goes on to the model");
            Check(ModelNotes.MaximumLocalTranscriptCharacters(8192) == (8192 - ModelNotes.MaximumLocalAnswerTokens - 1024) * 3 && ModelNotes.MaximumLocalTranscriptCharacters(65536) > ModelNotes.MaximumLocalTranscriptCharacters(32768)
                && ModelNotes.MaximumLocalTranscriptCharacters(0) == 0, "the local transcript limit follows the context, leaving the answer and the prompt their tokens at 3 characters per token");
            Check(new[] { "10:30", "4 pm", "9 a.m.", "16 Uhr", "12.30 Uhr", "noon", "Friday", "el viernes", "Thursday, March 12", "12 de marzo", "3. Mai", "March 3rd", "2026-03-12", "12/03/2026", "Mittwoch um 9:00" }.All(ModelNotes.IsClockTimeOrDate)
                && !new[] { "2.5", "version 3.10", "1.2.3", "this week", "a week before release", "next month", "March", "Q3", "2027", "3 minutes", "3 amounts", "", null }.Any(ModelNotes.IsClockTimeOrDate),
                "only clock times, weekdays, dates with their day and numeric dates count as times; version numbers, durations and vague spans do not");
            var vagueTimes = ModelNotes.Read("""
                {"summary": ["Planning."], "decisions": [], "actionItems": [], "openQuestions": [], "timesMentioned": [{"time": "12:30", "text": "Report due.", "citations": ["62:05"]}, {"time": "2.5", "text": "Version.", "citations": ["00:05"]},
                 {"time": "this week", "text": "Soon.", "citations": ["00:05"]}, {"time": "Friday", "text": "Lumen ships.", "citations": ["00:05"]}, {"text": "No time.", "citations": ["00:05"]}]}
                """, planning, "test-model");
            Check(vagueTimes.NotTimes == 3 && vagueTimes.Dropped == 0 && vagueTimes.Text.Contains("\n3 mentioned times were left out because they were not a clock time, date or weekday.\n")
                && vagueTimes.Text.EndsWith($"Times mentioned\n- 12:30 · Report due. {Citation(0)}\n- Friday · Lumen ships. {Citation(1)} {Citation(2)}"), "times that are no clock time or date are left out and counted apart from uncited points");
            Check(ModelNotes.System.Contains("Never list version numbers, durations, amounts or vague spans"), "the prompt asks for clock times and dates only");
            var mostlySpanish = new MeetingDocument { Languages = "es,en", Segments = [new() { Text = "Vale, lanzamos la campaña el 3 de diciembre y no a finales de noviembre." },
                new() { Start = 5, Text = "Okay, that works for me." }, new() { Start = 9, Text = "Entonces el evento es online, no en Valencia." }] };
            var spanishRequest = ModelNotes.Request(mostlySpanish);
            Check(ModelNotes.MainLanguage(mostlySpanish) == "es" && spanishRequest.Contains("Notes language: Spanish" + Environment.NewLine) &&spanishRequest.EndsWith("</transcript>"),
                "the notes language is the one most of the transcript is spoken in, named once before the transcript");
            Check(ModelNotes.MainLanguage(new MeetingDocument { Languages = "de", Segments = [new() { Text = "The plan is fine." }] }) == "de"
                && ModelNotes.MainLanguage(new MeetingDocument { Languages = "en,de", Segments = [new() { Text = "Wir bleiben bei dem Stecker, das ist entschieden." }] }) == "de",
                "a meeting held in one chosen language gets notes in that language; among several the transcript decides");
        }
    }

    public static void VerifyNotesLanguageAndSetup()
    {
        // The notes language counts only what was said; the times list says when its cap cut it; the notes button names a port that is not the
        // scheme's default; the Notes tab drops only the header of the rule-based notes; a failed setup says why.
        {
            var labelled = new MeetingDocument { Languages = "en,de", Segments = [new() { Speaker = "You", Text = "Morgen ist Freitag." }, new() { Start = 5, Speaker = "You", Text = "Die Liste ist fertig." }] };
            Check(ModelNotes.MainLanguage(labelled) == "de", "the notes language counts the spoken words only, so the speaker label \"You\" does not make a German meeting English");
            var timed = new MeetingDocument { Title = "Timetable", Segments = [new() { Start = 5, Speaker = "Ana", Text = "The trains leave at 9:00, 9:30 and so on." }] };
            string TimesAnswer(int count) => "{\"summary\": [], \"decisions\": [], \"actionItems\": [], \"openQuestions\": [], \"timesMentioned\": ["
                + string.Join(", ", Enumerable.Range(0, count).Select(index => $"{{\"time\": \"{index % 24}:00\", \"text\": \"Train.\", \"citations\": [\"00:05\"]}}")) + "]}";
            var capped = ModelNotes.Read(TimesAnswer(25), timed, "test-model"); var full = ModelNotes.Read(TimesAnswer(19), timed, "test-model");
            using (var schema = System.Text.Json.JsonDocument.Parse(ModelNotes.AnswerSchema))
                Check(schema.RootElement.GetProperty("properties").GetProperty("timesMentioned").GetProperty("maxItems").GetInt32() == ModelNotes.MaximumTimesMentioned
                    && capped.Text.Contains("\nTimes mentioned stop at 20; the transcript may name more.\n") && capped.Text.Split("\n- ").Length - 1 == 20
                    && !full.Text.Contains("Times mentioned stop at"), "model notes list at most 20 times and say so when the list reached that cap; a shorter list says nothing");
            PrivateFlow.Windows.Settings Endpoint(string address) => new() { MeetingNotesStyle = MeetingNotesStyle.Api, RewriteModel = "test-model", RewriteEndpoint = address };
            Check(PrivateFlow.Windows.MeetingNotesWriter.Destination(Endpoint("https://notes.example:8443/v1")) == "test-model at notes.example:8443"
                && PrivateFlow.Windows.MeetingNotesWriter.Destination(Endpoint("https://notes.example/v1")) == "test-model at notes.example"
                && PrivateFlow.Windows.MeetingNotesWriter.Destination(Endpoint("https://notes.example:443/v1")) == "test-model at notes.example"
                && PrivateFlow.Windows.MeetingNotesWriter.Destination(Endpoint("http://127.0.0.1:8080/v1")) == "test-model at 127.0.0.1:8080",
                "the notes button names the host with its port when the port is not the scheme's default");
            var silentMicrophone = new MeetingDocument { MicrophoneDevice = "Headset", Status = "Complete", Chunks = [new() { File = "mic-000000.wav", Channel = "mic", Duration = 20, Peak = 0 }],
                Segments = [new() { Start = 1, End = 2, Speaker = "Remote speaker", Channel = "system", Text = "We meet at 12:30." }] };
            var onPage = MeetingHistory.RuleNotesOnPage(silentMicrophone);
            Check(onPage.StartsWith("The microphone “Headset” delivered no sound") && !onPage.Contains(ExtractiveNotes.Header) && onPage.Contains(ExtractiveNotes.SummaryHeading)
                && MeetingHistory.RuleNotesOnPage(new MeetingDocument { Segments = silentMicrophone.Segments }).StartsWith(ExtractiveNotes.SummaryHeading),
                "the Notes tab drops the header of the rule-based notes also when a silent channel is reported before it, and keeps that report");
            Same(PrivateFlow.Windows.SpeechSetup.FailureMessage("curl: (6) Could not resolve host: huggingface.co\r\nPinned range download failed; nothing installed.\r\nAt line:1 char:1\r\n"),
                "Setup failed: curl: (6) Could not resolve host: huggingface.co. Check network, available disk space and pinned download availability.", "a failed setup names the first error the script wrote");
            Same(PrivateFlow.Windows.SpeechSetup.FailureMessage(""), "Setup failed. Check network, available disk space and pinned download availability.", "a failed setup without an error text keeps the general advice");
            Same(PrivateFlow.Windows.SpeechSetup.FailureMessage("Access to the path 'C:\\Users\\<user>\\AppData\\Local\\PrivateFlow\\models\\model.gguf.partial' is denied.\r\n"),
                "Setup failed: Access to the path 'model.gguf.partial' is denied. Check network, available disk space and pinned download availability.", "a quoted path in a setup error shows only its file name");
            Same(PrivateFlow.Windows.SpeechSetup.FailureMessage("Expand-Archive : C:\\Users\\<user>\\AppData\\Local\\PrivateFlow\\downloads\\llama.zip is not a valid archive"),
                "Setup failed: Expand-Archive : llama.zip is not a valid archive. Check network, available disk space and pinned download availability.", "an unquoted path in a setup error shows only its file name");
            var readable = MeetingHistory.ReadableNotes($"- Ship it. [Design · review · 00:04:30 · You · {new string('a', 32)}] [Design · review · 01:02:05 · Remote speaker · {new string('b', 32)}]\nNone.");
            Check(string.Concat(readable.Select(piece => piece.Text)) == "- Ship it. [04:30 · You] [1:02:05 · Remote speaker]\nNone."
                && readable.Where(piece => piece.SegmentId is not null).Select(piece => piece.SegmentId).SequenceEqual([new string('a', 32), new string('b', 32)])
                && MeetingHistory.ReadableNotes("No citations here [12:30].").Single() == new NotesPiece("No citations here [12:30].", null),
                "on the Notes tab a citation reads as its time and speaker and keeps the row it leads to; other brackets stay text");
            var setupRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PrivateFlow-setup-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(setupRoot, "models")); Directory.CreateDirectory(Path.Combine(setupRoot, "downloads"));
                string[] leftovers = [Path.Combine(setupRoot, "models", PrivateFlow.Windows.MeetingNotesModel.FileName + ".partial"), Path.Combine(setupRoot, "models", PrivateFlow.Windows.MeetingNotesModel.FileName + ".range"),
                    Path.Combine(setupRoot, "downloads", "llama-b11577-bin-win-cuda-12.4-x64.zip.partial"), Path.Combine(setupRoot, "downloads", "cudart-llama-bin-win-cuda-12.4-x64.zip.partial")];
                // Verified files, and partial downloads of the speech setup and of the processor build the notes setup does not fetch on this PC.
                string[] kept = [Path.Combine(setupRoot, "models", "ggml-large-v3.bin"), Path.Combine(setupRoot, "downloads", "llama-b11577-bin-win-cpu-x64.zip"),
                    Path.Combine(setupRoot, "models", "ggml-large-v3-turbo.bin.partial"), Path.Combine(setupRoot, "downloads", "whisper-blas-bin-x64.zip.partial"),
                    Path.Combine(setupRoot, "downloads", "llama-b11577-bin-win-cpu-x64.zip.partial"), Path.Combine(setupRoot, "downloads", "other.range")];
                foreach (var file in leftovers.Concat(kept)) File.WriteAllText(file, "x");
                PrivateFlow.Windows.MeetingNotesModel.RemovePartialDownloads(setupRoot, nvidiaGpu: true);
                PrivateFlow.Windows.MeetingNotesModel.RemovePartialDownloads(Path.Combine(setupRoot, "missing"), nvidiaGpu: true);
                var processorBuildKept = File.Exists(Path.Combine(setupRoot, "downloads", "llama-b11577-bin-win-cpu-x64.zip.partial"));
                PrivateFlow.Windows.MeetingNotesModel.RemovePartialDownloads(setupRoot, nvidiaGpu: false);
                Check(!leftovers.Any(File.Exists) && processorBuildKept && kept.Where(file => !file.EndsWith("cpu-x64.zip.partial")).All(File.Exists)
                    && !File.Exists(Path.Combine(setupRoot, "downloads", "llama-b11577-bin-win-cpu-x64.zip.partial")),
                    "a cancelled notes model install removes only its own partial downloads and keeps every verified file and the partial downloads of another setup");
            }
            finally { Directory.Delete(setupRoot, true); }
        }
    }

    public static async Task VerifyGpuMemoryWait()
    {
        // A final pass waiting for GPU memory: one last load after the wait, fewer tries while the meeting notes hold the GPU, and a step that
        // follows what it waits for.
        {
            var memory = new GpuMemory(1_000_000_000, 8_000_000_000, 3_000_000_000);
            var timing = new MemoryWaitTiming(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
            async Task<(Exception? Ended, List<double> Pauses, List<bool> Steps, int Tries)> Wait(Func<double, bool> notes, Func<int, double, string?> attempt)
            {
                var now = 0.0; var pauses = new List<double>(); var steps = new List<bool>(); var tries = 0;
                var ended = await ModelAttempts.WaitForMemoryAsync(memory, timing, () => notes(now),
                    () => attempt(++tries, now) is { } failure ? Task.FromException(new InvalidOperationException(failure)) : Task.CompletedTask,
                    failure => failure.Message == "short" ? memory : null, (forNotes, _) => steps.Add(forNotes),
                    pause => { pauses.Add(pause.TotalSeconds); now += pause.TotalSeconds; return Task.CompletedTask; }, () => TimeSpan.FromSeconds(now), CancellationToken.None);
                return (ended, pauses, steps, tries);
            }
            var lastTry = await Wait(_ => false, (tries, _) => tries <= 10 ? "short" : null);
            Check(lastTry.Ended is null && lastTry.Tries == 11 && lastTry.Pauses.All(pause => pause == 3) && lastTry.Steps.SequenceEqual([false]),
                "after the wait for GPU memory ends, the pass tries one last load before it falls back");
            var afterNotes = await Wait(now => now < 45, (_, now) => now < 45 ? "short" : null);
            Check(afterNotes.Ended is null && afterNotes.Pauses.SequenceEqual([10.0, 10, 10, 10, 10]) && afterNotes.Steps.SequenceEqual([true]),
                "while the meeting notes hold the GPU the pass waits as long as they run and tries every 10 s instead of every 3 s");
            var stillShort = await Wait(now => now < 25, (_, _) => "short");
            Check(stillShort.Ended?.Message == "short" && stillShort.Steps.SequenceEqual([true, false]) && stillShort.Pauses.SequenceEqual([10.0, 10, 10]) && stillShort.Tries == 4,
                "when the notes end and the memory is still short, the step says so and the wait ends after its limit and one last try");
            var broken = await Wait(_ => false, (_, _) => "broken");
            Check(broken.Ended?.Message == "broken" && broken.Tries == 1, "a load that fails for another reason ends the wait at once");
            Same(SpeechRuntime.MemoryWaitStep("large-v3", memory, true), $"Waiting for the meeting notes being written on the GPU to finish ({SpeechRuntime.Gigabytes(1_000_000_000)} of {SpeechRuntime.Gigabytes(8_000_000_000)} GB free, about {SpeechRuntime.Gigabytes(3_000_000_000)} GB needed for large-v3)",
                "the step names the meeting notes when they hold the GPU");
            Check(SpeechRuntime.GpuMemoryLowLoading(memory).StartsWith("GPU memory is low (") && SpeechRuntime.GpuMemoryLowLoading(memory).EndsWith("so the speech model loads slowly"),
                "a dictation that loads into too little GPU memory can say so while it loads");
            var bars = new double[1];
            var restingMoves = WaveBars.Approach(bars, 0, 0, .02);
            var rising = WaveBars.Approach(bars, 0, .8, .02);
            var falls = 0; while (WaveBars.Approach(bars, 0, 0, .02) && falls < 1000) falls++;
            Check(!restingMoves && rising && bars[0] == 0 && falls < 100 && !WaveBars.Approach(bars, 0, 0, .02),
                "a wave bar at its level does not move, and a falling bar settles exactly on zero, so a silent meeting wave needs no redraw");
        }
    }

    public static async Task VerifyNotesModelCheck()
    {
        // The meeting notes check for their model before they take the local back end, so a missing model neither waits for a polish dictation
        // nor stops its warm server.
        {
            var polishRunning = await PrivateFlow.Windows.LocalRewriter.EnterAsync("the local polish", null, Timeout.InfiniteTimeSpan, CancellationToken.None);
            try
            {
                var absent = Path.Combine(Path.GetTempPath(), "PrivateFlow-missing-" + Guid.NewGuid().ToString("N"));
                var withoutNotesModel = new PrivateFlow.Windows.Settings { MeetingNotesEnginePath = Path.Combine(absent, "llama-server.exe"), MeetingNotesModelPath = Path.Combine(absent, "model.gguf") };
                var writing = PrivateFlow.Windows.LocalRewriter.WriteNotesAsync(withoutNotesModel, "", "", 16, TimeSpan.FromSeconds(1), "{}", CancellationToken.None);
                await Task.WhenAny(writing, Task.Delay(5000));
                Check(writing.IsFaulted && writing.Exception!.InnerException!.Message == PrivateFlow.Windows.LocalRewriter.NotesMissing && PrivateFlow.Windows.LocalRewriter.Running == "the local polish",
                    "meeting notes without their model fail at once with the reason instead of waiting for a running polish dictation");
            }
            finally { polishRunning.Dispose(); }
        }
    }

    public static async Task VerifySharedLocalBackend()
    {
        // The shared local back end: a Local polish dictation that arrives while the meeting notes hold the local model waits for them, hears at
        // once what it waits for and when the wait ends, keeps its Clean text when cancelled and otherwise runs when the notes are done. Without the
        // polish model it does not wait.
        {
            var installed = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PrivateFlow-polish-" + Guid.NewGuid().ToString("N"))).FullName;
            // Empty stand-ins pass the installed check; the "engine" is no program, so starting it fails and the polish falls back to Clean.
            File.WriteAllText(Path.Combine(installed, "not-an-engine.txt"), ""); File.WriteAllText(Path.Combine(installed, "model.gguf"), "");
            var polishing = new PrivateFlow.Windows.Settings { OutputStyle = OutputStyle.LocalPolish, LocalRewriteEnginePath = Path.Combine(installed, "not-an-engine.txt"), LocalRewriteModelPath = Path.Combine(installed, "model.gguf") };
            var missing = Path.Combine(Path.GetTempPath(), "PrivateFlow-missing-" + Guid.NewGuid().ToString("N"));
            var notInstalledPolish = new PrivateFlow.Windows.Settings { OutputStyle = OutputStyle.LocalPolish, LocalRewriteEnginePath = Path.Combine(missing, "llama-server.exe"), LocalRewriteModelPath = Path.Combine(missing, "model.gguf") };
            try
            {
                var notesRunning = await PrivateFlow.Windows.LocalRewriter.EnterAsync(PrivateFlow.Windows.LocalRewriter.NotesTask, null, Timeout.InfiniteTimeSpan, CancellationToken.None);
                string? heard = null;
                bool? waitEnded = null;
                using var skip = new CancellationTokenSource();
                var skippable = PrivateFlow.Windows.DictationPipeline.ProcessAsync("um, Please polish this sentence.", polishing, CancellationToken.None, busy => heard = busy, skip.Token, polishRuns => waitEnded = polishRuns);
                Check(heard == "the meeting notes" && !skippable.IsCompleted && waitEnded is null && PrivateFlow.Windows.LocalRewriter.Running == "the meeting notes",
                    "a Local polish dictation during the meeting notes is queued and hears at once that the meeting notes are still running");
                // The controller cancels a waiting dictation through the skip token only; its own token ends dictations that did not wait.
                var skippedAt = System.Diagnostics.Stopwatch.StartNew();
                skip.Cancel();
                var kept = await skippable;
                Check(skippedAt.Elapsed < TimeSpan.FromSeconds(1) && kept.Final == "Please polish this sentence." && kept.Notice == "Used Clean: " + PrivateFlow.Windows.DictationPipeline.PolishCancelled
                    && waitEnded is null && PrivateFlow.Windows.LocalRewriter.Running == "the meeting notes",
                    "Cancel while the polish waits for the notes ends the wait at once and keeps the recognized text as Clean with a note, without an end of the wait that would take Escape back, while the notes go on");
                string? notInstalledHeard = null;
                var withoutPolish = await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Please polish this sentence.", notInstalledPolish, CancellationToken.None, busy => notInstalledHeard = busy);
                Check(notInstalledHeard is null && withoutPolish.Notice == "Used Clean: " + PrivateFlow.Windows.LocalRewriter.PolishMissing,
                    "without the polish model a dictation does not wait for the notes and uses Clean at once, saying where to set up Local polish");
                string? limitedHeard = null;
                polishing.PolishWaitSeconds = 1;
                var waitedOut = System.Diagnostics.Stopwatch.StartNew();
                var skipped = await PrivateFlow.Windows.DictationPipeline.ProcessAsync("um, Please polish this sentence.", polishing, CancellationToken.None, busy => limitedHeard = busy, waitEnded: polishRuns => waitEnded = polishRuns);
                Check(limitedHeard == "the meeting notes" && waitEnded == false && waitedOut.Elapsed >= TimeSpan.FromSeconds(0.9) && waitedOut.Elapsed < TimeSpan.FromSeconds(5)
                    && skipped.Final == "Please polish this sentence." && skipped.Notice == "Used Clean: Polish skipped because the wait for the meeting notes ran out."
                    && PrivateFlow.Windows.LocalRewriter.Running == "the meeting notes",
                    "past its wait limit a queued dictation hears that the wait ended without the polish, uses Clean and says the polish was skipped because the meeting notes were running, while the notes go on");
                polishing.PolishWaitSeconds = PrivateFlow.Windows.Settings.DefaultPolishWaitSeconds;
                heard = null; waitEnded = null;
                var afterNotes = PrivateFlow.Windows.DictationPipeline.ProcessAsync("Please polish this sentence.", polishing, CancellationToken.None, busy => heard = busy, waitEnded: polishRuns => waitEnded = polishRuns);
                Check(heard == "the meeting notes" && !afterNotes.IsCompleted && waitEnded is null, "a second queued dictation waits as well");
                notesRunning.Dispose();
                var polished = await afterNotes;
                Check(waitEnded == true && polished.Notice == "Used Clean: rewriting unavailable." && PrivateFlow.Windows.LocalRewriter.Running is null,
                    "once the notes are done the queued dictation hears that its wait ended and its polish runs, here falling back to Clean because the stand-in engine cannot start, and the back end is free again");
                string? notHeard = null; waitEnded = null;
                await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Please polish this sentence.", polishing, CancellationToken.None, busy => notHeard = busy, waitEnded: polishRuns => waitEnded = polishRuns);
                Check(notHeard is null && waitEnded is null, "a dictation hears nothing when the local back end is free");
            }
            finally { Directory.Delete(installed, true); }
            var waitedPolicy = new DictationPolicy();
            var waitedSession = waitedPolicy.Begin(SelectedTarget);
            Check(waitedPolicy.CanInsert(waitedSession, SelectedTarget, false, "hello") && !waitedPolicy.CanInsert(waitedSession, OtherTarget, false, "hello"),
                "after the wait the text is pasted only while the selected window is still in front by itself");
        }
    }

    public static async Task VerifyGpuMemoryAndFinalPass()
    {
        GpuMemory shortMemory = new(1_000_000_000, 8_589_000_000, 4_640_000_000), tooLarge = new(7_500_000_000, 8_589_000_000, 24_000_000_000), enough = new(6_000_000_000, 8_589_000_000, 4_640_000_000);
        Same(SpeechRuntime.GpuMemoryShortMessage("large-v3", shortMemory),
            $"Not enough free GPU memory for large-v3: {SpeechRuntime.Gigabytes(1_000_000_000)} of {SpeechRuntime.Gigabytes(8_589_000_000)} GB free, about {SpeechRuntime.Gigabytes(4_640_000_000)} GB needed. " +
            "Other GPU work, such as a game, a local language model or meeting notes being written, is using the rest.",
            "a GPU memory shortage names the model and the free, total and needed memory, without claiming to know who holds the rest");
        Check(SpeechRuntime.GpuMemoryShortMessage("custom.bin", tooLarge).EndsWith("The GPU has too little memory for this model.") && shortMemory.CanFree && !tooLarge.CanFree,
            "a model larger than the whole GPU is not blamed on other work, and only a model that fits the GPU is worth waiting for");
        Check(SpeechRuntime.DecideGpuLoad(enough, true) == GpuMemoryDecision.Load && SpeechRuntime.DecideGpuLoad(enough, false) == GpuMemoryDecision.Load
            && SpeechRuntime.DecideGpuLoad(shortMemory, true) == GpuMemoryDecision.Refuse && SpeechRuntime.DecideGpuLoad(shortMemory, false) == GpuMemoryDecision.LoadWithWarning
            && SpeechRuntime.DecideGpuLoad(tooLarge, true) == GpuMemoryDecision.Refuse && SpeechRuntime.DecideGpuLoad(tooLarge, false) == GpuMemoryDecision.Refuse
            && SpeechRuntime.DecideGpuLoad(new(0, 0, 4_640_000_000), false) == GpuMemoryDecision.Load,
            "a model that fits loads; short memory is refused where a final pass can wait or fall back and loads with a warning for dictation; a model larger than the GPU is always refused; an unknown GPU size loads");
        // Scripted final passes: each work call and each wait takes the next outcome. "ok" succeeds (a wait that succeeds has loaded the model),
        // "memory" is a shortage other work can free, "large" a model larger than the GPU, anything else a failure with that message.
        async Task<(string Model, string Fallback, string Error, string Calls)> Attempts(string meeting, string[] work, string[] waits)
        {
            var calls = new List<string>(); var works = new Queue<string>(work); var waiting = new Queue<string>(waits);
            Exception Failure(string kind)
            {
                var failure = new InvalidOperationException(kind);
                if (kind is "memory" or "large") failure.Data["memory"] = kind == "memory" ? shortMemory : tooLarge;
                return failure;
            }
            try
            {
                var result = await ModelAttempts.RunAsync(meeting, "speech",
                    async model => { calls.Add("work " + model); await Task.Yield(); var outcome = works.Dequeue(); return outcome == "ok" ? model : throw Failure(outcome); },
                    failure => failure.Data["memory"] as GpuMemory,
                    async (model, _) => { calls.Add("wait " + model); await Task.Yield(); var outcome = waiting.Dequeue(); return outcome == "ok" ? null : Failure(outcome); },
                    (step, retry) => { calls.Add((retry ? "retry " : "next ") + step); return Task.CompletedTask; }, model => model, CancellationToken.None);
                return (result.Model, result.Fallback, "", string.Join(" | ", calls));
            }
            catch (InvalidOperationException ex) { return ("", "", ex.Message, string.Join(" | ", calls)); }
        }
        var waitThenCrash = await Attempts("meeting", ["memory", "worker ended", "ok"], ["worker ended while loading"]);
        Check(waitThenCrash is { Model: "speech", Error: "" } && waitThenCrash.Fallback == "The meeting model meeting failed twice (worker ended), so the speech model speech produced this transcript."
            && waitThenCrash.Calls == "work meeting | wait meeting | retry Trying meeting again after an error (worker ended while loading) | work meeting | next Finishing with speech: meeting failed twice | work speech",
            $"a load that fails while waiting for GPU memory is tried once more with a fresh worker, then the speech model finishes with the reason ({waitThenCrash.Calls})");
        var waitThenFits = await Attempts("meeting", ["memory", "ok"], ["ok"]);
        Check(waitThenFits is { Model: "meeting", Fallback: "", Calls: "work meeting | wait meeting | work meeting" }, "once the GPU memory frees up during the wait, the meeting model runs the pass");
        var waitInVain = await Attempts("meeting", ["memory", "ok"], ["memory"]);
        Check(waitInVain.Model == "speech" && waitInVain.Fallback.Contains("were free because other GPU work was using the rest") && !waitInVain.Calls.Contains("retry"),
            "a meeting model that still does not fit after the wait gives way to the speech model at once, with the memory in the reason");
        var crashThenShort = await Attempts("meeting", ["memory", "memory", "ok"], ["worker ended"]);
        Check(crashThenShort.Model == "speech" && crashThenShort.Fallback.Contains("GB of GPU memory") && crashThenShort.Calls.Split(" | ").Count(call => call.StartsWith("wait")) == 1,
            "after a failed wait and a retry that is short of memory again, the speech model finishes without a second wait");
        var larger = await Attempts("meeting", ["large", "ok"], []);
        Check(larger.Model == "speech" && larger.Fallback.Contains("more than the") && !larger.Calls.Contains("wait"), "a meeting model larger than the GPU gives way at once without waiting");
        var crashOnce = await Attempts("meeting", ["timeout", "ok"], []);
        Check(crashOnce is { Model: "meeting", Fallback: "" } && crashOnce.Calls.Contains("retry Trying meeting again after an error"), "a failure other than memory is tried once more and the meeting model finishes");
        var nothingFits = await Attempts("", ["memory"], ["memory"]);
        Check(nothingFits.Error == "memory Close what uses the GPU or let it finish, then finish the transcript again.",
            "when only the speech model is left and it does not fit after the wait, the pass ends with the reason and says what to do");
        var failsTwice = await Attempts("", ["first", "second"], []);
        Check(failsTwice is { Error: "second" } && failsTwice.Calls.Contains("retry"), "the speech model alone is tried once more, then the pass ends with the last error");
    }
}
