using PrivateFlow.Core;
using static Fixtures;
using static Harness;

static class OutputStyleChecks
{
    public static void VerifyTextClean()
    {
        var critical = "Do not delete C:\\Data\\file_2.txt. Cost €12.50, deadline 03/10, --no-cache.";
        Check(TextCleanup.Clean("  " + critical + "  ") == critical, "cleanup preserves negation, paths, numbers and flags");
        Check(TextCleanup.Clean("um\t  hello hello") == "um hello hello", "cleanup never silently removes speech words");
        Check(TextCleanup.Clean("first\nsecond") == "first\nsecond", "cleanup preserves line boundaries");
        Same(TextCleanup.RemoveFillers("Um, so I think, uh, we should ship it."), "So I think we should ship it.", "Clean removes English fillers");
        Same(TextCleanup.RemoveFillers("Also ähm ich glaube äh das passt."), "Also ich glaube das passt.", "Clean removes German fillers");
        Same(TextCleanup.RemoveFillers("Wir treffen uns um 5 Uhr."), "Wir treffen uns um 5 Uhr.", "German preposition um survives");
        Same(TextCleanup.RemoveFillers("I I think the the build is fine."), "I think the build is fine.", "Clean collapses stutters");
        Same(TextCleanup.RemoveFillers("I had had enough."), "I had had enough.", "grammatical doubles survive");
        Same(TextCleanup.RemoveFillers("Do not, um, delete C:\\Data\\file_2.txt, it costs €12.50 and is 5 mm wide."),
            "Do not delete C:\\Data\\file_2.txt, it costs €12.50 and is 5 mm wide.", "Clean preserves negation, paths, numbers and units");
        Same(TextCleanup.RemoveFillers("You know, it works. I mean it."), "It works. I mean it.", "phrase fillers only when set off by commas");
        Same(TextCleanup.RemoveFillers("Das ist eh egal, hmm."), "Das ist eh egal.", "German eh (anyway) survives while hmm is removed");
        Same(TextCleanup.RemoveFillers("uh"), "", "filler-only speech becomes empty");
        // Review findings on PR #2: units, file names, paths and negations must survive Clean.
        Same(TextCleanup.RemoveFillers("Die Kapazität beträgt 5 Ah."), "Die Kapazität beträgt 5 Ah.", "ampere-hour unit after a number survives");
        Same(TextCleanup.RemoveFillers("Do not modify ah.txt."), "Do not modify ah.txt.", "file name that looks like a filler survives");
        Same(TextCleanup.RemoveFillers("Bitte benenne /tmp/um, in /tmp/out um."), "Bitte benenne /tmp/um, in /tmp/out um.", "path segment that looks like a filler survives");
        Same(TextCleanup.RemoveFillers("Run it with --uh, then stop."), "Run it with --uh, then stop.", "flag that looks like a filler survives");
        Same(TextCleanup.RemoveFillers("It's not not safe."), "It's not not safe.", "English double negation survives");
        Same(TextCleanup.RemoveFillers("Das ist nicht nicht wichtig, kein kein Problem."), "Das ist nicht nicht wichtig, kein kein Problem.", "German double negation survives");
        Same(TextCleanup.RemoveFillers("Ah, I see, hmm, ah okay."), "I see okay.", "standalone fillers away from numbers are still removed");
        // Round 2: technical tokens are protected through every stage, negations by form.
        Same(TextCleanup.RemoveFillers("Open uh+backup.txt."), "Open uh+backup.txt.", "file name with a filler stem survives");
        Same(TextCleanup.RemoveFillers("Open C:/Duran Duran/track.txt."), "Open C:/Duran Duran/track.txt.", "path with a repeated segment is not treated as a stutter");
        Same(TextCleanup.RemoveFillers("Die Kapazität [Ah] bleibt gleich."), "Die Kapazität [Ah] bleibt gleich.", "bracketed unit label survives");
        Same(TextCleanup.RemoveFillers("config.json is fine."), "config.json is fine.", "file name at sentence start keeps its case");
        Same(TextCleanup.RemoveFillers("uh, config.json is fine."), "config.json is fine.", "filler before a file name is removed without changing the file name");
        Same(TextCleanup.RemoveFillers("It wasn't wasn't safe."), "It wasn't wasn't safe.", "repeated n't contraction survives");
        Same(TextCleanup.RemoveFillers("I didn’t didn’t delete it."), "I didn’t didn’t delete it.", "repeated typographic n’t contraction survives");
        Same(TextCleanup.RemoveFillers("Ich habe niemanden niemanden gesehen."), "Ich habe niemanden niemanden gesehen.", "German negative inflection survives");
        Same(TextCleanup.RemoveFillers("Das ist keineswegs keineswegs falsch."), "Das ist keineswegs keineswegs falsch.", "German kein- negation survives");
        Same(TextCleanup.RemoveFillers("We we should, um, ship version 2.1 today."), "We should ship version 2.1 today.", "stutter and filler still removed around a version number");
        // Round 3: whole path spans, punctuated hesitations after numbers, linear time, sentence starts.
        Same(TextCleanup.RemoveFillers("Open \"C:/Projects/release uh notes/report.txt\"."), "Open \"C:/Projects/release uh notes/report.txt\".", "quoted path with spaces is protected whole");
        Same(TextCleanup.RemoveFillers("Open \"C:/Projects/release um, notes/report.txt\"."), "Open \"C:/Projects/release um, notes/report.txt\".", "quoted path with a punctuated um is protected whole");
        Same(TextCleanup.RemoveFillers("Open C:/Duran Duran Duran Duran/track.txt."), "Open C:/Duran Duran Duran Duran/track.txt.", "unquoted path with spaces is protected whole");
        Same(TextCleanup.RemoveFillers("Copy /tmp/a uh to /tmp/b."), "Copy /tmp/a to /tmp/b.", "prose between two separate paths stays editable");
        Same(TextCleanup.RemoveFillers("I need 5, uh, copies."), "I need 5 copies.", "punctuated hesitation after a number is removed");
        Same(TextCleanup.RemoveFillers("There are 5, um, copies."), "There are 5 copies.", "punctuated um after a number is removed");
        Same(TextCleanup.RemoveFillers("Es sind 5, ähm, Kopien."), "Es sind 5 Kopien.", "German punctuated hesitation after a number is removed");
        Same(TextCleanup.RemoveFillers("Use 5 Ah. then stop."), "Use 5 Ah. Then stop.", "unit before a sentence end keeps capitalization of the next sentence");
        Same(TextCleanup.RemoveFillers("Open config.json. then stop."), "Open config.json. Then stop.", "file name before a sentence end keeps capitalization of the next sentence");
        Same(TextCleanup.RemoveFillers("It costs 5. then stop."), "It costs 5. Then stop.", "number before a sentence end keeps capitalization of the next sentence");
        // Round 4: units before punctuation, quotes of any length, case-sensitive unit symbols.
        Same(TextCleanup.RemoveFillers("Die Kapazität beträgt 5 Ah, die Spannung 12 V."), "Die Kapazität beträgt 5 Ah, die Spannung 12 V.", "unit before a comma survives");
        Same(TextCleanup.RemoveFillers("Use 5 Ah… then stop."), "Use 5 Ah… then stop.", "unit before an ellipsis survives");
        Same(TextCleanup.RemoveFillers("I need 5 uh, copies."), "I need 5 copies.", "hesitation word after a number is still removed");
        const string longPath = "\"C:/Projects/this is a very long release directory with uh all the final approved customer facing documentation files/report.txt\"";
        Same(TextCleanup.RemoveFillers("Open " + longPath + "."), "Open " + longPath + ".", "quoted path longer than 16 words is protected whole");
        Same(TextCleanup.RemoveFillers("Öffne „C:/Projekte/ein sehr langer Ordner mit äh vielen Wörtern für die finale Dokumentation der Kunden/bericht.txt“."),
            "Öffne „C:/Projekte/ein sehr langer Ordner mit äh vielen Wörtern für die finale Dokumentation der Kunden/bericht.txt“.", "long German-quoted path is protected whole");
        Same(TextCleanup.RemoveFillers("He said \"uh, I think so\" and left."), "He said \"I think so\" and left.", "quoted prose without technical content stays editable");
        Same(TextCleanup.RemoveFillers("Open \"C:/unclosed uh path and more."), "Open \"C:/unclosed uh path and more.", "unclosed quote falls back to the path span");
        Same(TextCleanup.RemoveFillers("mAh is the unit."), "mAh is the unit.", "standalone mAh keeps its milli prefix");
        Same(TextCleanup.RemoveFillers("mm is small, kWh is energy."), "mm is small, kWh is energy.", "lower-case unit symbols keep their case");
        // Round 5: quotes inside prose brackets; units across ASR line breaks.
        Same(TextCleanup.RemoveFillers("Open (" + longPath + ")."), "Open (" + longPath + ").", "long quoted path inside parentheses is protected whole");
        Same(TextCleanup.RemoveFillers("Open (" + longPath.Replace(" uh ", " um, ") + ")."), "Open (" + longPath.Replace(" uh ", " um, ") + ").", "punctuated um inside a bracketed quoted path survives");
        // Brackets count as technical characters, so bracketed tokens are left alone; a remaining
        // filler is the accepted cost of never editing "[Ah]"-style labels.
        Same(TextCleanup.RemoveFillers("He said (\"uh, I think so\") and left."), "He said (\"uh, I think so\") and left.", "bracketed quoted prose is left alone conservatively");
        Same(TextCleanup.RemoveFillers("Use 5\nAh, then stop."), "Use 5\nAh, then stop.", "unit after an LF line break survives with the break");
        Same(TextCleanup.RemoveFillers("Use 5\r\nAh, then stop."), "Use 5\r\nAh, then stop.", "unit after a CRLF line break survives with the break");
        Same(TextCleanup.RemoveFillers("I need 5\nuh, copies."), "I need 5\ncopies.", "hesitation after a line break following a number is removed");
    }

