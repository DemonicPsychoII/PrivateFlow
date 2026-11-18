using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// A setting settings.json stores: the settings section or page whose Save preferences stores it, null when no control there reads it,
// and whether a setup installs it.
internal sealed record SettingDescription(string Name, string? Section, bool Installed = false);

// The preferences Save preferences stores, by the settings section or page that shows them. A section is unsaved while the values
// it shows differ from the stored ones.
internal static class PreferenceSections
{
    public const string Audio = "Microphone and speakers", Shortcuts = "Shortcuts", LanguageAndPasting = "Language and pasting", TextStyle = "Text style",
        Profiles = "Profiles per app", MeetingDefaults = "Meeting defaults", Models = "Models", SpeechRecognition = "Speech recognition", Vocabulary = "Vocabulary";
    // Every stored setting, once, by section in the order of the settings popup, then those without a control. The checks fail for a
    // stored setting missing here, for a section's setting without exactly one control in the window, and for a setup script that
    // writes other settings than those installed here.
    public static readonly SettingDescription[] Table =
    [
        new(nameof(Settings.MicrophoneId), Audio), new(nameof(Settings.PlaybackDeviceId), Audio), new(nameof(Settings.SystemAudioColor), Audio),
        new(nameof(Settings.ShortcutModifiers), Shortcuts), new(nameof(Settings.ShortcutKey), Shortcuts), new(nameof(Settings.ShortcutBehavior), Shortcuts),
        new(nameof(Settings.MeetingShortcutModifiers), Shortcuts), new(nameof(Settings.MeetingShortcutKey), Shortcuts),
        new(nameof(Settings.Language), LanguageAndPasting), new(nameof(Settings.AutoInsert), LanguageAndPasting), new(nameof(Settings.SpokenFormatting), LanguageAndPasting),
        new(nameof(Settings.TechnicalMode), LanguageAndPasting), new(nameof(Settings.LiteralMode), LanguageAndPasting),
        new(nameof(Settings.OutputStyle), TextStyle), new(nameof(Settings.PolishWaitSeconds), TextStyle), new(nameof(Settings.RewriteEndpoint), TextStyle),
        new(nameof(Settings.RewriteModel), TextStyle), new(nameof(Settings.RewriteKeyVariable), TextStyle),
        new(nameof(Settings.Profiles), Profiles),
        new(nameof(Settings.MeetingLanguages), MeetingDefaults), new(nameof(Settings.RetainMeetingAudio), MeetingDefaults), new(nameof(Settings.MeetingRetentionDays), MeetingDefaults),
        new(nameof(Settings.LocalPolishModel), Models), new(nameof(Settings.MeetingNotesGpuLayers), Models), new(nameof(Settings.MeetingNotesStyle), Models),
        new(nameof(Settings.SpeechDetection), SpeechRecognition), new(nameof(Settings.KeepModelWarm), SpeechRecognition),
        new(nameof(Settings.EnginePath), SpeechRecognition, true), new(nameof(Settings.ModelPath), SpeechRecognition, true),
        new(nameof(Settings.LocalRewriteEnginePath), SpeechRecognition, true), new(nameof(Settings.LocalRewriteModelPath), SpeechRecognition, true),
        new(nameof(Settings.VadModelPath), SpeechRecognition, true),
        new(nameof(Settings.Vocabulary), Vocabulary),
        // Shown under Speech recognition and set up by Apply, which installs them; Save preferences never reads them.
        new(nameof(Settings.SpeechBackend), null, true), new(nameof(Settings.SpeechModel), null, true),
        // Written by a setup with the engine; paths typed in by hand empty the final-pass model and mark the paths (UsePathsEnteredByHand).
        new(nameof(Settings.MeetingModelPath), null, true), new(nameof(Settings.SpeechPathsCustom), null, true), new(nameof(Settings.SpeechSetupVersion), null, true),
        // The meeting notes model's files, written by its install under Models.
        new(nameof(Settings.MeetingNotesEnginePath), null, true), new(nameof(Settings.MeetingNotesModelPath), null, true),
        // Entered in settings.json by hand only.
        new(nameof(Settings.MeetingNotesContextTokens), null),
        // Stored at once by Save corrections and snippets on the Vocabulary page, outside Save preferences.
        new(nameof(Settings.Corrections), null), new(nameof(Settings.Snippets), null),
        // Set only on a dictation's copy by a profile, which is a session overlay and never stored as a preference.
        new(nameof(Settings.SelectedProfileId), null),
        // Read only from older files: Load moves it into Vocabulary.
        new(nameof(Settings.MeetingVocabulary), null),
        // Set by Load.
        new(nameof(Settings.SettingsVersion), null),
    ];
    private static readonly string[] Order = Table.Select(setting => setting.Section).OfType<string>().Distinct().ToArray();
    private static readonly HashSet<string> WithSection = Table.Where(setting => setting.Section is not null).Select(setting => setting.Name).ToHashSet();
    // The settings a setup may install, the only ones its result file may name.
    public static readonly string[] Installed = Table.Where(setting => setting.Installed).Select(setting => setting.Name).ToArray();
    public static List<string> Changed(Settings shown, Settings stored)
    {
        var differing = Settings.Differences(shown, stored, WithSection);
        return Order.Where(section => Table.Any(setting => setting.Section == section && differing.Contains(setting.Name))).ToList();
    }
    // Why the controls of the window, by the settings each one shows, do not cover every setting of a section exactly once; null when they do.
    public static string? ControlProblem(IEnumerable<string> controlled)
    {
        var counts = controlled.GroupBy(name => name).ToDictionary(group => group.Key, group => group.Count());
        var problems = Table.Where(setting => setting.Section is not null && counts.GetValueOrDefault(setting.Name) != 1).Select(setting => $"{setting.Name} has {counts.GetValueOrDefault(setting.Name)} controls")
            .Concat(counts.Keys.Where(name => !Table.Any(setting => setting.Name == name && setting.Section is not null)).Select(name => $"{name} has a control but no section")).ToList();
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
    // Why the shown preferences cannot be stored, with the section to fix them in; null when they can. Only changed sections are
    // checked, so a file edited by hand does not block saving something else.
    public static (string Section, string Message)? Problem(Settings shown, Settings stored)
    {
        var changed = Changed(shown, stored);
        if (changed.Contains(MeetingDefaults) && shown.MeetingRetentionDays is < 1 or > 3650)
            return (MeetingDefaults, "Keep completed meetings for: choose 1 to 3650 days.");
        if (!changed.Contains(Profiles) && !changed.Contains(Shortcuts)) return null;
        var profiles = shown.Profiles;
        if (profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Name) || !SpeechLanguages.IsChoice(profile.Language))
            || profiles.GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return (Profiles, "Profile names must be unique, and every profile needs a language.");
        var withShortcuts = profiles.Where(profile => profile.ShortcutKey != 0).Select(profile => (profile.Name, Shortcut: new ActivationShortcut(profile.ShortcutModifiers, profile.ShortcutKey))).ToList();
        foreach (var (name, shortcut) in withShortcuts)
        {
            if (shortcut.Problem is { } problem) return (Profiles, $"The shortcut of the profile “{name}” cannot be used: {problem}");
            if (shortcut == new ActivationShortcut(shown.ShortcutModifiers, shown.ShortcutKey)) return (Profiles, $"The profile “{name}” has the dictation shortcut. Choose another one for it.");
            if (shortcut == new ActivationShortcut(shown.MeetingShortcutModifiers, shown.MeetingShortcutKey)) return (Profiles, $"The profile “{name}” has the meeting shortcut. Choose another one for it.");
            if (withShortcuts.FirstOrDefault(other => other.Shortcut == shortcut && other.Name != name) is { Name: { } other })
                return (Profiles, $"The profiles “{name}” and “{other}” have the same shortcut. Choose another one for one of them.");
        }
        return null;
    }
    // The line of the question before unsaved preferences would be lost.
    public static string Describe(IReadOnlyList<string> sections) => sections.Count switch
    {
        0 => "",
        1 => $"Your changes under {sections[0]} are not saved yet.",
        _ => $"Your changes under {string.Join(", ", sections.SkipLast(1))} and {sections[^1]} are not saved yet."
    };
}
