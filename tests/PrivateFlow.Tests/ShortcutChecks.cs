using PrivateFlow.Core;
using static Harness;

static class ShortcutChecks
{
    public static void VerifyShortcutValidation()
    {
        Check(ActivationShortcut.Default.IsValid, "default shortcut is valid");
        Check(new ActivationShortcut(0, 0x78).IsValid, "standalone function key is supported");
        Check(new ActivationShortcut(4, 0x78).IsValid, "Shift plus function key is supported");
        foreach (var key in new uint[] { 0x41, 0x31, 0x20 })
            Check(!new ActivationShortcut(4, key).IsValid, "Shift-only typing cannot activate dictation");
        foreach (var key in new uint[] { 0x4d, 0x51, 0x45, 0x37, 0xba, 0x41 })
            Check(new ActivationShortcut(3, key).IsValid && new ActivationShortcut(3, key).Problem is null, $"Ctrl + Alt with a typing key (0x{key:x2}) can be a shortcut");
        foreach (var shortcut in new[] { new ActivationShortcut(0, 0x41), new ActivationShortcut(3, 0x1b),
            new ActivationShortcut(3, 0xa2), new ActivationShortcut(16, 0x41), new ActivationShortcut(3, 1) })
            Check(!shortcut.IsValid, "typing, Escape, modifiers, invalid flags and mouse buttons cannot become activation shortcuts");
        Check(new ActivationShortcut(3, 0x2e).Problem!.Contains("Ctrl + Alt + Delete") && new ActivationShortcut(7, 0x2e).Problem is not null && new ActivationShortcut(8, 0x4c).Problem!.Contains("Win + L")
            && new ActivationShortcut(1, 0x73).Problem!.Contains("Alt + F4") && new ActivationShortcut(3, 0x73).Problem is null && new ActivationShortcut(2, 0x09).Problem!.StartsWith("Escape, Tab and Enter are reserved")
            && new ActivationShortcut(0, 0x41).Problem!.StartsWith("Add Ctrl, Alt or Win") && new ActivationShortcut(0, 0x5d).Problem == "That key cannot be used in a shortcut.",
            "combinations Windows reserves or that are unsafe are rejected with their reason: Ctrl + Alt + Delete, Win + L, Alt + F4, Tab, a plain letter, the menu key");
        {
            // German layout (T1), the owner's: AltGr types µ on M, @ on Q, € on E, { on 7 and \ on ß; nothing on A; a space on Space.
            var german = new FixedLayout(new() { [0x4d] = ("m", "µ"), [0x51] = ("q", "@"), [0x45] = ("e", "€"), [0x37] = ("7", "{"), [0xdb] = ("ß", "\\"), [0x41] = ("a", ""), [0x20] = (" ", " ") });
            var english = new FixedLayout(new() { [0x4d] = ("m", ""), [0x51] = ("q", "") });
            ActivationShortcut ControlAlt(uint key) => new(3, key);
            Check(ControlAlt(0x4d).AltGrCharacter(german) == "µ" && ControlAlt(0x51).AltGrCharacter(german) == "@" && ControlAlt(0x45).AltGrCharacter(german) == "€"
                && ControlAlt(0x37).AltGrCharacter(german) == "{" && ControlAlt(0xdb).AltGrCharacter(german) == "\\", "on a German layout Ctrl + Alt + M, Q, E, 7 and ß name the character AltGr types with them");
            Check(ControlAlt(0x41).AltGrCharacter(german) is null && ActivationShortcut.Default.AltGrCharacter(german) is null && ControlAlt(0x4d).AltGrCharacter(english) is null
                && new ActivationShortcut(2, 0x4d).AltGrCharacter(german) is null && new ActivationShortcut(11, 0x4d).AltGrCharacter(german) is null && new ActivationShortcut(6, 0x4d).AltGrCharacter(german) is null,
                "no warning where AltGr types nothing: Ctrl + Alt + A and Ctrl + Alt + Space on German, Ctrl + Alt + M on English, and combinations that are not exactly Ctrl + Alt (Ctrl + M, Ctrl + Alt + Win + M, Ctrl + Shift + M)");
            Check(ActivationShortcut.AltGrWarning("Ctrl + Alt + M", "M", "µ") == "Ctrl + Alt + M also is AltGr + M, which types µ on this keyboard; while Private Flow runs, that character cannot be typed with it.",
                "the warning names the shortcut, the AltGr combination and the character it takes over");
        }
    }

