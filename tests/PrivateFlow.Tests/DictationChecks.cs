using PrivateFlow.Core;
using static Fixtures;
using static Harness;

static class DictationChecks
{
    public static void VerifyInsertionPolicy()
    {
        var policy = new DictationPolicy();
        var session = policy.Begin(SelectedTarget);
        Check(policy.CanInsert(session, SelectedTarget, false, "hello"), "unchanged selected field may receive text");
        Check(!policy.CanInsert(session, OtherTarget, false, "hello"), "different window cannot receive text");
        Check(policy.CanInsert(session, SelectedTarget with { Control = 99 }, false, "hello"), "control identity changes within the selected window still paste");
        Check(policy.CanInsert(session, SelectedTarget with { AccessibleId = "new-field" }, false, "hello"), "browser accessibility identity changes within the selected window still paste");
        Check(!policy.CanInsert(session, SelectedTarget with { Process = 99 }, false, "hello"), "a reused window handle in another process cannot receive text");
        Check(!policy.CanInsert(session, SelectedTarget with { Safe = false }, false, "hunter2"), "password field at paste time is refused");
        Check(!policy.CanInsert(session, SelectedTarget, true, "hello"), "held modifiers prevent insertion");
        Check(!policy.CanInsert(session, SelectedTarget, false, "  "), "empty transcription cannot be inserted");
        Check(policy.CanInsert(session, SelectedTarget, false, "hello\nworld"), "multiline text is pasted, never typed as Enter");
        Check(policy.Target == SelectedTarget, "policy remembers the field selected at start");
        session = policy.Begin(SelectedTarget);
        policy.Cancel();
        Check(!policy.IsCurrent(session), "cancel invalidates a late transcription result");
        Check(!policy.CanInsert(session, SelectedTarget, false, "hello"), "cancel prevents pending insertion");
        var fresh = policy.Begin(SelectedTarget);
        Check(!policy.IsCurrent(session) && policy.IsCurrent(fresh), "new recording rejects previous result");
        policy.Complete(session);
        Check(policy.IsCurrent(fresh), "old completion cannot stop the new session");
        policy.Complete(fresh);
        Check(!policy.IsCurrent(fresh), "completed session cannot insert twice");
        session = policy.Begin(SelectedTarget with { Safe = false });
        Check(!policy.CanInsert(session, SelectedTarget, false, "secret"), "password field or Private Flow selected at start uses the clipboard fallback");
        session = policy.Begin(SelectedTarget with { Window = 0 });
        Check(!policy.CanInsert(session, SelectedTarget with { Window = 0 }, false, "hello"), "no foreground window uses the clipboard fallback");
    }

    public static void VerifyPreparedDictation()
    {
        var aliases = new[] { new PhraseRule { Original = "code x", Replacement = "Codex" } };
        var snippets = new[] { new PhraseRule { Original = "my signature", Replacement = "Alex\nCreative Engineer" }, new PhraseRule { Original = "signature", Replacement = "short" } };
        var prepared = new PreparedDictation("Use code x. my signature", aliases, snippets, "en", false, false);
        Same(prepared.Restore(prepared.Text), "Use Codex. Alex\nCreative Engineer", "aliases apply with longest nonrecursive snippet expansion");
        Check(!prepared.PreservesSnippets("changed text"), "missing snippet token is rejected");
        Reject(() => prepared.Restore(prepared.Text + prepared.Text), "duplicated snippet tokens are rejected");
        prepared = new PreparedDictation("quote code x end quote and code x", aliases, [], "en", false, false);
        Same(prepared.Restore(prepared.Text), "\"code x\" and Codex", "spoken quotations protect aliases");
        prepared = new PreparedDictation("code x my signature new paragraph", aliases, snippets, "en", true, true);
        Same(prepared.Text, "code x my signature new paragraph", "literal mode disables libraries and commands");
        prepared = new PreparedDictation("Open C:/new paragraph/comma.txt. new paragraph Next comma okay", [], [], "en", false, true);
        Same(prepared.Text, "Open C:/new paragraph/comma.txt.\n\nNext, okay", "formatting preserves unquoted paths while composing text");
        Same(SpokenFormatting.Apply("Start neuer Absatz Weiter Komma fertig"), "Start\n\nWeiter, fertig", "German paragraph and punctuation formatting");
        Same(SpokenFormatting.Apply("spell A B C 1 end spelling"), "ABC1", "explicit spelling joins character identifiers");
    }