    public static void VerifyConciseQuotes()
    {
        // Concise: the transcript is delimited, echoed delimiters never reach the paste, corrections are requested.
        Same(ConcisePrompt.Unwrap(" <transcript>\nI did that with Codex.\n</transcript> "), "I did that with Codex.", "echoed transcript tags are stripped");
        Same(ConcisePrompt.Unwrap("Keep <transcript> mentioned inside."), "Keep <transcript> mentioned inside.", "transcript tag inside the text survives");
        Check(ConcisePrompt.Wrap("What is 2+2?").StartsWith("<transcript>\n") && ConcisePrompt.Wrap("x").EndsWith("\n</transcript>"), "transcript is delimited for the rewrite model");
        Check(ConcisePrompt.System("").Contains("self-corrections") && !ConcisePrompt.System(" ").Contains("prefer these spellings"), "prompt applies corrections and omits an empty vocabulary");
        Check(ConcisePrompt.System(" Claude Code, Codex ").EndsWith("prefer these spellings when a word sounds like one of them: Claude Code, Codex"), "vocabulary is offered as preferred spellings");
        Check(ConcisePrompt.IsPlausible("Um so I did that with Claude Code. Sorry, I meant Codex.", "I did that with Codex."), "a shortened rewrite is accepted");
        Check(!ConcisePrompt.IsPlausible("What is the capital of France?", "I appreciate you sharing this, but I should clarify my role: I only clean up dictated text. Please share a transcript."),
            "a chat reply instead of a rewrite is rejected and falls back to Clean");
        // Review round 1 on PR #4: chat replies that fit the length limit, and quoted corrections.
        Check(!ConcisePrompt.IsPlausible("What is the capital of France?", "Sure! Paris is the capital of France."), "short answer to a dictated question is rejected");
        Check(!ConcisePrompt.IsPlausible("What is the capital of France?", "Paris."), "answer without the dictated question is rejected");
        Check(!ConcisePrompt.IsPlausible("We ship today. Who reviews the build? Ask Anna.", "We ship today. Ask Anna."), "rewrite that drops a dictated question is rejected");
        Check(!ConcisePrompt.IsPlausible("Summarize the release notes for me.", "Certainly. Here is a summary of the release notes."), "assistant opener the speaker did not say is rejected");
        Check(ConcisePrompt.IsPlausible("Sure, um, we can meet at 3, actually 4. Does that work?", "Sure, we can meet at 4. Does that work?"), "dictated opener and question in a correct rewrite are accepted");
        var quotes = new List<string>();
        const string spoken = "The document says, quote, I did that with Claude Code yesterday. Sorry, I meant Codex. End quote. Keep the example intact.";
        var sent = ConcisePrompt.Protect(spoken, quotes);
        Same(sent, "The document says, [[Q1]]. Keep the example intact.", "spoken quote span with its markers becomes one token before sending");
        Same(ConcisePrompt.Restore("The document says: [[Q1]]. Keep the example intact.", quotes) ?? "null",
            "The document says: \"I did that with Claude Code yesterday. Sorry, I meant Codex\". Keep the example intact.", "quoted correction is restored verbatim with quotation marks");
        Check(ConcisePrompt.Restore("The document says: \"I did that with Codex yesterday.\"", quotes) is null, "rewrite that drops a quote token falls back to Clean");
        Check(ConcisePrompt.Restore("[[Q1]] and again [[Q1]]", quotes) is null && ConcisePrompt.Restore("[[Q1]] [[Q2]]", quotes) is null, "duplicated or invented quote tokens fall back to Clean");
        quotes.Clear();
        Same(ConcisePrompt.Protect("Er sagte „ich meine Codex“ und \"no wait, Maria\" sowie Zitat, nein, Anna. Zitat Ende.", quotes),
            "Er sagte [[Q1]] und [[Q2]] sowie [[Q3]].", "literal and German spoken quotes are protected");
        Same(string.Join("|", quotes), "„ich meine Codex“|\"no wait, Maria\"|„nein, Anna“", "tokens restore literal quotes unchanged and spoken ones with matching marks");
        // Owner test run 2026-10-06: Whisper wrote spoken markers around literal quotes, and the model
        // pulled the next dictated question into the quote.
        const string mixedQuote = "He said quote \"never trust a number you didn't measure\" end quote. Can you remind me on Friday at 9.30 to send the report to Dr. Meyer?";
        quotes.Clear();
        var mixedSent = ConcisePrompt.Protect(mixedQuote, quotes);
        Same(mixedSent, "He said [[Q1]]. Can you remind me on Friday at 9.30 to send the report to Dr. Meyer?", "spoken markers around literal quotes leave one token");
        Same(quotes.Count == 1 ? quotes[0] : "count " + quotes.Count, "\"never trust a number you didn't measure\"", "mixed quote keeps only the quoted words and one pair of marks");
        Check(!ConcisePrompt.IsPlausible(mixedSent, "He said [[Q1]] and \"can you remind me on Friday at 9.30 to send the report to Dr. Meyer?\"."),
            "rewrite that quotes the dictated question falls back to Clean");
        // Review of PR #8: the token carries its own marks, so a mark moved past the next question is new.
        Check(!ConcisePrompt.IsPlausible(mixedSent, "He said \"[[Q1]]. Can you remind me on Friday at 9.30 to send the report to Dr. Meyer?\""),
            "closing mark moved past the next question falls back to Clean");
        Check(ConcisePrompt.IsPlausible(mixedSent, "He said [[Q1]]. Can you remind me on Friday at 9:30 to send the report to Dr. Meyer?"), "rewrite that keeps the quote token is accepted");
        quotes.Clear();
        var nestedSent = ConcisePrompt.Protect("He said quote He said \"no wait, Maria\" and left end quote. Continue.", quotes);
        Check(nestedSent == "He said [[Q1]]. Continue." && quotes.Count == 1 && quotes[0] == "“He said \"no wait, Maria\" and left”" &&
            ConcisePrompt.Restore("He said [[Q1]]. Continue.", quotes) == "He said “He said \"no wait, Maria\" and left”. Continue.",
            "literal quote inside a spoken quote stays in one protected token, so its correction cannot be applied");
        quotes.Clear();
        Same(ConcisePrompt.Protect("Open \"C:\\release quote notes end quote\\report.txt\".", quotes), "Open [[Q1]].", "quoted path containing quote markers is protected whole");
        Same(TextCleanup.RemoveFillers("Open \"C:\\release quote notes end quote\\report.txt\"."), "Open \"C:\\release quote notes end quote\\report.txt\".", "Clean keeps quote words inside a quoted path");
        Same(TextCleanup.RemoveFillers("Open C:/release quote notes end quote/report.txt."), "Open C:/release quote notes end quote/report.txt.", "Clean keeps quote words inside an unquoted path");
        Same(TextCleanup.RemoveFillers("Use --quote text end quote."), "Use --quote text end quote.", "Clean keeps a --quote flag");
        Same(TextCleanup.RemoveFillers("He said quote \"C:/a b/c.txt\" end quote."), "He said \"C:/a b/c.txt\".", "spoken quote around a quoted path keeps one pair of marks");
        // Review round 2 of PR #8: nested quotes at the edges keep their marks; unquoted paths with
        // spaces keep folder words that sound like markers.
        Same(TextCleanup.RemoveFillers("He said quote He said \"no wait, Maria\" end quote. Continue."), "He said “He said \"no wait, Maria\"”. Continue.", "nested quote at the end of a spoken quote keeps its marks");
        Same(TextCleanup.RemoveFillers("He said quote \"no wait, Maria\" and left end quote."), "He said “\"no wait, Maria\" and left”.", "nested quote at the start of a spoken quote keeps its marks");
        Same(TextCleanup.RemoveFillers("Er sagte Zitat „nein, Anna“ und ging Zitat Ende."), "Er sagte „„nein, Anna“ und ging“.", "German nested quote at the edge keeps its marks");
        Same(TextCleanup.RemoveFillers("Er sagte Zitat „nein, Anna“ Zitat Ende."), "Er sagte „nein, Anna“.", "German marks wrapping the whole spoken quote are not doubled");
        quotes.Clear();
        Same(ConcisePrompt.Restore(ConcisePrompt.Protect("He said quote He said \"no wait, Maria\" end quote. Continue.", quotes), quotes) ?? "null",
            "He said “He said \"no wait, Maria\"”. Continue.", "Concise restores an edge-nested quote with all its marks");
        foreach (var path in new[] { "C:/release quote notes end quote final/report.txt", "C:\\release quote notes end quote final\\report.txt" })
        {
            Same(TextCleanup.RemoveFillers($"Open {path} now."), $"Open {path} now.", "Clean keeps marker words inside an unquoted path with spaces");
            quotes.Clear();
            Same(ConcisePrompt.Restore(ConcisePrompt.Protect($"Open {path} now.", quotes), quotes) ?? "null", $"Open {path} now.", "Concise keeps marker words inside an unquoted path with spaces");
        }
        // Timing checks catch catastrophic backtracking, not small slowdowns. The shared CI guest runs this
        // code about 60 times slower than a desktop (110 ms here, 6.5 s there for the same linear input),
        // while the quadratic cases these checks were written for took 25 s on a desktop.
        var unclosed = string.Concat(Enumerable.Repeat("quote word ", 6000));
        // Warm up first: the bound tests linear scaling, not JIT and regex construction. The work takes
        // about 70 ms here but 2-3.4 s on the shared CI guest; a quadratic regression at 66k characters
        // takes minutes, so 10 s still catches it without failing on a slow guest.
        TextCleanup.RemoveFillers("quote word end quote."); ConcisePrompt.Protect("quote word end quote.", new List<string>());
        var quoteTimer = System.Diagnostics.Stopwatch.StartNew();
        Check(TextCleanup.RemoveFillers(unclosed).Length > 0 && ConcisePrompt.Protect(unclosed, new List<string>()) == unclosed &&
            ConcisePrompt.Protect(unclosed + "end quote.", new List<string>()) == "[[Q1]]." && quoteTimer.Elapsed < TimeSpan.FromSeconds(10),
            $"66k characters of unclosed or one long spoken quote clean in linear time ({quoteTimer.ElapsedMilliseconds} ms)");
        // Review round 3 of PR #8: a marker rejected inside a path must not swallow a later quotation,
        // and a long quotation stays protected.
        quotes.Clear();
        Same(ConcisePrompt.Protect("Open C:/release quote notes/report.txt. He said quote \"no wait, Maria\" end quote. Continue.", quotes),
            "Open C:/release quote notes/report.txt. He said [[Q1]]. Continue.", "path-owned start marker does not swallow the following quotation");
        Same(TextCleanup.RemoveFillers("Open C:/release quote notes/report.txt. He said quote no wait, Maria, end quote. Continue."),
            "Open C:/release quote notes/report.txt. He said \"no wait, Maria\". Continue.", "Clean converts a quotation after a path-owned marker");
        quotes.Clear();
        Same(ConcisePrompt.Protect("He said quote see C:/release end quote notes/report.txt now end quote. Continue.", quotes) + " | " + string.Join("|", quotes),
            "He said [[Q1]]. Continue. | \"see C:/release end quote notes/report.txt now\"", "end marker inside a path is skipped and the real end closes the quotation");
        // Review round 4: an empty "" inside a spoken quote must not hide the literal quote after it.
        quotes.Clear();
        var emptySent = ConcisePrompt.Protect("He said quote Use \"\" for an empty string end quote. \"no wait, Maria\".", quotes);
        Check(emptySent == "He said [[Q1]]. [[Q2]]." && quotes.Count == 2 && quotes[1] == "\"no wait, Maria\"" &&
            ConcisePrompt.Restore("He said [[Q1]]. \"no wait, Anna\".", quotes) is null,
            "literal quote after a spoken quote containing \"\" stays protected, so its correction cannot be applied");
        Same(TextCleanup.RemoveFillers("He said quote Use \"\" for an empty string end quote. \"no wait, Maria\"."),
            "He said “Use \"\" for an empty string”. \"no wait, Maria\".", "Clean keeps both quotations around an empty \"\"");
        var passage = string.Concat(Enumerable.Repeat("The report repeats this. ", 170)) + "I did that with Claude Code. Sorry, I meant Codex";
        quotes.Clear();
        var longSent = ConcisePrompt.Protect($"He said quote {passage} end quote. Continue.", quotes);
        Check(passage.Length > 4000 && longSent == "He said [[Q1]]. Continue." && quotes.Count == 1 && !longSent.Contains("Claude Code"),
            "a quotation longer than 4000 characters is one protected token, so its correction cannot be applied");
        Same(TextCleanup.RemoveFillers("Quote C:/a b/c.txt end quote is the path."), "\"C:/a b/c.txt\" is the path.", "a spoken quote around an unquoted path is still a quote");
        Same(TextCleanup.RemoveFillers(mixedQuote), "He said \"never trust a number you didn't measure\". Can you remind me on Friday at 9.30 to send the report to Dr. Meyer?",
            "Clean drops spoken markers around literal quotes");
        Same(TextCleanup.RemoveFillers("He said, quote, uh, never trust it, end quote, and left."), "He said, \"never trust it\", and left.", "Clean turns spoken quote markers into quotation marks");
        Same(TextCleanup.RemoveFillers("Sie sagte Zitat, das passt. Zitat Ende."), "Sie sagte „das passt“.", "Clean turns German spoken quote markers into German quotation marks");
        Same(TextCleanup.Clean(mixedQuote), mixedQuote, "Exact keeps spoken quote markers");
        Same(TextCleanup.RemoveFillers("I quote him often."), "I quote him often.", "quote without an end marker is prose");
        Same(TextCleanup.JoinSegments(" I mean, it does matter.\nHe said it.\r\n  Took about\n\n2.5 seconds. "), "I mean, it does matter. He said it. Took about 2.5 seconds.",
            "Whisper segment line breaks become spaces");
        // Review round 2: Whisper output can break a line inside a literal quote.
        foreach (var lineBreak in new[] { "\n", "\r\n" })
        {
            var name = lineBreak.Length == 1 ? "LF" : "CRLF";
            quotes.Clear();
            var multiline = ConcisePrompt.Protect($"The document says, \"I did that with Claude Code yesterday.{lineBreak}Sorry, I meant Codex.\" Keep it.", quotes);
            Check(multiline == "The document says, [[Q1]] Keep it." && ConcisePrompt.Restore("The document says, \"I did that with Codex yesterday.\" Keep it.", quotes) is null,
                $"literal quote across a {name} line break is protected and a changed quote falls back to Clean");
            quotes.Clear();
            Check(ConcisePrompt.Protect($"Er sagte „ich meine{lineBreak}Codex“ und “no wait,{lineBreak}Maria”.", quotes) == "Er sagte [[Q1]] und [[Q2]]." && quotes[0].Contains(lineBreak),
                $"smart quotes across a {name} line break are protected with the break kept");
        }
        quotes.Clear();
        Same(ConcisePrompt.Protect("I quote him often. Sorry, I meant Codex.", quotes), "I quote him often. Sorry, I meant Codex.", "quote without an end marker stays editable");
        var longToken = new string('a', 65536);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Check(TextCleanup.RemoveFillers(longToken) == "A" + longToken[1..] && TextCleanup.RemoveFillers(new string(':', 65536)).Length == 65536 && timer.Elapsed < TimeSpan.FromSeconds(2),
            $"64k-character tokens clean in linear time ({timer.ElapsedMilliseconds} ms)");
    }