    public static void VerifyHoldToTalk()
    {
        Check(HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, true, TimeSpan.FromSeconds(5)) == HeldShortcutOutcome.KeepRecording, "hold to talk records while the shortcut is held");
        Check(HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, false, TimeSpan.FromSeconds(0.3)) == HeldShortcutOutcome.Transcribe
            && HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, false, TimeSpan.FromSeconds(30)) == HeldShortcutOutcome.Transcribe, "releasing a held shortcut transcribes and inserts");
        Check(HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, false, TimeSpan.FromSeconds(0.29)) == HeldShortcutOutcome.Discard
            && HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, false, TimeSpan.Zero) == HeldShortcutOutcome.Discard, "a tap shorter than 0.3 s is discarded without transcription");
        Check(HeldShortcut.Decide(ShortcutBehavior.Toggle, false, TimeSpan.FromSeconds(0.1)) == HeldShortcutOutcome.KeepRecording
            && HeldShortcut.Decide(ShortcutBehavior.Toggle, false, TimeSpan.FromSeconds(5)) == HeldShortcutOutcome.KeepRecording, "toggle keeps recording after release until the next press");
        Check(ActivationShortcut.Default.Problem is null && new ActivationShortcut(3, 0x4d).Problem is null && new ActivationShortcut(6, 0x4d).Problem is null
            && new ActivationShortcut(4, 0x4d).Problem!.StartsWith("Add Ctrl, Alt or Win") && new ActivationShortcut(2, 0x1b).Problem!.StartsWith("Escape, Tab and Enter are reserved"),
            "a rejected shortcut says why, while Ctrl + Alt + Space, Ctrl + Alt + M and Ctrl + Shift + M are taken");
        {
            // Hold to talk as DictationController runs it: a hotkey message for the recording's own shortcut while its keys are down at that
            // moment repeats the hold and is ignored (HeldShortcut.RepeatsHold); any other message during the recording is a press and applies
            // the hold decision; the timer ends the recording when it reads the keys up. Time is the captured audio in seconds.
            var dictationShortcut = ActivationShortcut.Default; var profileShortcut = new ActivationShortcut(3, 0x70);
            var starts = 0; var transcribed = 0; var discarded = 0; var recording = false; ActivationShortcut? recordingShortcut = null;
            void End(HeldShortcutOutcome outcome)
            {
                if (outcome == HeldShortcutOutcome.KeepRecording) return;
                if (outcome == HeldShortcutOutcome.Discard) discarded++; else transcribed++;
                recording = false; recordingShortcut = null;
            }
            void Hotkey(ActivationShortcut pressed, bool keysDown, double seconds)
            {
                if (!recording) { recording = true; recordingShortcut = pressed; starts++; return; }
                if (HeldShortcut.RepeatsHold(true, recordingShortcut, pressed, keysDown)) return;
                End(HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, false, TimeSpan.FromSeconds(seconds)) == HeldShortcutOutcome.Discard ? HeldShortcutOutcome.Discard : HeldShortcutOutcome.Transcribe);
            }
            void Tick(bool keysDown, double seconds) { if (recording) End(HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, keysDown, TimeSpan.FromSeconds(seconds))); }
            // The owner's flicker: messages repeat while the keys stay down. Before, the first repeat (under 0.3 s of audio) discarded the
            // recording and the next one started another: a pill flickering on and off and no text.
            Hotkey(dictationShortcut, true, 0); Tick(true, 0.05); Hotkey(dictationShortcut, true, 0.1); Hotkey(dictationShortcut, true, 0.2); Tick(true, 0.25);
            Hotkey(dictationShortcut, true, 0.5); Tick(true, 1); Hotkey(dictationShortcut, true, 1.5); Tick(true, 2.5);
            var heldThrough = recording && starts == 1 && discarded == 0 && transcribed == 0;
            Tick(false, 3);
            Check(heldThrough && !recording && starts == 1 && discarded == 0 && transcribed == 1,
                "hold to talk: hotkey messages repeated while the keys are down are ignored, and the release after speech transcribes the one recording (the flicker sequence)");
            Hotkey(dictationShortcut, true, 0); Tick(false, 0.1);
            Check(starts == 2 && discarded == 1 && transcribed == 1 && !recording, "hold to talk: a hold that captured under 0.3 s of audio is still discarded");
            Hotkey(dictationShortcut, true, 0); Tick(true, 0.5); Hotkey(dictationShortcut, false, 1);
            Hotkey(dictationShortcut, true, 0); Tick(true, 0.4); Tick(false, 2);
            Check(starts == 4 && transcribed == 3 && discarded == 1 && !recording,
                "hold to talk: a press whose keys are already up at the message is a release and press again and ends the recording; the next press starts a new one");
            Hotkey(dictationShortcut, true, 0); Tick(true, 0.5); Hotkey(profileShortcut, true, 1);
            Check(starts == 5 && transcribed == 4 && !recording, "hold to talk: another registered shortcut pressed during the recording is a press, never a repeat");
            Check(!HeldShortcut.RepeatsHold(false, dictationShortcut, dictationShortcut, true) && !HeldShortcut.RepeatsHold(false, profileShortcut, profileShortcut, true)
                && !HeldShortcut.RepeatsHold(true, new ActivationShortcut(0, 0x1b), new ActivationShortcut(0, 0x1b), true) && !HeldShortcut.RepeatsHold(true, null, dictationShortcut, true),
                "nothing is a repeat outside a hold-to-talk recording started by that shortcut: toggle presses, Escape and the meeting shortcut always act");
        }
    }

    public static void VerifyMeetingShortcut()
    {
        // The meeting shortcut starts from idle, stops a capture, and refuses with a reason otherwise.
        Check(MeetingShortcut.Decide(false, false, false, false, false).Action == MeetingShortcutAction.Start, "the meeting shortcut starts a meeting from idle");
        Check(MeetingShortcut.Decide(false, true, false, false, false).Action == MeetingShortcutAction.Stop, "the meeting shortcut stops a recording meeting");
        foreach (var (decision, description) in new[] {
            (MeetingShortcut.Decide(true, false, false, false, false), "in diagnostic mode"), (MeetingShortcut.Decide(true, true, false, false, false), "in diagnostic mode (even with a capture)"),
            (MeetingShortcut.Decide(false, false, false, true, false), "while dictation records or transcribes"), (MeetingShortcut.Decide(false, false, true, false, false), "while a transcript is being finished"),
            (MeetingShortcut.Decide(false, false, false, false, true), "while the speech engine is set up or checked") })
            Check(decision.Action == MeetingShortcutAction.Refuse && decision.Reason.Length > 0, "the meeting shortcut does nothing " + description + " and says why");
        Check(PasteTiming.RestoreDelay("OUTLOOK") == TimeSpan.FromSeconds(3) && PasteTiming.RestoreDelay("mstsc") == TimeSpan.FromSeconds(5) &&
            PasteTiming.RestoreDelay("notepad") == TimeSpan.FromMilliseconds(400) && PasteTiming.RestoreDelay(null) == TimeSpan.FromMilliseconds(400),
            "Office and remote desktops hold the dictated clipboard longer");
    }
}

// A keyboard layout for the checks: what each key types without and with AltGr; Shift is not needed by them.
sealed class FixedLayout(Dictionary<uint, (string Plain, string AltGr)> keys) : IKeyboardLayout
{
    public string Typed(uint virtualKey, bool shift, bool altGr) =>
        keys.TryGetValue(virtualKey, out var typed) ? (altGr ? typed.AltGr : typed.Plain) : "";
}