    public static void VerifyStartingCapture()
    {
        // From its pill on, a start ends as a running recording does (review of #67): every event in the starting state, for both features.
        string Outcomes(Func<StartingEvent, StartingOutcome> decide) => string.Join(", ", Enum.GetValues<StartingEvent>().Select(startingEvent => $"{startingEvent} {decide(startingEvent)}"));
        Check(Outcomes(StartingCapture.Dictation) == "Press Cancel, HeldRepeat Continue, Release Discard, Escape Cancel, Cancel Cancel, Stop Cancel, Quit Cancel, WindowClosed Continue, OpenFailed Fail",
            $"a starting dictation ends without opening the microphone on a second press, Escape, Cancel, Stop or Quit; a hold-to-talk release is a tap; a held shortcut's repeat and the window closed to the tray leave it; a failed open fails it ({Outcomes(StartingCapture.Dictation)})");
        Check(Outcomes(StartingCapture.Meeting) == "Press Cancel, HeldRepeat Continue, Release Continue, Escape Cancel, Cancel Cancel, Stop Cancel, Quit Cancel, WindowClosed RefuseClose, OpenFailed Fail",
            $"a starting meeting ends without opening its devices on the meeting shortcut, Stop and finish or Quit; closing the window is refused as while it records; a failed open fails it ({Outcomes(StartingCapture.Meeting)})");
        var gate = new StartingGate(); var first = gate.Begin(); var keptWhileStarting = gate.Keeps(first) && gate.IsStarting;
        gate.End(); var keptAfterEnd = gate.Keeps(first); var second = gate.Begin();
        Check(keptWhileStarting && !keptAfterEnd && !gate.Keeps(first) && gate.Keeps(second),
            "a device open that returns after its start ended, also when another start has begun, is closed at once");
    }

