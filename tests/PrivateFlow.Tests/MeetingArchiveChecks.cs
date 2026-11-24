using PrivateFlow.Core;
using static Harness;

static class MeetingArchiveChecks
{
    public static void VerifyMeetingArchive()
    {
        var archiveRoot = Path.Combine(Path.GetTempPath(), "PrivateFlow-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new MeetingArchive(archiveRoot);
            var meeting = new MeetingDocument { Title = "Build review", Ended = DateTimeOffset.UtcNow.AddDays(-31), Status = "Complete", Segments = [new() { Start = 42, End = 45, Speaker = "Alex", Text = "We agreed the voltage must remain 42 V." }] };
            archive.Save(meeting); var loaded = archive.Load(meeting.Id);
            Check(loaded.Segments[0].Text == meeting.Segments[0].Text, "durable meeting manifest roundtrip preserves evidence");
            Reject(() => archive.DirectoryFor("../other"), "archive rejects traversing identifiers");
            Reject(() => archive.ChunkPath(meeting.Id, "../../secret.wav"), "archive rejects traversing audio names");
            var evidence = MeetingArchive.Search([loaded], "What voltage was agreed?");
            Check(evidence.Count == 1 && evidence[0].Segment.Text.Contains("42 V") && evidence[0].Citation.Contains("00:00:42") && evidence[0].Citation.Contains(meeting.Segments[0].Id), "a search returns the actual evidence with a stable citation");
            Check(MeetingArchive.Search([loaded], "Missing unicorn details").Count == 0, "a search for something nobody said finds nothing");
            Check(MeetingArchive.Notes(loaded).Contains(ExtractiveNotes.DecisionsHeading + "\n- We agreed the voltage must remain 42 V.") && MeetingArchive.Notes(loaded).Contains(meeting.Segments[0].Id), "meeting notes link decisions and actions to segments");
            var backup = archiveRoot + ".zip";
            try
            {
                File.WriteAllText(backup, "previous backup");
                archive.Export(meeting.Id, backup);
                using (var zip = System.IO.Compression.ZipFile.OpenRead(backup))
                    Check(zip.GetEntry("meeting.json") is not null, "confirmed backup overwrite produces a valid archive");
                var previous = File.ReadAllBytes(backup);
                try { archive.Export(Guid.NewGuid().ToString("N"), backup); Check(false, "missing source rejects backup export"); }
                catch (IOException) { Check(previous.SequenceEqual(File.ReadAllBytes(backup)), "failed export preserves previous backup"); }
                Check(!Directory.EnumerateFiles(Path.GetDirectoryName(backup)!, Path.GetFileName(backup) + ".*.partial").Any(), "backup export cleans temporary files");
            }
            finally { File.Delete(backup); }
            foreach (var badJson in new[] { "\"Chunks\":[null]", "\"Chunks\":[{\"File\":null}]", "\"Segments\":[null]" })
            {
                var damaged = new MeetingDocument(); archive.Save(damaged);
                File.WriteAllText(Path.Combine(archive.DirectoryFor(damaged.Id), "meeting.json"), "{\"Id\":\"" + damaged.Id + "\"," + badJson + "}");
                Reject(() => archive.Load(damaged.Id), "damaged manifest is rejected with a validation error");
                Check(archive.List().Single().Id == meeting.Id, "damaged manifest does not hide valid meetings");
                archive.Delete(damaged.Id);
            }
            var incomplete = new MeetingDocument { Status = "Interrupted", Ended = DateTimeOffset.UtcNow.AddDays(-40) }; archive.Save(incomplete);
            Check(archive.DeleteExpired(30, DateTimeOffset.UtcNow) == 1 && archive.List().Single().Id == incomplete.Id, "retention deletes only completed expired entries and preserves recovery");
            Check(MeetingArchive.DescribeUnfinished(new MeetingDocument { Status = "Recording", UnfinishedReason = MeetingArchive.FinishingCancelled }).EndsWith("ended unexpectedly while recording it, such as in a crash.")
                && MeetingArchive.DescribeUnfinished(new MeetingDocument { Status = "Finalizing" }).EndsWith("ended unexpectedly while finishing its transcript, such as in a crash."),
                "a meeting still recording or finishing when it is found unfinished is described as ended by a crash");
            var failedMeeting = new MeetingDocument { Status = "Interrupted", UnfinishedReason = MeetingArchive.FinalTranscriptFailed("the model could not load.") }; archive.Save(failedMeeting);
            Same(MeetingArchive.DescribeUnfinished(archive.Load(failedMeeting.Id)), failedMeeting + " · The final transcript failed: the model could not load.", "the reason a meeting ended unfinished is stored and shown with it");
            var manifestFile = Path.Combine(archive.DirectoryFor(incomplete.Id), "meeting.json");
            var withoutReason = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestFile))!.AsObject(); withoutReason.Remove("UnfinishedReason");
            File.WriteAllText(manifestFile, withoutReason.ToJsonString());
            var olderMeeting = archive.Load(incomplete.Id);
            withoutReason["UnfinishedReason"] = null; File.WriteAllText(manifestFile, withoutReason.ToJsonString());
            Check(olderMeeting.UnfinishedReason == "" && MeetingArchive.DescribeUnfinished(olderMeeting) == olderMeeting.ToString() && MeetingArchive.DescribeUnfinished(archive.Load(incomplete.Id)) == olderMeeting.ToString(),
                "a meeting saved before the reason was kept, or with no reason, shows no reason");
            var fellBack = new MeetingDocument { Status = "Complete", TranscriptModel = "large-v3-turbo-q8_0",
                ModelFallback = "The meeting model large-v3 failed twice (no memory), so the speech model large-v3-turbo-q8_0 produced this transcript.",
                Segments = [new() { Start = 1, End = 2, Speaker = "You", Channel = "mic", Text = "We agreed on Friday." }] };
            archive.Save(fellBack); var reloadedFallback = archive.Load(fellBack.Id);
            var withoutFallback = archive.Load(fellBack.Id); withoutFallback.ModelFallback = "";
            Check(reloadedFallback.TranscriptModel == "large-v3-turbo-q8_0" && MeetingArchive.Notes(reloadedFallback) == fellBack.ModelFallback + "\n\n" + MeetingArchive.Notes(withoutFallback)
                && !MeetingArchive.Notes(withoutFallback).Contains("The meeting model") && olderMeeting is { TranscriptModel: "", ModelFallback: "" },
                "the meeting record keeps which model produced the transcript, a fallback and its reason lead the notes, and older meetings have neither");
            Same(MeetingArchive.ExportSrt([new(1.25, 2.5, "Hello")]), "1\n00:00:01,250 --> 00:00:02,500\nHello\n\n".Replace("\n", Environment.NewLine), "timestamp export uses real segment times");
        }
        finally { Directory.Delete(archiveRoot, true); }
        Same(new PreparedDictation("spell A B C 1 end spelling", [], [], "en", false, true).Text, "ABC1", "spelling survives technical-token protection in the full pipeline");
    }

    public static async Task VerifyHistorySearch()
    {
        // The History page: hits per meeting through the archive search, a filter to one meeting, notes written by a model kept with the meeting.
        {
            MeetingDocument Meeting(string title, params string[] lines) => new()
            {
                Title = title, Status = "Complete", Started = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), Ended = new DateTimeOffset(2026, 11, 17, 9, 25, 0, TimeSpan.Zero),
                Segments = [.. lines.Select((text, index) => new MeetingSegment { Start = 10 * index, End = 10 * index + 5, Speaker = "You", Channel = "mic", Text = text })]
            };
            var design = Meeting("Design review", "Can we do the demo before lunch?", "Lunch is at 12:30, so eleven would be safer.", "Eleven then.", "The train leaves at twelve thirty.");
            var supplier = Meeting("Supplier call", "The parts arrive Friday.", "Call me back at 1230.");
            var weekly = Meeting("Weekly sync", "Nothing new this week.");
            var history = MeetingHistory.HitsByMeeting([design, supplier, weekly], "twelve thirty");
            Check(history.Count == 2 && history[design.Id].Select(segment => segment.Start).SequenceEqual([10.0, 30.0]) && history[supplier.Id].Select(segment => segment.Start).SequenceEqual([10.0]) && !history.ContainsKey(weekly.Id),
                "hits per meeting list each matching row in time order, however the time is written, and leave out meetings without hits");
            var onlyDesign = MeetingHistory.HitsByMeeting([design, supplier, weekly], "twelve thirty", design.Id);
            Check(onlyDesign.Keys.SequenceEqual([design.Id]) && onlyDesign[design.Id].Count == 2 && MeetingHistory.HitsByMeeting([design, supplier], "twelve thirty", weekly.Id).Count == 0,
                "the meeting filter searches only the named meeting");
            var everyRow = MeetingArchive.Search([design, supplier, weekly], "twelve thirty", int.MaxValue);
            Check(history.Values.Sum(rows => rows.Count) == everyRow.Count && everyRow.All(hit => history[hit.MeetingId].Contains(hit.Segment))
                && MeetingHistory.HitsByMeeting([design], "what was the meeting about").Count == 0 && MeetingHistory.HitsByMeeting([design], "  ").Count == 0,
                "hits per meeting are exactly the rows the archive search finds, beyond its usual limit, and question words alone find nothing");
            var fifteen = Meeting("Long", [.. Enumerable.Range(0, 15).Select(index => $"Row {index} mentions Lumen.")]);
            Check(MeetingArchive.Search([fifteen], "Lumen").Count == 12 && MeetingHistory.HitsByMeeting([fifteen], "Lumen")[fifteen.Id].Count == 15, "hit counts include every matching row, not only the twelve the evidence search shows");
            var now = new DateTimeOffset(2026, 11, 17, 18, 0, 0, TimeSpan.Zero);
            var brief = Meeting("Short"); brief.Ended = brief.Started.AddSeconds(30);
            Check(MeetingHistory.Describe(design, now).EndsWith(" · 25 min") && MeetingHistory.Describe(brief, now).EndsWith(" · under 1 min")
                && MeetingHistory.Describe(new MeetingDocument { Started = design.Started, Ended = design.Started.AddMinutes(65) }, now).EndsWith(" · 1 h 05 min")
                && MeetingHistory.Duration(new MeetingDocument { Status = "Interrupted", Chunks = [new() { File = "mic-000000.wav", Channel = "mic", Offset = 40, Duration = 20 }] }) == TimeSpan.FromMinutes(1),
                "the list gives each meeting's length, and an unfinished meeting's from its saved audio");
            var describedDay = MeetingHistory.Describe(new MeetingDocument { Started = now.AddDays(-30), Ended = now.AddDays(-30).AddMinutes(5) }, now);
            Check(MeetingHistory.Describe(new MeetingDocument { Started = now.AddMinutes(-30), Ended = now }, now).StartsWith("Today ") && describedDay.Contains(now.AddDays(-30).ToLocalTime().ToString("yyyy")),
                "a meeting of today says Today, an older one its date");
            var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-history-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new MeetingArchive(root);
                store.Save(design);
                var plain = store.Load(design.Id);
                Check(plain.WrittenNotes is null && MeetingHistory.Notes(plain) == MeetingArchive.Notes(plain) && !MeetingHistory.WrittenNotesMatchTranscript(plain), "a meeting without written notes shows the extractive notes");
                var olderFile = Path.Combine(store.DirectoryFor(design.Id), "meeting.json");
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(olderFile))!.AsObject(); json.Remove("WrittenNotes"); File.WriteAllText(olderFile, json.ToJsonString());
                Check(store.Load(design.Id).WrittenNotes is null && store.List().Any(document => document.Id == design.Id), "a meeting file saved before written notes were kept loads without them");
                var written = new MeetingWrittenNotes { Text = "Written by the model test-model from the transcript.", Model = "test-model through the API", Written = now, Transcript = MeetingHistory.TranscriptFingerprint(design) };
                plain.WrittenNotes = written; store.Save(plain);
                var reloaded = store.Load(design.Id);
                Check(reloaded.WrittenNotes is { } kept && kept.Text == written.Text && kept.Model == written.Model && kept.Written == now && MeetingHistory.Notes(reloaded) == written.Text && MeetingHistory.WrittenNotesMatchTranscript(reloaded),
                    "written notes are stored with the meeting together with the model and the time, and shown and exported instead of the extractive notes");
                var opened = new MeetingWrittenNotes { Text = "The microphone delivered no sound.\n\n" + ModelNotes.WrittenBy("test-model through the API") + "Check every point against the transcript before relying on it.\n\nSummary\nWritten by hand.", Model = "test-model through the API" };
                Same(MeetingHistory.WrittenNotesOnPage(opened), "The microphone delivered no sound.\n\nCheck every point against the transcript before relying on it.\n\nSummary\nWritten by hand.",
                    "the Notes tab leaves out the opening that names the model, which the row above the notes already says, and keeps the rest");
                reloaded.Segments[1].Text = "Lunch is at one.";
                Check(!MeetingHistory.WrittenNotesMatchTranscript(reloaded), "an edited transcript marks written notes as written from an earlier transcript");
                json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(olderFile))!.AsObject(); json["WrittenNotes"]!["Text"] = null; File.WriteAllText(olderFile, json.ToJsonString());
                Check(store.Load(design.Id).WrittenNotes is null, "damaged written notes fall back to the extractive notes instead of hiding the meeting");
                Check(MeetingArchive.UnfinishedReasonText(new MeetingDocument { Status = "Interrupted", UnfinishedReason = MeetingArchive.StoppedToFinishLater }) == MeetingArchive.StoppedToFinishLater
                    && MeetingArchive.UnfinishedReasonText(new MeetingDocument { Status = "Recording" }).StartsWith("Private Flow ended unexpectedly while recording")
                    && MeetingArchive.DescribeUnfinished(new MeetingDocument { Status = "Interrupted", Title = "T" }).EndsWith("(Interrupted)"), "the list names why a meeting is unfinished, as the Meetings page does");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            var calls = 0;
            Task<string> Answer(string system, string user, CancellationToken token)
            {
                calls++;
                return Task.FromResult($$"""{"summary": ["Demo planning."], "decisions": [{"text": "Demo at eleven.", "citations": ["00:20"]}], "actionItems": [], "openQuestions": [], "timesMentioned": []}""");
            }
            var apiStyle = new PrivateFlow.Windows.Settings { MeetingNotesStyle = MeetingNotesStyle.Api, RewriteEndpoint = "https://notes.invalid/v1", RewriteModel = "test-model", RewriteKeyVariable = "PRIVATE_FLOW_TEST_KEY_" + Guid.NewGuid().ToString("N") };
            var byApi = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(design, apiStyle, CancellationToken.None, Answer);
            var byRules = await PrivateFlow.Windows.MeetingNotesWriter.WriteAsync(design, new PrivateFlow.Windows.Settings(), CancellationToken.None, Answer);
            Check(calls == 1 && byApi.Written && byApi.Model == "test-model through the API" && byApi.Text.Contains("Demo at eleven.") && !byRules.Written && byRules.Model is null,
                "a completion function stands in for the model, the result names the model for storing, and the rule-based style calls no model");
            Check(PrivateFlow.Windows.MeetingNotesWriter.Destination(apiStyle) == "test-model at notes.invalid" && PrivateFlow.Windows.MeetingNotesWriter.Destination(new PrivateFlow.Windows.Settings { MeetingNotesStyle = MeetingNotesStyle.LocalModel }) == "the local model",
                "the notes button names the model and the host the transcript goes to, or the local model");
        }
    }

    public static void VerifyArchiveHelpers()
    {
        var helperRoot = Path.Combine(Path.GetTempPath(), "PrivateFlow-archive-helpers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var helperArchive = new MeetingArchive(helperRoot);
            var review = new MeetingDocument { Title = "Review", Status = "Complete", Segments = [new() { Start = 3725, End = 3727, Speaker = "You", Text = "Wir haben beschlossen, am Freitag zu liefern." },
                new() { Start = 10, End = 12, Speaker = "Remote speaker", Text = "The voltage is 42 V and the voltage stays." }] };
            Same(MeetingArchive.ExportText(review), "[00:00:10] Remote speaker: The voltage is 42 V and the voltage stays.\n[01:02:05] You: Wir haben beschlossen, am Freitag zu liefern.", "the text export orders segments with timestamps and speakers");
            Check(MeetingArchive.Notes(review).Contains(ExtractiveNotes.DecisionsHeading + "\n- Wir haben beschlossen"), "German decisions are listed in the notes");
            var hits = MeetingArchive.Search([review], "What was the voltage on Freitag?");
            Check(hits.Count == 2 && hits[0].Segment.Start == 10 && MeetingArchive.Search([review], "what was the meeting about").Count == 0, "search ranks by matched terms and ignores question words");
            string[] spokenTimes = ["The test finished at 12:34.", "The test finished at 1234.", "Terminó a las doce y treinta y cuatro.", "Der Test war um zwölf Uhr vierunddreißig fertig.", "It ended at twelve thirty-four.", "Fertig um 12.34 Uhr."];
            var timedMeeting = new MeetingDocument { Title = "Show", Segments = [.. spokenTimes.Select((text, index) => new MeetingSegment { Start = index, Text = text }), new() { Start = 9, Text = "We ran 42 units and 12 boards." }] };
            Check(new[] { "12:34", "1234", "12.34", "twelve thirty-four", "doce treinta y cuatro" }.All(query => MeetingArchive.Search([timedMeeting], query).Select(hit => hit.Segment.Text).Order().SequenceEqual(spokenTimes.Order())),
                "search finds a spoken time however the query and the transcript write it, and not other numbers");
            var minutes = new MeetingDocument { Title = "Show", Segments = [new() { Text = "We are at minute forty-two." }, new() { Text = "Estamos en el minuto cuarenta y dos." }, new() { Text = "Wir sind bei Minute zweiundvierzig." }, new() { Text = "Back to 12:42." }] };
            Check(MeetingArchive.Search([minutes], "minute 42").Count == 4 && MeetingArchive.Search([minutes], "minute 42")[0].Segment.Text.StartsWith("We are"), "a minute mark is found in English, Spanish and German, and a lone number still finds a clock time");
            var manyMinutes = new MeetingDocument { Title = "Show", Segments = [.. Enumerable.Range(1, 15).Select(minute => new MeetingSegment { Start = minute, Text = $"We are at minute {minute}." }), new() { Start = 99, Text = "En el minuto cuarenta hablan con el jefe." }] };
            Check(MeetingArchive.Search([manyMinutes], "minute 40")[0].Segment.Start == 99 && MeetingArchive.Search([manyMinutes], "Minute vierzig")[0].Segment.Start == 99,
                "a minute mark asked for in English or German ranks first when the transcript says it in Spanish");
            Same(string.Join(" | ", spokenTimes.Append("Es en el minuto cuarenta y uno.").Append("Nine fifteen.").Append("At 12:05 we stopped.").Append("We ran 42 units over 3 days.").Append("Es ocho siete tres.").Append("Let us move the call to 345.").Select(text => string.Join(",", TranscriptScoring.TimeReferences(text)))),
                "12:34 | 12:34 | 12:34 | 12:34 | 12:34 | 12:34 | minute 41 | 9:15 | 12:05 |  |  | 3:45", "times are recognized in every written and spoken form, ordinary numbers are not times");
            Same(string.Join(",", TranscriptScoring.TimeReferences("Call me on 4915123456789 tomorrow, 400 000 000 000 euros 15 times.")), "", "a number too long for a time is not read as one");
            Same(string.Join(" | ", new[] { "It ended at 12:05.", "It ended at twelve oh five.", "Terminó a las doce y cinco.", "Fertig um zwölf Uhr fünf.", "We have 12 5 items." }.Select(text => string.Join(",", TranscriptScoring.TimeReferences(text)))),
                "12:05 | 12:05 | 12:05 | 12:05 | ", "times with minutes below ten are recognized in every form");
            string Times(IEnumerable<string> texts) => string.Join(" | ", texts.Select(text => string.Join(",", TranscriptScoring.TimeReferences(text))));
            Same(Times(["Monday at 9:00, one hour.", "Montag um 9:00, eine Stunde.", "El lunes a las 9:00, una hora."]), "9:00 | 9:00 | 9:00", "a quantity after a time is not read as its minutes");
            Same(Times(["We meet at nine, five of us.", "Wir treffen uns um neun, fünf von uns.", "Nos vemos a las nueve, cinco de nosotros.", "At 12. 5 left.", "At 12? 5 left.", "At 12! 5 left.", "At 12; 5 left.", "At 12: 5 left."]),
                " |  |  |  |  |  |  | ", "punctuation between an hour and a digit ends the time");
            Same(Times(["We meet at nine one hour.", "At 12 5 minutes.", "At 12 5 percent.", "At 12 5 euros.", "At twelve five people came.", "At 12 5 days.", "At 12 3 weeks.",
                "Um neun eine Stunde.", "Um zwölf fünf Minuten.", "Um 12 5 Prozent.", "Um 12 5 Tage.", "Um 12 3 Wochen.",
                "A las nueve una hora.", "A las doce cinco minutos.", "A las 12 5 por ciento.", "A las nueve cinco personas.", "A las 12 5 días.", "A las 12 3 semanas."]),
                string.Join(" | ", Enumerable.Repeat("", 18)), "a digit before a unit is a quantity, not minutes, in English, German and Spanish");
            Same(Times(["At 12 15 percent were ready.", "At 12 15 people came.", "At 9 30 minutes were left.", "Um 12 15 Prozent.", "A las 12 15 personas.", "At 12 15.", "It is 12 15 pm.", "At 12 15 we meet.",
                "At 14 30 hours.", "A las 18 30 horas."]),
                " |  |  |  |  | 12:15 | 12:15 | 12:15 | 14:30 | 18:30", "two digits before a unit are a quantity too, and two-digit minutes without one, or before \"hours\", still make a time");
            Same(string.Join(" | ", new[] { "At 12 15 percent were ready.", "At 12 15 we meet." }.Select(text => string.Join(",", ExtractiveNotes.TimesIn(text)))), " | 12:15",
                "the rule-based notes list no time for two digits before a unit");
            Same(Times(["We start at 9:00 5 of us.", "We start at 9 30 5 of us.", "We start at five o'clock five of us.", "Wir starten um 17.30 Uhr fünf von uns.", "Wir starten um 9 Uhr 30 fünf von uns.",
                "Empezamos a las nueve y media cinco de nosotros.", "At 9 15 30 of us."]),
                "9:00 | 9:30 |  | 17:30 | 9:30 | 9:30 | 9:15", "nothing after a time that has its minutes adds minutes");
            Same(Times(["It ended at twelve five.", "It ended at 12 oh 5.", "It ended at 12 5 and we left.", "Terminó a las doce y cinco.", "Terminó a las 12:05.", "Fertig um zwölf Uhr fünf.", "Fertig um 12:05.", "Fertig um 12.05 Uhr."]),
                "12:05 | 12:05 | 12:05 | 12:05 | 12:05 | 12:05 | 12:05 | 12:05", "spoken and written minutes below ten still count in English, Spanish and German");
            var earlyMinutes = new[] { "It ended at 12:05.", "It ended at 1205.", "It ended at twelve oh five.", "Terminó a las doce y cinco.", "Fertig um zwölf Uhr fünf." }
                .Select((text, index) => new MeetingDocument { Id = "early" + index, Title = text, Segments = [new MeetingSegment { Start = 1, End = 2, Text = text, Speaker = "You", Channel = "mic" }] }).ToArray();
            Check(new[] { "12:05", "1205", "twelve oh five", "doce y cinco" }.All(query => MeetingArchive.Search(earlyMinutes, query).Count == earlyMinutes.Length)
                && MeetingArchive.Search(earlyMinutes, "12:50").Count == 0 && MeetingArchive.Search([new MeetingDocument { Id = "digits", Title = "digits", Segments = [new MeetingSegment { Start = 1, End = 2, Text = "Es 873.", Speaker = "You", Channel = "mic" }] }], "ocho siete tres").Count == 1 && MeetingArchive.Search(earlyMinutes, "5").Count >= 3, "a time with minutes below ten is found however it is written or asked for");
            var quantities = new[] { "Monday at 9:00, one hour.", "Montag um neun eine Stunde.", "A las nueve cinco personas.", "At nine, five of us.", "At 9 15 5 of us." }
                .Select((text, index) => new MeetingDocument { Id = "quantity" + index, Title = text, Segments = [new MeetingSegment { Start = 1, End = 2, Text = text, Speaker = "You", Channel = "mic" }] }).ToArray();
            Check(new[] { "9:01", "nine oh one", "9:05", "nueve y cinco", "15:05" }.All(query => MeetingArchive.Search(quantities, query).Count == 0) && MeetingArchive.Search(quantities, "9:00").Count >= 1,
                "search does not read a quantity or a number after punctuation or after full minutes as the minutes of a time");
            Check(MeetingArchive.Notes(timedMeeting).Contains("Times mentioned\n- 12:34 · The test finished at 12:34.") && MeetingArchive.Notes(review).Contains("Times mentioned\n- Freitag · Wir haben beschlossen"), "notes list the times a meeting mentions with their segment");
            Same(MeetingVocabulary.Prompt("Priya, Lindqvist", "Design review", "Claude Code, Codex, priya", [new() { Original = "Lindquist", Replacement = "Lindqvist" }, new() { Original = "sig", Replacement = "Best regards,\nAlex Meyer, Example Institute" }]),
                "Claude Code, Codex, Design review, Priya, Lindqvist", "the meeting prompt merges corrections, dictation vocabulary, title and meeting vocabulary, the meeting's own terms last");
            Same(MeetingVocabulary.Prompt("", MeetingVocabulary.DefaultTitle, "", []), "", "the default title adds nothing to the prompt");
            Same(MeetingVocabulary.Prompt("", "Design review", "Claude Code, Okafor", []), "Claude Code, Okafor, Design review", "a meeting without its own vocabulary is prompted with the shared vocabulary and its title");
            Same(MeetingVocabulary.MergeIntoShared("Codex,", "codex, Zephyr\nPriya"), "Codex, Zephyr, Priya", "merging keeps the shared terms first and adds each new term once");
            Same(MeetingVocabulary.MergeIntoShared(" Codex; GPT ", ""), "Codex; GPT", "nothing to merge leaves the shared vocabulary as typed");
            Same(string.Join("|", VocabularyTerms.Split(" Claude Code ,Codex;  GPT\r\nZephyr,, ;\n")), "Claude Code|Codex|GPT|Zephyr", "the vocabulary list splits at commas, semicolons and line breaks, trims each term and skips empty ones");
            Check(VocabularyTerms.Split(null).Count == 0 && VocabularyTerms.Split(" , ; ").Count == 0 && VocabularyTerms.Join([]) == "", "an empty or missing vocabulary is an empty list and joins to an empty text");
            Same(string.Join("|", VocabularyTerms.Split("Zephyr, codex, Codex, Ärztekammer")), "Zephyr|codex|Codex|Ärztekammer", "loading keeps the stored order and every term, a duplicate included");
            Check(new[] { "Claude Code, Codex, GPT", "Private Flow", "Joaquín, C#/.NET, AT&T (QA), \"Lumen\", 東京, Zoë 🎙, x86-64" }.All(stored => VocabularyTerms.Join(VocabularyTerms.Split(stored)) == stored),
                "a vocabulary written as the field suggested, with unusual characters in its terms, joins back to the same text");
            string[] unusualTerms = ["Joaquín", "C#/.NET", "AT&T (QA)", "\"Lumen\"", "東京", "Zoë 🎙", "x86-64", "  spaced  out  "];
            Check(VocabularyTerms.Split(VocabularyTerms.Join(unusualTerms)).SequenceEqual(unusualTerms.Select(term => term.Trim())), "a list without separators in its terms survives joining and splitting in order");
            var (addedTerms, presentTerms) = VocabularyTerms.Additions(["Codex", "Priya"], "  Zephyr , codex; PRIYA\nLindqvist, zephyr,  ");
            Check(addedTerms.SequenceEqual(["Zephyr", "Lindqvist"]) && presentTerms.SequenceEqual(["codex", "PRIYA", "zephyr"]),
                "an entry adds its new terms once in typed order and reports those already listed, ignoring case");
            Check(VocabularyTerms.Additions(["Codex"], "  \n , ") is { Added.Count: 0, Present.Count: 0 }, "an empty entry adds and reports nothing");
            Same(string.Join(" | ", SegmentAssembly.JoinSentences([new(19.1, 20.4, "No pone las siete"), new(20.4, 21.6, "y cuarenta."), new(21.7, 24.7, "Go back."), new(24.7, 26, "Minute 6")]).Select(segment => FormattableString.Invariant($"{segment.Start}-{segment.End} {segment.Text}"))),
                "19.1-21.6 No pone las siete y cuarenta. | 21.7-24.7 Go back. | 24.7-26 Minute 6", "a segment that ends in mid-sentence takes the next one");
            int[] spokenValues =[0, 1, 7, 12, 16, 21, 34, 42, 59, 100, 101, 214, 512, 669, 816, 1000, 2025, 20000, 21000, 999999];
            Check(new[] { "en", "es", "de" }.All(language => spokenValues.All(value => TranscriptScoring.Words(NumberWords.Say(value, language)).SequenceEqual([value.ToString()]))),
                "numbers spelled in English, Spanish and German read back as their digits");
            Check(new[] { "en", "es", "de" }.All(language => new[] { (12, 34), (9, 15), (1, 30), (23, 59), (7, 45) }.All(time => TranscriptScoring.Words(NumberWords.SayTime(time.Item1, time.Item2, language)).SequenceEqual([time.Item1.ToString(), time.Item2.ToString()])))
                && NumberWords.SayTime(12, 34, "es") == "doce y treinta y cuatro" && NumberWords.SayTime(12, 34, "de") == "zwölf Uhr vierunddreißig" && NumberWords.SayTime(5, 0, "en") == "five",
                "clock times are said as a speaker of each language says them and read back as hour and minutes");
            string[] ordinaryNumbers = ["We compare at 2024 figures and at 2030 targets.", "The link runs at 100 Mbps and the pulse at 150 bpm.", "Sample at 1000 hertz, the clock is at 400 kHz.",
                "It settles at 100 milliseconds.", "The board is priced at 1250.", "Look at 205 Main Street.", "Der Umsatz stieg um 150 auf 900.", "Um 2030 herum ist es fertig.", "It finished at 1149."];
            var numbersMeeting = new MeetingDocument { Title = "Numbers", Segments = [.. ordinaryNumbers.Select((text, index) => new MeetingSegment { Start = index, Text = text })] };
            Check(MeetingArchive.ExportText(numbersMeeting).Split('\n').Select(line => line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..]).SequenceEqual(ordinaryNumbers)
                && MeetingArchive.Search([numbersMeeting], "11:49").Select(hit => hit.Segment.Start).SequenceEqual([8.0]) && MeetingArchive.Notes(numbersMeeting).Contains("- 11:49 · It finished at 1149."),
                "numbers in a meeting stay as recognized; a time written without its colon is still found by search and listed in the notes");
            var mixedTerms = new MeetingDocument { Title = "Terms", Segments = [new() { Start = 1, Text = "We have two options." }, new() { Start = 2, Text = "Version 1.2.3 is out." }, new() { Start = 3, Text = "There are four of us." },
                new() { Start = 4, Text = "I need three things." }, new() { Start = 5, Text = "Das ist ein Problem." }, new() { Start = 6, Text = "Es gibt eine Lösung." }, new() { Start = 7, Text = "The I2C bus and the M4 core render in 3D." },
                new() { Start = 8, Text = "Up to 40mm in the mountains." }, new() { Start = 9, Text = "The final starts tomorrow at 8 15 8 15 tomorrow yes" }] };
            string StartsFound(string query) => string.Join(",", MeetingArchive.Search([mixedTerms], query).Select(hit => hit.Segment.Start).Order());
            Same(string.Join(" | ", new[] { "I2C", "M4", "3D", "ein Problem", "one", "two", "40", "forty", "8:15" }.Select(StartsFound)), "7 | 7 | 7 | 5 | 2 | 1,2 | 8 | 8 | 9",
                "names with digits are searched whole; the article ein is not the number one; numbers still find their digits and words, a repeated time too");
            var compactTerms = new MeetingDocument { Title = "Terms", Segments = [new() { Start = 1, Text = "We start at 9 a.m. tomorrow." }, new() { Start = 2, Text = "The ride is 10 km long." },
                new() { Start = 3, Text = "It answers in 20 ms." }, new() { Start = 4, Text = "Wir treffen uns um ein Uhr dreißig." }, new() { Start = 5, Text = "Das ist ein Problem." }] };
            Same(string.Join(" | ", new[] { "9am", "10km", "20ms", "1:30", "130", "to get her" }.Select(query => string.Join(",", MeetingArchive.Search([compactTerms], query).Select(hit => hit.Segment.Start)))), "1 | 2 | 3 | 4 | 4 | ",
                "a compact query finds its spaced form; a German time with the article ein is found as digits");
            helperArchive.Save(review);
            File.WriteAllBytes(helperArchive.ChunkPath(review.Id, "mic-000002.wav"), new byte[44 + 32000]);
            File.WriteAllBytes(helperArchive.ChunkPath(review.Id, "system-000000.wav"), new byte[20]);
            helperArchive.RecoverUnlistedChunks(review);
            var recovered = helperArchive.Load(review.Id).Chunks;
            Check(recovered.Count == 1 && recovered[0] is { File: "mic-000002.wav", Channel: "mic", Offset: 40.0, Duration: 1.0, Transcribed: false }, "a chunk saved before its manifest is recovered with an estimated offset; a truncated file is skipped");
            File.WriteAllBytes(helperArchive.ChunkPath(review.Id, "mic-000003.wav"), new byte[44 + 64000]);
            helperArchive.RecoverUnlistedChunks(review);
            Check(helperArchive.Load(review.Id).Chunks.Single(chunk => chunk.File == "mic-000003.wav") is { Offset: 41.0, Duration: 2.0 }, "a recovered chunk follows the end of the chunk before it");
            File.WriteAllText(Path.Combine(helperArchive.DirectoryFor(review.Id), "meeting.json"),
                "{\"Id\":\"" + review.Id + "\",\"Chunks\":[{\"File\":\"mic-000000.wav\",\"Channel\":\"mic\",\"Offset\":-1,\"Duration\":1}],\"Segments\":[]}");
            Reject(() => helperArchive.Load(review.Id), "a manifest with a negative chunk time is rejected");
        }
        finally { Directory.Delete(helperRoot, true); }
    }

    public static void VerifyArchiveAudio()
    {
        var audioRoot = Path.Combine(Path.GetTempPath(), "PrivateFlow-archive-audio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var audioArchive = new MeetingArchive(audioRoot);
            byte[] Wave(int dataBytes)
            {
                var bytes = new byte[44 + dataBytes];
                System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0); BitConverter.GetBytes(36 + dataBytes).CopyTo(bytes, 4); System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
                return bytes;
            }
            MeetingDocument Meeting(string status, bool retain, params string[] files)
            {
                var document = new MeetingDocument { Status = status, RetainAudio = retain }; audioArchive.Save(document);
                foreach (var file in files) File.WriteAllBytes(Path.Combine(audioArchive.DirectoryFor(document.Id), file), Wave(32000));
                return document;
            }
            var finished = Meeting("Complete", false, "mic-000000.wav", "system-000001.wav.partial");
            var retained = Meeting("Complete", true, "mic-000000.wav");
            var interrupted = Meeting("Interrupted", false, "mic-000000.wav", "mic-000001.wav.partial");
            var crashedWhileRecording = Meeting("Recording", false, "system-000000.wav");
            var crashedWhileFinishing = Meeting("Finalizing", false, "mic-000000.wav");
            var previewOnly = Meeting("Interrupted", false);
            File.WriteAllText(Path.Combine(audioArchive.DirectoryFor(interrupted.Id), "notes.txt"), "not audio");
            Check(audioArchive.DeleteLeftoverAudio(audioArchive.List()) == 2 && audioArchive.AudioBytes(finished.Id) == 0 && audioArchive.AudioBytes(retained.Id) > 0 && audioArchive.AudioBytes(interrupted.Id) == 2 * (44 + 32000)
                && audioArchive.AudioBytes(crashedWhileRecording.Id) > 0 && audioArchive.AudioBytes(crashedWhileFinishing.Id) > 0,
                "audio left in complete meetings without retention is deleted, including unfinished chunk writes; retained and unfinished meetings keep theirs");
            var unfinishedIds = audioArchive.Unfinished().Select(found => found.Document.Id).ToHashSet();
            Check(unfinishedIds.SetEquals([interrupted.Id, crashedWhileRecording.Id, crashedWhileFinishing.Id]) && !audioArchive.Unfinished(crashedWhileRecording.Id).Any(found => found.Document.Id == crashedWhileRecording.Id)
                && audioArchive.Unfinished().Single(found => found.Document.Id == interrupted.Id).AudioBytes == 2 * (44 + 32000),
                "interrupted meetings and those a crash left recording or finalizing are offered for finishing with their audio size; one without audio and the one recording now are not");
            // The History page reads every meeting file once at start and passes the list on, so a meeting file damaged after that read changes
            // nothing, where reading the files again would skip it.
            var stored = audioArchive.List();
            File.WriteAllBytes(Path.Combine(audioArchive.DirectoryFor(finished.Id), "mic-000002.wav"), Wave(32000));
            var damaged = new[] { finished.Id, crashedWhileFinishing.Id }.Select(id => Path.Combine(audioArchive.DirectoryFor(id), "meeting.json")).ToDictionary(file => file, File.ReadAllText);
            foreach (var file in damaged.Keys) File.WriteAllText(file, "{");
            var fromStored = audioArchive.Unfinished(stored).Select(found => found.Document.Id).ToHashSet(); var reread = audioArchive.Unfinished().Select(found => found.Document.Id).ToHashSet();
            var leftoverFromStored = audioArchive.DeleteLeftoverAudio(stored);
            foreach (var (file, text) in damaged) File.WriteAllText(file, text);
            Check(fromStored.SetEquals(unfinishedIds) && !reread.Contains(crashedWhileFinishing.Id) && audioArchive.Unfinished(stored, crashedWhileRecording.Id).All(found => found.Document.Id != crashedWhileRecording.Id)
                && leftoverFromStored == 1 && audioArchive.AudioBytes(finished.Id) == 0 && audioArchive.AudioBytes(retained.Id) > 0,
                "the unfinished meetings and the leftover audio come from the meetings already read, without reading a meeting file again");
            File.WriteAllBytes(Path.Combine(audioArchive.DirectoryFor(interrupted.Id), "mic-000002.wav.partial"), new byte[44 + 32000]);
            audioArchive.RecoverUnlistedChunks(interrupted);
            var recoveredChunks = audioArchive.Load(interrupted.Id).Chunks.Select(chunk => chunk.File).ToArray();
            Check(recoveredChunks.SequenceEqual(["mic-000000.wav", "mic-000001.wav"]) && File.Exists(Path.Combine(audioArchive.DirectoryFor(interrupted.Id), "mic-000002.wav.partial")),
                "a chunk write that completed before a crash is recovered from its partial file; an incomplete one is left alone");
            audioArchive.DeleteAudio(interrupted.Id);
            Check(audioArchive.AudioBytes(interrupted.Id) == 0 && File.Exists(Path.Combine(audioArchive.DirectoryFor(interrupted.Id), "notes.txt")) && audioArchive.Load(interrupted.Id).Status == "Interrupted"
                && !audioArchive.Unfinished().Any(found => found.Document.Id == interrupted.Id), "deleting a meeting's audio removes every chunk and partial write, keeps the record and ends the offer to finish it");
            var devices = new MeetingDocument { MicrophoneDevice = "Mikrofon (USB Audio)", PlaybackDevice = "Lautsprecher (USB Audio)", Status = "Complete",
                Chunks = [new() { File = "mic-000000.wav", Channel = "mic", Duration = 20, Peak = 0 }, new() { File = "mic-000001.wav", Channel = "mic", Offset = 20, Duration = 20, Peak = 0.00003 },
                    new() { File = "system-000000.wav", Channel = "system", Duration = 20, Peak = 0.4 }],
                Segments = [new() { Start = 1, End = 2, Speaker = "Remote speaker", Channel = "system", Text = "We meet at 12:30." }] };
            audioArchive.Save(devices); var reloaded = audioArchive.Load(devices.Id);
            Check(reloaded.MicrophoneDevice == "Mikrofon (USB Audio)" && reloaded.PlaybackDevice == "Lautsprecher (USB Audio)" && reloaded.Chunks[1].Peak == 0.00003, "the meeting record keeps its capture and playback devices and each chunk's peak level");
            Check(MeetingArchive.SilentChannels(reloaded).SequenceEqual(["mic"]) && MeetingArchive.Notes(reloaded).StartsWith("The microphone “Mikrofon (USB Audio)” delivered no sound during the whole meeting")
                && MeetingArchive.Notes(reloaded).Contains("Times mentioned\n- 12:30 · We meet at 12:30."), "a microphone below the silence level in every chunk is reported in the notes, which keep their sections");
            reloaded.Chunks[1].Peak = 0.01; reloaded.Chunks.RemoveAt(2);
            Check(MeetingArchive.SilentChannels(reloaded).SequenceEqual(["system"]), "one audible chunk clears the microphone; system audio that recorded nothing at all is reported");
            Check(MeetingArchive.SilentChannels(new MeetingDocument { MicrophoneDevice = "Old", Chunks = [new() { File = "mic-000000.wav", Channel = "mic" }] }).Count == 0
                && MeetingArchive.SilentChannels(new MeetingDocument { Chunks = [new() { File = "mic-000000.wav", Channel = "mic", Peak = 0 }] }).Count == 0,
                "meetings saved before levels or devices were recorded are not reported as silent");
            var gap = new MeetingDocument { Chunks = [new() { File = "mic-000000.wav", Channel = "mic", Duration = 20, Transcribed = true }, new() { File = "mic-000001.wav", Channel = "mic", Offset = 20, Duration = 19.2 },
                new() { File = "mic-000002.wav", Channel = "mic", Offset = 40, Duration = 20 }] };
            audioArchive.Save(gap); File.WriteAllBytes(audioArchive.ChunkPath(gap.Id, "mic-000002.wav"), [0]);
            gap.MissingAudioSeconds = MeetingArchive.MissingAudioSeconds(gap, gap.Chunks.Where(chunk => File.Exists(audioArchive.ChunkPath(gap.Id, chunk.File))).ToHashSet());
            Check(gap.MissingAudioSeconds == 19.2 && MeetingArchive.Notes(gap).StartsWith("20 s of the recorded audio was missing when the transcript was finished")
                && !MeetingArchive.Caveats(new MeetingDocument()).Contains("missing"),
                "audio missing at the final pass without a live preview is counted and named in the notes; missing audio with a preview is not");
            gap.WrittenNotes = new() { Text = "Written by test-model. Summary\nWe ship.", Model = "test-model", Transcript = "" };
            Check(MeetingHistory.Notes(gap).StartsWith("20 s of the recorded audio was missing") && MeetingHistory.ExportedNotes(gap).Contains("20 s of the recorded audio was missing")
                && MeetingHistory.WithMissingAudio(gap, MeetingHistory.Notes(gap)) == MeetingHistory.Notes(gap),
                "notes a model wrote before the gap was found show it before them and in exports, once");
            var old = new MeetingDocument(); audioArchive.Save(old);
            File.WriteAllText(Path.Combine(audioArchive.DirectoryFor(old.Id), "meeting.json"), "{\"Id\":\"" + old.Id + "\",\"Status\":\"Interrupted\",\"Chunks\":[{\"File\":\"mic-000000.wav\",\"Channel\":\"mic\",\"Offset\":0,\"Duration\":1}],\"Segments\":[]}");
            Check(audioArchive.Load(old.Id) is { MicrophoneDevice: "", PlaybackDevice: "" } loadedOld && loadedOld.Chunks[0].Peak is null, "a meeting record from an earlier version loads without devices and levels");
            // A channel's chunks read as one recording (ChannelAudio): two adjacent seconds, half a second of gap, then a second listed whose
            // file a crash cut to 0.75 s. Every sample read, across each boundary and past the end, is the one its file holds, zero elsewhere.
            var channelFolder = Path.Combine(audioRoot, "channel"); Directory.CreateDirectory(channelFolder);
            var timeline = new short[56000];
            (string File, double Offset, double Duration) ChannelChunk(string name, double offset, double duration, int samples)
            {
                var values = Enumerable.Range(0, samples).Select(index => (short)((name[^1] - '0' + 1) * 7919 + index * 13 % 30011 - 15000)).ToArray();
                values.CopyTo(timeline, (int)(offset * 16000));
                var file = Path.Combine(channelFolder, name + ".wav");
                using (var writer = new NAudio.Wave.WaveFileWriter(file, new NAudio.Wave.WaveFormat(16000, 16, 1))) writer.WriteSamples(values, 0, values.Length);
                return (file, offset, duration);
            }
            var second = ChannelChunk("mic-1", 1, 1, 16000); var cut = ChannelChunk("mic-2", 2.5, 1, 12000); var first = ChannelChunk("mic-0", 0, 1, 16000);
            var channel = new PrivateFlow.Windows.ChannelAudio([second, cut, first]);
            bool Reads(int start, int count) => channel.Read(start, count).SequenceEqual(Enumerable.Range(start, count).Select(index => index < timeline.Length ? timeline[index] / 32768f : 0f));
            (int Start, int Count)[] reads = [(0, 56000), (15990, 20), (0, 16000), (31000, 10000), (33000, 4000), (50000, 8000), (60000, 100), (39999, 2)];
            var wrongReads = reads.Where(read => !Reads(read.Start, read.Count)).ToArray();
            Check(channel.Length == 56000 && wrongReads.Length == 0 && channel.Read(0.99, 1.01).SequenceEqual(channel.Read(15840, 320)),
                $"a channel's chunks read as one recording across chunk boundaries, a gap and a file shorter than listed, the gap and the missing end as zeros (wrong: {string.Join(", ", wrongReads)})");
        }
        finally { Directory.Delete(audioRoot, true); }
    }

    // The archive never follows a link or junction out of its folder: deleting a meeting whose folder is, or holds, a link to
    // somewhere else refuses and leaves that place alone. On Windows the link is a junction, which needs no administrator rights.
    public static void VerifyArchiveLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-archive-links-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "PrivateFlow-outside-" + Guid.NewGuid().ToString("N"));
        void Link(string link, string target)
        {
            if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
            using var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            junction.WaitForExit();
            if (junction.ExitCode != 0) throw new IOException("mklink /J failed.");
        }
        try
        {
            Directory.CreateDirectory(outside);
            var kept = Path.Combine(outside, "keep.txt"); File.WriteAllText(kept, "outside the archive");
            var archive = new MeetingArchive(root);
            var linkedMeeting = Guid.NewGuid().ToString("N");
            Link(archive.DirectoryFor(linkedMeeting), outside);
            string? refusal = null;
            try { archive.Delete(linkedMeeting); } catch (InvalidOperationException error) { refusal = error.Message; }
            Check(refusal == "Meeting archive cannot follow links or junctions." && File.Exists(kept), "deleting a meeting whose folder is a link refuses and leaves the linked folder alone");
            var nestedMeeting = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(archive.DirectoryFor(nestedMeeting));
            File.WriteAllText(Path.Combine(archive.DirectoryFor(nestedMeeting), "meeting.json"), "{}");
            Link(Path.Combine(archive.DirectoryFor(nestedMeeting), "chunks"), outside);
            refusal = null;
            try { archive.Delete(nestedMeeting); } catch (InvalidOperationException error) { refusal = error.Message; }
            Check(refusal is not null && File.Exists(kept) && File.Exists(Path.Combine(archive.DirectoryFor(nestedMeeting), "meeting.json")),
                "deleting a meeting that holds a link refuses before deleting anything, inside or outside the archive");
            var linkedRoot = root + "-link";
            Link(linkedRoot, outside);
            try { _ = new MeetingArchive(linkedRoot); Check(false, "an archive folder that is a link is refused"); }
            catch (InvalidOperationException) { Check(File.Exists(kept), "an archive folder that is a link is refused"); }
            finally { Directory.Delete(linkedRoot); }
        }
        finally
        {
            // The links go first, without following them, so removing the archive cannot reach the folder they point to.
            foreach (var link in Directory.Exists(root) ? Directory.GetDirectories(root).SelectMany(meeting => Directory.GetDirectories(meeting).Prepend(meeting)).ToArray() : [])
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }
}