    public static async Task VerifyConciseConfiguration()
    {
        // Concise configuration failures must surface as InvalidOperationException (the Clean
        // fallback boundary) with fixed messages that never echo the key. No network is used.
        async Task ExpectConfigFailure(string key, string endpoint, string description)
        {
            var variable = "PRIVATE_FLOW_TEST_KEY_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variable, key);
            try
            {
                var rewrite = new PrivateFlow.Windows.Settings { RewriteKeyVariable = variable, RewriteEndpoint = endpoint };
                await PrivateFlow.Windows.ConciseRewriter.RewriteAsync("hello", rewrite, CancellationToken.None);
                Check(false, description);
            }
            catch (InvalidOperationException ex) { Check(key.Length < 4 || !ex.Message.Contains(key.Trim()), description); }
            finally { Environment.SetEnvironmentVariable(variable, null); }
        }
        await ExpectConfigFailure("secret-key\nInjected: header", "https://example.invalid/v1", "key with a newline fails as configuration, without echoing the key");
        await ExpectConfigFailure("secret-key", "", "an empty endpoint fails before a request so Concise uses Clean");
        await ExpectConfigFailure("secret-key", "ftp://127.0.0.1/v1", "non-http endpoint fails as configuration");
        await ExpectConfigFailure("", "https://example.invalid/v1", "missing key fails as configuration");
        await ExpectConfigFailure("secret-key", "http://example.invalid/v1", "remote cleartext endpoint is rejected before sending credentials or text");
        Check(!RewriteIntegrity.PreservesMeaning("Do not delete the 42 files.", "Delete the 42 files.", false), "rewrite cannot remove negation");
        Check(!RewriteIntegrity.PreservesMeaning("I didn’t delete it.", "I deleted it.", false), "rewrite preserves typographic negative contractions");
        Check(!RewriteIntegrity.PreservesMeaning("Niemand hat die Datei gelöscht.", "Jemand hat die Datei gelöscht.", false), "rewrite preserves German negative pronouns");
        Check(!RewriteIntegrity.PreservesMeaning("The voltage is 42 V.", "The voltage is 24 V.", false), "rewrite cannot change numerical facts");
        Check(!RewriteIntegrity.PreservesMeaning("The delay is 42ms.", "The delay is 24ms.", false), "rewrite protects numbers attached to units");
        Check(!RewriteIntegrity.PreservesMeaning("The voltage is 42 V.", "The voltage is 42 A.", false), "rewrite cannot swap physical units");
        Check(RewriteIntegrity.PreservesMeaning("The voltage is 42 V.", "The voltage is 42V.", false), "unit spacing changes preserve numerical facts");
        Check(RewriteIntegrity.PreservesMeaning("Meet at 3 pm, sorry, I meant 4 pm.", "Meet at 4 pm.", false), "explicit numerical self-correction can remove superseded number");
    }