    public static async Task VerifyDictationPipeline()
    {
        // The dictation pipeline per output style, with nothing installed and no network.
        var pipelineVariable = "PRIVATE_FLOW_TEST_KEY_" + Guid.NewGuid().ToString("N");
        var pipelineRoot = Path.Combine(Path.GetTempPath(), "PrivateFlow-pipeline-" + Guid.NewGuid().ToString("N"));
        PrivateFlow.Windows.Settings Styled(OutputStyle style, Action<PrivateFlow.Windows.Settings>? change = null)
        {
            var styled = new PrivateFlow.Windows.Settings { OutputStyle = style, RewriteEndpoint = "https://example.invalid/v1", RewriteKeyVariable = pipelineVariable,
                LocalRewriteEnginePath = Path.Combine(pipelineRoot, "llama-server.exe"), LocalRewriteModelPath = Path.Combine(pipelineRoot, "model.gguf") };
            change?.Invoke(styled); return styled;
        }
        const string corrected = "Um, meet at 3 pm, no wait, 4 pm.";
        var exact = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Exact), CancellationToken.None);
        Check(exact.Final == corrected && exact.Notice is null && exact.Original == corrected, "Exact pastes the spoken words, fillers and corrections included");
        var clean = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Clean), CancellationToken.None);
        Same(clean.Final, "Meet at 4 pm.", "Clean removes fillers and applies the spoken correction");
        var noKey = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Concise), CancellationToken.None);
        Check(noKey.Final == clean.Final && noKey.Notice == "Used Clean: API key variable is not set.", "Concise without a key pastes Clean and says why");
        var noEndpoint = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Concise, settings => settings.RewriteEndpoint = ""), CancellationToken.None);
        Check(noEndpoint.Final == clean.Final && noEndpoint.Notice!.StartsWith("Used Clean: No API base URL configured."), "Concise without an endpoint uses Clean and explains why without making a request");
        var notInstalled = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.LocalPolish), CancellationToken.None);
        Check(notInstalled.Final == clean.Final && notInstalled.Notice == "Used Clean: " + PrivateFlow.Windows.LocalRewriter.PolishMissing, "Local polish that is not installed pastes Clean and says why");
        Check(PrivateFlow.Windows.DictationPipeline.PolishNotSetUp(notInstalled.Notice) && !PrivateFlow.Windows.DictationPipeline.PolishNotSetUp(noKey.Notice)
            && !PrivateFlow.Windows.DictationPipeline.PolishNotSetUp("Used Clean: rewriting unavailable.") && !PrivateFlow.Windows.DictationPipeline.PolishNotSetUp(null),
            "the Used Clean card offers the Local polish setup only when its model is missing");
        var longText = string.Concat(Enumerable.Repeat("We ship on Friday. ", 700));
        Environment.SetEnvironmentVariable(pipelineVariable, "synthetic-key");
        try
        {
            var tooLong = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(longText, Styled(OutputStyle.Concise, styled => styled.RewriteEndpoint = "https://example.invalid/v1"), CancellationToken.None);
            Check(tooLong.Notice == "Used Clean: Long transcript uses Clean; select smaller sections for rewriting." && tooLong.Final == longText.Trim(), "a transcript over 12,000 characters is never sent for rewriting");
        }
        finally { Environment.SetEnvironmentVariable(pipelineVariable, null); }
        var signature = new[] { new PhraseRule { Original = "my signature", Replacement = "Alex\nCreative Engineer" } };
        var expanded = await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Please add my signature.", Styled(OutputStyle.Clean, styled => styled.Snippets = [.. signature]), CancellationToken.None);
        Same(expanded.Final, "Please add Alex\nCreative Engineer.", "Clean expands a snippet with its stored line breaks");
        var exactSnippet = await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Please add my signature.", Styled(OutputStyle.Exact, styled => styled.Snippets = [.. signature]), CancellationToken.None);
        Same(exactSnippet.Final, "Please add my signature.", "Exact leaves snippet phrases as spoken");
        var mail = await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Hi Anna, uh, thanks for the update. Best regards, Alex.", Styled(OutputStyle.Clean, styled => styled.SpokenFormatting = true), CancellationToken.None);
        Same(mail.Final, "Hi Anna,\n\nThanks for the update.\n\nBest regards,\nAlex", "spoken formatting lays out an email after Clean");
        var literal = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Clean, styled => { styled.LiteralMode = true; styled.SpokenFormatting = true; }), CancellationToken.None);
        Same(literal.Final, "Meet at 3 pm, no wait, 4 pm.", "literal mode keeps the spoken correction and skips formatting while Clean still drops fillers");
        var spanishSnippet = new[] { new PhraseRule { Original = "mi firma", Replacement = "Saludos, Alex", Language = "es" } };
        Same((await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Añade mi firma.", Styled(OutputStyle.Clean, styled => { styled.Language = "es"; styled.Snippets = [.. spanishSnippet]; }), CancellationToken.None)).Final,
            "Añade Saludos, Alex.", "a Spanish snippet applies to Spanish dictation");
        Same((await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Add mi firma.", Styled(OutputStyle.Clean, styled => { styled.Language = "en"; styled.Snippets = [.. spanishSnippet]; }), CancellationToken.None)).Final,
            "Add mi firma.", "a Spanish snippet stays out of English dictation");
        try { await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Clean), new CancellationToken(true)); Check(false, "a cancelled dictation pastes nothing"); }
        catch (OperationCanceledException) { Check(true, "a cancelled dictation pastes nothing"); }
    }
}
