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
        Check(!policy.CanInsert(session, SelectedTarget with { Password = PasswordField.Yes }, false, "hunter2"), "password field at paste time is refused");
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
        session = policy.Begin(SelectedTarget with { OwnWindow = true });
        Check(!policy.CanInsert(session, SelectedTarget, false, "secret"), "password field or Private Flow selected at start uses the clipboard fallback");
        session = policy.Begin(SelectedTarget with { Window = 0 });
        Check(!policy.CanInsert(session, SelectedTarget with { Window = 0 }, false, "hello"), "no foreground window uses the clipboard fallback");

        // A field whose kind is unknown is never pasted into: at the start or at paste time.
        session = policy.Begin(SelectedTarget with { Password = PasswordField.Unknown });
        Check(!policy.CanInsert(session, SelectedTarget, false, "hello"), "a field that could not be checked at the start uses the clipboard fallback");
        session = policy.Begin(SelectedTarget);
        Check(!policy.CanInsert(session, SelectedTarget with { Password = PasswordField.Unknown }, false, "hello"), "a field that cannot be checked at paste time uses the clipboard fallback");
        // UI Automation and the Win32 edit control: a password field by either blocks; otherwise one that says no decides; neither, unknown.
        var no = PasswordField.No; var yes = PasswordField.Yes; var unknown = PasswordField.Unknown;
        var table = new (PasswordField Automation, PasswordField Edit, PasswordField Expected)[]
        {
            (no, no, no), (no, yes, yes), (no, unknown, no), (yes, no, yes), (yes, yes, yes), (yes, unknown, yes), (unknown, no, no), (unknown, yes, yes), (unknown, unknown, unknown),
        };
        foreach (var (automation, edit, expected) in table)
            Check(PasswordFields.Combine(automation, edit) == expected, $"UI Automation {automation} and the edit control {edit} make the field {expected}");
        Check(PasswordFields.FromEditControl("Edit", 0x20, null) == yes && PasswordFields.FromEditControl("RICHEDIT50W", 0, '*') == yes && PasswordFields.FromEditControl("Edit", 0, 0) == no
            && PasswordFields.FromEditControl("RichEditD2DPT", 0, 0) == no && PasswordFields.FromEditControl("WindowsForms10.EDIT.app.0.141b42a_r6_ad1", 0x20, 0) == yes
            && PasswordFields.FromEditControl("Edit", 0, null) == unknown && PasswordFields.FromEditControl("Chrome_RenderWidgetHostHWND", 0x20, '*') == unknown
            && PasswordFields.FromEditControl("ConsoleWindowClass", 0, 0) == unknown,
            "an edit or rich edit control says by ES_PASSWORD or its password character; one that does not answer in time, and any other window class, cannot say");
        Check(new FocusTarget(1, 2, 3, false, no).Safe && !new FocusTarget(1, 2, 3, false, unknown).Safe && !new FocusTarget(1, 2, 3, false, yes).Safe
            && !new FocusTarget(1, 2, 3, true, no).Safe && !new FocusTarget(0, 2, 3, false, no).Safe, "only a known ordinary field of another app's window in front is safe");
        // The reason the not-pasted notice gives.
        Check(DictationPolicy.NotPastedReason(SelectedTarget with { Password = unknown }, SelectedTarget, false) == "could not check whether this is a password field"
            && DictationPolicy.NotPastedReason(SelectedTarget, SelectedTarget with { Password = unknown }, false) == "could not check whether this is a password field"
            && DictationPolicy.NotPastedReason(SelectedTarget with { Password = yes }, SelectedTarget, false) == "password field or Private Flow itself"
            && DictationPolicy.NotPastedReason(SelectedTarget, SelectedTarget with { OwnWindow = true }, false) == "password field or Private Flow itself"
            && DictationPolicy.NotPastedReason(SelectedTarget, OtherTarget with { Password = unknown }, false) == "the selected window is no longer in front"
            && DictationPolicy.NotPastedReason(SelectedTarget, SelectedTarget, false) == "the focus moved or Windows blocked the paste"
            && DictationPolicy.NotPastedReason(SelectedTarget, SelectedTarget with { Control = SelectedTarget.Control + 1 }, false) == "the focus moved or Windows blocked the paste"
            && DictationPolicy.NotPastedReason(SelectedTarget, SelectedTarget, false, keysHeld: true) == "Ctrl, Alt, Shift or Windows was still held"
            && DictationPolicy.NotPastedReason(SelectedTarget, OtherTarget, false, keysHeld: true) == "Ctrl, Alt, Shift or Windows was still held"
            && DictationPolicy.NotPastedReason(SelectedTarget with { Password = yes }, SelectedTarget, false, keysHeld: true) == "password field or Private Flow itself"
            && DictationPolicy.NotPastedReason(SelectedTarget, SelectedTarget, true) == "the clipboard could not be saved"
            && DictationPolicy.NotPastedReason(SelectedTarget with { Window = 0, Password = unknown }, SelectedTarget, false) == "no window was in front at the start",
            "the not-pasted notice names why: an unchecked field, a password field or Private Flow, a held modifier key, another window in front, the clipboard, or a moved focus or Windows within the same window");
        // The clipboard before a paste: an unreadable one is never replaced, by the paste or by the copy left when it fails.
        Check(ClipboardSnapshot.Classify(true, []) == ClipboardCopy.Empty && ClipboardSnapshot.Classify(true, [true, true, true]) == ClipboardCopy.Complete
            && ClipboardSnapshot.Classify(false, []) == ClipboardCopy.Incomplete && ClipboardSnapshot.Classify(true, [true, false, true]) == ClipboardCopy.Incomplete
            && ClipboardSnapshot.AllowsPaste(ClipboardCopy.Empty) && ClipboardSnapshot.AllowsPaste(ClipboardCopy.Complete) && !ClipboardSnapshot.AllowsPaste(ClipboardCopy.Incomplete),
            "a clipboard that stayed locked, or held a format that could not be read or read as no data, is not saved completely, so nothing replaces it; an empty or completely saved one may be");
        // One observation: the field classified must still have the focus right before Ctrl+V, and a probe that timed out knows nothing.
        var other = SelectedTarget with { Window = 77, Control = 78, Process = 79 };
        Check(!DictationPolicy.SameFocus(SelectedTarget, other) && !DictationPolicy.SameFocus(SelectedTarget, SelectedTarget with { Control = 99 })
            && DictationPolicy.SameFocus(SelectedTarget, SelectedTarget with { Password = unknown, AccessibleId = "x" }),
            "classified A with the focus now on B, even another control of the same window, is not pasted; the same window, control and process are");
        var element = SelectedTarget with { AccessibleId = "42.7" };
        Check(DictationPolicy.SameElement(element, element with { AccessibleId = null }, "42.7") && !DictationPolicy.SameElement(element, element, "42.8")
            && !DictationPolicy.SameElement(element, element, null) && !DictationPolicy.SameElement(element, other, "42.7") && DictationPolicy.SameElement(SelectedTarget with { AccessibleId = null }, SelectedTarget, null),
            "right before Ctrl+V the accessibility element must be the one classified too: a move into the password field beside it in the same browser window is not pasted");
        Check(PasswordFields.Settle(SelectedTarget, other).Password == unknown && !PasswordFields.Settle(SelectedTarget, other).Safe && PasswordFields.Settle(SelectedTarget, SelectedTarget) == SelectedTarget,
            "a field whose focus moved while its password state was asked becomes unknown and is not safe; an unmoved one keeps its answer");
        Check(PasswordFields.Combine(no, unknown, editControlTimedOut: true) == unknown && PasswordFields.Combine(no, no, editControlTimedOut: true) == unknown
            && !new FocusTarget(1, 2, 3, false, PasswordFields.Combine(no, unknown, editControlTimedOut: true)).Safe,
            "an edit control that does not answer in time leaves the field unknown, which is not pasted into, whatever UI Automation said");
    }

    public static void VerifyPreparedDictation()
    {
        var aliases = new[] { new PhraseRule { Original = "code x", Replacement = "Codex" } };
        var snippets = new[] { new PhraseRule { Original = "my signature", Replacement = "Alex\nTest Engineer" }, new PhraseRule { Original = "signature", Replacement = "short" } };
        var prepared = new PreparedDictation("Use code x. my signature", aliases, snippets, "en", false, false);
        Same(prepared.Restore(prepared.Text), "Use Codex. Alex\nTest Engineer", "aliases apply with longest nonrecursive snippet expansion");
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
        var literalClean = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Clean, styled => styled.LiteralMode = true), CancellationToken.None);
        var literalConcise = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.Concise, styled => styled.LiteralMode = true), CancellationToken.None);
        var literalPolish = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(corrected, Styled(OutputStyle.LocalPolish, styled => styled.LiteralMode = true), CancellationToken.None);
        Check(literalClean.Final.Contains("no wait") && literalConcise.Final == literalClean.Final && literalConcise.Notice is null
            && literalPolish.Final == literalClean.Final && literalPolish.Notice is null, "Literal mode keeps the spoken correction and skips the Concise and Local polish rewrite");
        var longText = string.Concat(Enumerable.Repeat("We ship on Friday. ", 700));
        Environment.SetEnvironmentVariable(pipelineVariable, "synthetic-key");
        try
        {
            var tooLong = await PrivateFlow.Windows.DictationPipeline.ProcessAsync(longText, Styled(OutputStyle.Concise, styled => styled.RewriteEndpoint = "https://example.invalid/v1"), CancellationToken.None);
            Check(tooLong.Notice == "Used Clean: Long transcript uses Clean; select smaller sections for rewriting." && tooLong.Final == longText.Trim(), "a transcript over 12,000 characters is never sent for rewriting");
        }
        finally { Environment.SetEnvironmentVariable(pipelineVariable, null); }
        var signature = new[] { new PhraseRule { Original = "my signature", Replacement = "Alex\nTest Engineer" } };
        var expanded = await PrivateFlow.Windows.DictationPipeline.ProcessAsync("Please add my signature.", Styled(OutputStyle.Clean, styled => styled.Snippets = [.. signature]), CancellationToken.None);
        Same(expanded.Final, "Please add Alex\nTest Engineer.", "Clean expands a snippet with its stored line breaks");
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