    public static void VerifyBacktrackGuard()
    {
        // Backtrack: the guard rejected every correction whose cue contains "no"/"not" and pasted the uncorrected text.
        Check(RewriteIntegrity.PreservesMeaning("Send it to Peter, no wait, to Maria.", "Send it to Maria.", false), "\"no wait\" is an interjection, not a stated negation");
        Check(RewriteIntegrity.PreservesMeaning("Invite Anna. Wait, no, invite Maria.", "Invite Maria.", false), "\"wait, no\" is an interjection");
        Check(RewriteIntegrity.PreservesMeaning("Meet at 3 pm, wait, no, 4 pm.", "Meet at 4 pm.", false), "numerical correction with \"wait, no\"");
        Check(RewriteIntegrity.PreservesMeaning("Send it to Peter, no wait, to Maria.", "Send it to Maria, no wait.", false), "a rewrite may also keep the cue");
        Check(!RewriteIntegrity.PreservesMeaning("Send it to Vendor B, wait not Vendor C.", "Send it to Vendor C.", false), "\"wait, not\" keeps its negation protected: it may exclude Vendor C");
        Check(RewriteIntegrity.PreservesMeaning("Send it to Vendor B, wait not Vendor C.", "Send it to Vendor B, not Vendor C.", false), "reading \"wait, not\" as an exclusion passes");
        Check(!RewriteIntegrity.PreservesMeaning("No, do not wait for me.", "Do wait for me.", false), "a real answer \"No\" and negation stay protected");
        Check(!RewriteIntegrity.PreservesMeaning("Send it to Peter, no wait, do not send it.", "Send it.", false), "a negation after the cue stays protected");
        Check(!RewriteIntegrity.PreservesMeaning("There is no wait time.", "There is wait time.", false), "\"no wait\" inside a phrase is content");
        Check(!RewriteIntegrity.PreservesMeaning("Wait, no one came.", "Wait, one came.", false), "\"wait, no one\" is content");
        Check(!RewriteIntegrity.PreservesMeaning("No, sorry, I cannot come.", "Sorry, I cannot come.", false), "an answer \"No, sorry\" stays protected");
        // Review round 1 on PR #13: a kept cue must not pay for a deleted negation; "no wait" as a noun phrase is content.
        Check(!RewriteIntegrity.PreservesMeaning("There are no refunds. No wait, exchanges only.", "There are refunds. No wait, exchanges only.", false),
            "a cue kept by the rewrite cannot stand in for a deleted content negation");
        Check(!RewriteIntegrity.PreservesMeaning("There is no wait.", "There is a wait.", false), "\"no wait\" at a sentence end is content");
        Check(!RewriteIntegrity.PreservesMeaning("There is no wait, so proceed.", "There is a wait, so proceed.", false), "\"no wait\" inside a clause is content");
        // Review round 2: a kept cue in other punctuation still cannot stand in for the deleted negation.
        foreach (var kept in new[] { "There are refunds. No wait exchanges only.", "There are refunds (no wait) exchanges only.", "There are refunds - no - wait, exchanges only." })
            Check(!RewriteIntegrity.PreservesMeaning("There are no refunds. No wait, exchanges only.", kept, false), "a re-punctuated kept cue cannot hide a deleted negation: " + kept);
        Check(RewriteIntegrity.PreservesMeaning("There are no refunds. No wait, exchanges only.", "There are no refunds; exchanges only.", false),
            "a dropped cue keeps the content negation it follows");
        Check(!RewriteIntegrity.PreservesMeaning("Open C:/Projects/a.txt.", "Open C:/Projects/b.txt.", true), "technical mode protects paths");
    }

    public static void VerifySpokenFormattingEmail()
    {
        // Spoken formatting, still opt-in through Spoken formatting: numbered items and email layout.
        Same(SpokenFormatting.Apply("Steps numbered item flash the board numbered item reset it"), "Steps\n1. flash the board\n2. reset it", "spoken numbered items count up");
        Same(SpokenFormatting.Apply("Schritte nummerierter Punkt Board flashen nummerierter Punkt neu starten"), "Schritte\n1. Board flashen\n2. neu starten", "German numbered items are no full stop");
        // Email layout runs last in the pipeline, after Clean: fillers and spoken punctuation are resolved by then.
        string Mail(string spoken, string language = "en") { var formatted = new PreparedDictation(spoken, [], [], language, false, true).Text; return SpokenFormatting.Email(TextCleanup.RemoveFillers(formatted), formatted); }
        Same(Mail("Hi Anna, thanks for the update. I will send it by Friday. Best regards, Alex."),
            "Hi Anna,\n\nThanks for the update. I will send it by Friday.\n\nBest regards,\nAlex", "English email gets greeting, body and closing lines");
        Same(Mail("Hallo Anna, danke für das Update. Ich schicke es bis Freitag. Viele Grüße, Alex.", "de"),
            "Hallo Anna,\n\ndanke für das Update. Ich schicke es bis Freitag.\n\nViele Grüße,\nAlex", "German email body continues in lower case");
        Same(Mail("Sehr geehrter Herr Müller, anbei der Bericht. Mit freundlichen Grüßen, Alex Meyer", "de"),
            "Sehr geehrter Herr Müller,\n\nanbei der Bericht.\n\nMit freundlichen Grüßen,\nAlex Meyer", "formal German greeting and two-word name");
        Same(Mail("Hi Anna, thanks for the update, I will send it by Friday, best regards, Alex."),
            "Hi Anna,\n\nThanks for the update, I will send it by Friday.\n\nBest regards,\nAlex", "Whisper's comma before the closing becomes a full stop");
        foreach (var plain in new[] { "Hey, can you check this?", "I said hi to Anna, then left.", "Send best regards to Anna, please.", "The team sends Viele Grüße, Alex and Anna wave." })
            Same(Mail(plain), plain, "text that is no email keeps its layout: " + plain);
        // Review round 1 on PR #15: fillers, dictated punctuation and long whitespace.
        foreach (var filler in new[] { "uh", "um", "ah", "erm", "you know", "I mean" })
            Same(Mail($"Hi Anna, {filler}, thanks for the update. Best regards, Alex."), "Hi Anna,\n\nThanks for the update.\n\nBest regards,\nAlex", $"email layout survives the filler \"{filler}\"");
        Same(Mail("Hallo Anna, äh, danke für das Update. Viele Grüße, Alex.", "de"), "Hallo Anna,\n\ndanke für das Update.\n\nViele Grüße,\nAlex", "German email layout survives a filler");
        // Review round 2 on PR #15: the dictated greeting is reused, never re-guessed; technical first words keep their case.
        Same(Mail("Hi Anna, uh, I will send the report. Best regards, Alex."), "Hi Anna,\n\nI will send the report.\n\nBest regards,\nAlex", "a capital \"I\" after a removed filler stays in the body");
        Same(Mail("Hi Anna, uh, Thanks for the update. Best regards, Alex."), "Hi Anna,\n\nThanks for the update.\n\nBest regards,\nAlex", "a capitalised body word stays in the body");
        Same(Mail("Hallo Anna, äh, Ich sende den Bericht. Viele Grüße, Alex.", "de"), "Hallo Anna,\n\nIch sende den Bericht.\n\nViele Grüße,\nAlex", "German capitalised body word stays in the body");
        Same(SpokenFormatting.Email("Hi Anna I will send the report.", "Hi Anna, I will send the report."), "Hi Anna,\n\nI will send the report.", "a rewrite without the greeting comma uses the dictated greeting");
        Same(SpokenFormatting.Email("Hi Anna Bob will call.", "Hi, Anna Bob will call."), "Hi Anna Bob will call.", "no greeting with a name in the dictated text, no layout");
        Same(Mail("Hi Anna, mm is the unit. Best regards, Alex."), "Hi Anna,\n\nmm is the unit.\n\nBest regards,\nAlex", "a unit at the body start keeps its case");
        Same(Mail("Hi Anna, config.json is ready. Best regards, Alex."), "Hi Anna,\n\nconfig.json is ready.\n\nBest regards,\nAlex", "a file name at the body start keeps its case");
        Same(Mail("Hi Anna comma thanks for the update full stop best regards comma Alex full stop"),
            "Hi Anna,\n\nThanks for the update.\n\nBest regards,\nAlex", "dictated punctuation produces the email layout");
        Same(Mail("Hallo Anna Komma danke für das Update Punkt Viele Grüße Komma Alex Punkt", "de"),
            "Hallo Anna,\n\ndanke für das Update.\n\nViele Grüße,\nAlex", "German dictated punctuation produces the email layout");
        foreach (var (name, nearMiss) in new[] { ("greeting", "Hi" + new string(' ', 32000) + "!"), ("title", "Hallo Herr" + new string(' ', 32000) + "x"),
            ("closing", "Done." + new string(' ', 32000) + "Best regards" + new string(' ', 32000) + "x") })
        {
            var mailTimer = System.Diagnostics.Stopwatch.StartNew();
            Check(SpokenFormatting.Email(nearMiss) == nearMiss && mailTimer.ElapsedMilliseconds < SlowGuestBoundMs, $"email layout stays linear on a long {name} near miss ({mailTimer.ElapsedMilliseconds} ms)");
        }Same(TextCleanup.RemoveFillers("First point,\n\nuh, second point."), "First point,\n\nsecond point.", "a bracketed filler never removes a dictated paragraph break");
    }

    public static async Task VerifyConciseClient()
    {
        async Task MockRewrite(string finish, string content, bool accepted, string description, bool redirect = false)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var variable = "PRIVATE_FLOW_TEST_KEY_" + Guid.NewGuid().ToString("N"); Environment.SetEnvironmentVariable(variable, "synthetic-key");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token); using var stream = client.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 32768)
                {
                    await stream.ReadExactlyAsync(one, timeout.Token); header.Add(one[0]);
                    if (header.Count > 3 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var text = System.Text.Encoding.ASCII.GetString(header.ToArray());
                var lengthMatch = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length: (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (lengthMatch.Success) await stream.ReadExactlyAsync(new byte[int.Parse(lengthMatch.Groups[1].Value)], timeout.Token);
                else
                {
                    while (true)
                    {
                        var line = new List<byte>();
                        do { await stream.ReadExactlyAsync(one, timeout.Token); line.Add(one[0]); } while (one[0] != 10);
                        var size = Convert.ToInt32(System.Text.Encoding.ASCII.GetString(line.ToArray()).Trim(), 16);
                        await stream.ReadExactlyAsync(new byte[size + 2], timeout.Token);
                        if (size == 0) break;
                    }
                }
                var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { choices = new[] { new { finish_reason = finish, message = new { content } } } });
                var response = System.Text.Encoding.ASCII.GetBytes(redirect ? $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/redirect\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" : $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, timeout.Token); await stream.WriteAsync(body, timeout.Token);
            });
            try
            {
                var settings = new PrivateFlow.Windows.Settings { RewriteEndpoint = $"http://127.0.0.1:{port}/v1", RewriteKeyVariable = variable };
                try { await PrivateFlow.Windows.ConciseRewriter.RewriteAsync("Do not delete the 42 files.", settings, timeout.Token); Check(accepted, description); }
                catch (InvalidOperationException) { Check(!accepted, description); }
                await server;
                if (redirect) Check(!listener.Pending(), "redirect never sends a second request containing transcript text");
            }
            finally { listener.Stop(); Environment.SetEnvironmentVariable(variable, null); }
        }
        await MockRewrite("stop", "", false, "redirect response is rejected at the configured endpoint", redirect: true);
        await MockRewrite("length", "Do not delete the 42 files.", false, "incomplete API response is rejected even when text looks intact");
        await MockRewrite("stop", "Delete the 42 files.", false, "complete API response still rejects removed negation");
        await MockRewrite("stop", "Do not delete the 24 files.", false, "complete API response rejects changed numerical facts");
        await MockRewrite("stop", "Do not delete the 42 files.", true, "complete API response with preserved facts succeeds");
    }

    public static void VerifyBacktrackOnDevice()
    {
        // Backtrack in the on-device styles: an explicit cue between two values of one kind replaces the old value.
        foreach (var (dictated, expected) in new[]
        {
            ("Use Vendor B, no wait, Vendor C.", "Use Vendor C."),
            ("Let's go with Vendor B, wait, no, Vendor C.", "Let's go with Vendor C."),
            ("Use Vendor B, sorry, I meant Vendor C.", "Use Vendor C."),
            ("Meet on Tuesday, sorry, I meant Thursday.", "Meet on Thursday."),
            ("Meet at 3 pm, no wait, 4 pm.", "Meet at 4 pm."),
            ("Let's meet at 3 pm, actually 4 pm.", "Let's meet at 4 pm."),
            ("We need 12 copies, sorry, I meant 14 copies.", "We need 14 copies."),
            ("Set 5 V, correction, 12 V.", "Set 12 V."),
            ("Set -5 V, correction, 6 V.", "Set 6 V."),
            ("Do not contact Vendor B, no wait, Vendor C.", "Do not contact Vendor C."),
            ("Use Vendor A, no wait, Vendor B, sorry, I meant Vendor C.", "Use Vendor C."),
            ("Ship it in March, make that April.", "Ship it in April."),
            ("Room 4, no wait, room 5.", "Room 5."),
            ("Nimm Lieferant B, nein, ich meine Lieferant C.", "Nimm Lieferant C."),
            ("Wir treffen uns am Montag, nein, ich meine Dienstag.", "Wir treffen uns am Dienstag."),
            ("Wir treffen uns am Montag, nein, am Dienstag.", "Wir treffen uns am Dienstag."),
            ("Wir treffen uns um 15 Uhr, nein, ich meine 16 Uhr.", "Wir treffen uns um 16 Uhr."),
            ("Wir treffen uns um 15 Uhr, äh, nein, um 16 Uhr.", "Wir treffen uns um 16 Uhr."),
            ("Das kostet 12 Euro, Korrektur, 14 Euro.", "Das kostet 14 Euro."),
            ("Keep this sentence. The draft is ready. Scratch that.", "Keep this sentence. "),
            ("Dieser Satz bleibt. Der Entwurf ist fertig. Streich das.", "Dieser Satz bleibt. "),
            ("The draft is ready. Scratch that.", ""),
            ("Keep it. Set 3.5 V. Scratch that.", "Keep it. "),
        })
            Same(SpokenCorrections.Apply(dictated), expected, "Backtrack applies an explicit correction: " + dictated);
        // Content, quotations, ambiguity and incomplete repairs stay exactly as spoken.
        foreach (var dictated in new[]
        {
            "Use Vendor B, wait not Vendor C.", "This is not Vendor C.", "Use Vendor B, not Vendor C.", "There are no objections.",
            "It is not not safe.", "I actually enjoyed the movie.", "Actually, the report is correct.", "I mean it.",
            "It is difficult. I mean, we have limited time.", "Wait, do not send the report.", "Use Vendor B, no wait.",
            "Meet at 3 pm, sorry, I meant.", "He said \"Use Vendor B, no wait, Vendor C.\"", "The message reads “Meet at 3 pm, actually 4 pm.”",
            "The phrase no wait signals a correction.", "Send 3 or 4 copies, sorry, I meant 5.", "Send 3 or 4 copies, sorry, I meant 5 copies.",
            "Never mind the cost; check the safety.", "We had 5 people, actually 6 people came.", "Meet at 3, no wait, 4 tomorrow.",
            "Is it on Monday? No, Tuesday.", "Use the 3 V rail, actually 3 V is too low.", "Open C:/v3, no wait, 4/file.txt.",
            "I may, actually, May works.", "Er sagte: „Nimm Lieferant B, nein, ich meine Lieferant C.“", "Im Protokoll steht \"15 Uhr, nein, 16 Uhr\".",
            "Das ist nicht Lieferant C.", "Nimm Lieferant B, nicht Lieferant C.", "Nein, danke. Der Bericht stimmt.", "Einen Moment bitte, ich suche die Datei.",
            "Ich meine es ernst.", "Lieferant B, Moment, nicht Lieferant C.", "Nimm Lieferant B, nein.", "Scratch that.", "Scratch that itch first.",
            "Use e.g. the red one. Scratch that.",
        })
            Same(SpokenCorrections.Apply(dictated), dictated, "Backtrack keeps content, quotations and ambiguous repairs: " + dictated);
        Same(SpokenCorrections.Apply("The doctor said quote no wait, Monday, sorry, I meant Tuesday end quote."), "The doctor said \"no wait, Monday, sorry, I meant Tuesday\".", "a spoken quotation is never corrected");
        Same(TextCleanup.RemoveFillers(new PreparedDictation("Um, send it to Vendor B, uh, no wait, Vendor C.", [], [], "en", false, false).Text), "Send it to Vendor C.",
            "Clean applies Backtrack after the dictionary and with fillers");
        Same(TextCleanup.RemoveFillers(new PreparedDictation("Keep this. The draft is ready. Scratch that.", [], [], "en", false, false).Text), "Keep this.",
            "Clean leaves no trailing space after a withdrawn sentence");
        foreach (var dictated in new[]
        {
            "The skin is irritated. Scratch that itch first.", "Die Tür ist rot. Streich das Holz danach blau.",
            "Open C:/draft Vendor B, no wait, Vendor C final/report.txt.", "Open C:/plans Monday, sorry, I meant Tuesday notes/a.txt.",
            "Open C:/runs 3 V, correction, 5 V final/a.txt.",
            "Open C:/release notes for the final customer build Vendor B, no wait, Vendor C final/report.txt.",
        })
            Same(SpokenCorrections.Apply(dictated), dictated, "Backtrack keeps continued sentences and paths with spaces: " + dictated);
        Same(TextCleanup.RemoveFillers(new PreparedDictation("Open C:/draft Vendor B, no wait, Vendor C final/report.txt. Use Vendor B, no wait, Vendor C.", [], [], "en", false, false).Text),
            "Open C:/draft Vendor B, no wait, Vendor C final/report.txt. Use Vendor C.", "a path with spaces keeps its words while the dictation is corrected");
        Same(TextCleanup.RemoveFillers(new PreparedDictation("Open C:/release notes/report.txt. The draft is ready. Scratch that.", [], [], "en", false, false).Text),
            "Open C:/release notes/report.txt.", "a sentence ending in a path survives the retraction of the next one");
        Same(SpokenCorrections.Apply("Open C:/data/a.txt. Meet at 3 pm, no wait, 4 pm. Then open D:/b.txt."),
            "Open C:/data/a.txt. Meet at 4 pm. Then open D:/b.txt.", "paths in other sentences do not block a correction");
        // Whisper's punctuation of cues, taken from the synthetic transcription pass (2026-10-19).
        foreach (var (dictated, expected) in new[]
        {
            ("Book the room for Tuesday. Sorry, I meant Wednesday.", "Book the room for Wednesday."),
            ("Order 12 resistors. Sorry, I meant 14 resistors.", "Order 14 resistors."),
            ("Nimm Lieferant B. Nein, ich meine Lieferant C.", "Nimm Lieferant C."),
            ("Set the timeout to 500 milliseconds, correction, 750 milliseconds.", "Set the timeout to 750 milliseconds."),
            ("Der Watchdog löst nach 500 Millisekunden, Korrektur, 750 Millisekunden aus.", "Der Watchdog löst nach 750 Millisekunden aus."),
            ("The meeting is on Monday, Scratch that, the meeting is on Friday.", "The meeting is on Friday."),
            ("The meeting is on Monday. Scratch that, the meeting is on Friday.", "The meeting is on Friday."),
            ("Keep this. The draft is ready, scratch that. Send it now.", "Keep this. Send it now."),
            ("Der Entwurf ist fertig, streich das, der Entwurf braucht noch einen Tag.", "Der Entwurf braucht noch einen Tag."),
        })
            Same(TextCleanup.RemoveFillers(new PreparedDictation(dictated, [], [], "auto", false, false).Text), expected, "Backtrack handles Whisper punctuation: " + dictated);
        foreach (var dictated in new[]
        {
            "Wir treffen uns um 15 Uhr? Nein, ich meine 16 Uhr.", "Order 12 resistors. No, 14.", "Book it for Tuesday. Sorry, I meant Wednesday is better.",
            "If you itch, scratch that itch.", "Use 3 V to 5 V, correction, 6 V.", "We could scratch that, it is optional.", "Is it Lieferant B? Nein, Lieferant C.",
        })
            Same(SpokenCorrections.Apply(dictated), dictated, "Backtrack keeps answers and content across sentence ends: " + dictated);
        // Review round 1 on PR #14: continued sentences, earlier clauses, paths, quotations, unit case and alternatives.
        foreach (var (dictated, expected) in new[]
        {
            ("Ship on Monday. Actually Tuesday, unlike Monday, is a holiday.", "Ship on Monday. Actually Tuesday, unlike Monday, is a holiday."),
            ("Do not delete the backup, book Monday, scratch that, book Tuesday.", "Do not delete the backup, book Tuesday."),
            ("Bitte speichere die Datei, lösche den Ordner, streich das, behalte den Ordner.", "Bitte speichere die Datei, behalte den Ordner."),
            ("Keep the backup, the draft is ready, scratch that.", "Keep the backup, the draft is ready, scratch that."),
            ("Open C:/draft, scratch that, final/report.txt.", "Open C:/draft, scratch that, final/report.txt."),
            ("Öffne C:/Entwurf, streich das, final/bericht.txt.", "Öffne C:/Entwurf, streich das, final/bericht.txt."),
            ("He said \"Do not delete it.\" The draft is ready, scratch that, send it later.", "He said \"Do not delete it.\" The draft is ready, scratch that, send it later."),
            ("He said \"Do not delete it.\" The draft is ready. Scratch that.", "He said \"Do not delete it.\" The draft is ready. Scratch that."),
            ("The voltage is wrong. Scratch that, mV is the correct unit.", "mV is the correct unit."),
            ("The voltage is wrong, scratch that, mV is the correct unit.", "mV is the correct unit."),
            ("The old file is wrong, scratch that, config.json is right.", "config.json is right."),
            ("Choose Monday or Tuesday, no wait, Wednesday.", "Choose Monday or Tuesday, no wait, Wednesday."),
            ("Wähle Montag oder Dienstag, Korrektur, Mittwoch.", "Wähle Montag oder Dienstag, Korrektur, Mittwoch."),
            ("Choose Vendor A or Vendor B, no wait, Vendor C.", "Choose Vendor A or Vendor B, no wait, Vendor C."),
            ("Send it to Vendor B, no wait, Vendor C.", "Send it to Vendor C."),
        })
            Same(TextCleanup.RemoveFillers(new PreparedDictation(dictated, [], [], "auto", false, false).Text), expected, "Backtrack stays within its clause, path and quotation: " + dictated);
        // Review round 3: a correction earlier in the transcript must not shift the positions later stages check.
        foreach (var (dictated, expected) in new[]
        {
            ("Set 3 V, correction, 5 V. Open C:/plans Monday, sorry, I meant Tuesday notes/a.txt.", "Set 5 V. Open C:/plans Monday, sorry, I meant Tuesday notes/a.txt."),
            ("Set 3 V, correction, 5 V. Open C:/draft Vendor B, no wait, Vendor C final/report.txt.", "Set 5 V. Open C:/draft Vendor B, no wait, Vendor C final/report.txt."),
            ("Meet on Monday, sorry, I meant Tuesday. Open C:/draft Vendor B, no wait, Vendor C final/report.txt.", "Meet on Tuesday. Open C:/draft Vendor B, no wait, Vendor C final/report.txt."),
            ("Set 3 V,  correction, 5 V. Meet on Monday, actually Tuesday is busy.", "Set 5 V. Meet on Monday, actually Tuesday is busy."),
        })
            Same(TextCleanup.RemoveFillers(new PreparedDictation(dictated, [], [], "en", false, false).Text), expected, "later stages check the text they matched: " + dictated);
        Same(SpokenCorrections.Apply("Let's meet at 3 pm, actually 4 pm. More text follows here."), "Let's meet at 4 pm. More text follows here.",
            "\"actually\" checks the clause end at the match, not the transcript start");
        Same(SpokenCorrections.Apply("Meet at 3 pm, actually 4 pm\nNext line."), "Meet at 4 pm\nNext line.", "a line break ends the clause after \"actually\"");
        Same(SpokenCorrections.Apply("Keep this. Done for today. Scratch that\nNext line."), "Keep this. \nNext line.", "a retraction at a line end is standalone");
        var slowInput = "3" + new string(' ', 11998) + "x";
        var slowWatch = System.Diagnostics.Stopwatch.StartNew();
        Check(SpokenCorrections.Apply(slowInput) == slowInput && slowWatch.ElapsedMilliseconds < SlowGuestBoundMs,
            $"a 12,000-character transcript without a cue stays fast and unchanged ({slowWatch.ElapsedMilliseconds} ms)");
        foreach (var (name, adversarial) in new[]
        {
            ("label", "Vendor" + new string(' ', 5999) + "B" + new string(' ', 5999) + "x"),
            ("weekday", "on Monday" + string.Concat(Enumerable.Repeat(" ,", 5990))),
            ("cue", string.Concat(Enumerable.Repeat("3 , no , ", 1330))),
            ("retraction", string.Concat(Enumerable.Repeat("a.b", 3000)) + ". Scratch that x"),
        })
        {
            slowWatch.Restart(); SpokenCorrections.Apply(adversarial);
            Check(slowWatch.ElapsedMilliseconds < SlowGuestBoundMs, $"adversarial {name} input stays bounded ({slowWatch.ElapsedMilliseconds} ms)");
        }
        var longDictation =string.Concat(Enumerable.Repeat("We meet at 3 pm in room 4 with Vendor B on Monday. ", 230));
        slowWatch.Restart();
        Check(SpokenCorrections.Apply(longDictation) == longDictation && slowWatch.ElapsedMilliseconds < SlowGuestBoundMs,
            $"a 12,000-character dictation of values without cues stays fast ({slowWatch.ElapsedMilliseconds} ms)");
        Same(new PreparedDictation("Use Vendor B, no wait, Vendor C.", [], [], "en", true, false).Text, "Use Vendor B, no wait, Vendor C.", "literal mode keeps spoken corrections");
        Same(TextCleanup.RemoveFillers(new PreparedDictation("He said quote use Vendor B, no wait, Vendor C end quote. Use Vendor B, no wait, Vendor C.", [], [], "en", false, false).Text),
            "He said \"use Vendor B, no wait, Vendor C\". Use Vendor C.", "a spoken quotation keeps its correction while the dictation is corrected");
    }
}
