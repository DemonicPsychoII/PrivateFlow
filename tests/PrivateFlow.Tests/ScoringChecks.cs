using PrivateFlow.Core;
using static Fixtures;
using static Harness;

static class ScoringChecks
{
    public static void VerifySegmentAssemblyAndScoring()
    {
        var assembly = new List<TranscriptSegment> { new(0, 20, "We will ship tomorrow.") };
        SegmentAssembly.Append(assembly, [new(0, 4, "ship tomorrow. Then verify." )], 19.5);
        Same(assembly[^1].Text, "Then verify.", "overlapping chunk words reconcile only at an audio overlap");
        SegmentAssembly.Append(assembly, [new(0, 2, "Then verify.")], 24);
        Same(assembly[^1].Text, "Then verify.", "intentional repetition without overlap survives");
        Same(Normalized("¿Qué tal, Joaquín? Don’t STOP!"), "qué tal joaquín don't stop", "scoring folds case and punctuation but keeps accents as letters");
        Same(Normalized("We are at minute forty-two."), Normalized("We are at minute 42"), "scoring treats number words and digits alike");
        Same(Normalized("twelve thirty-four"), "12 34", "an English spoken clock time scores as its digits");
        Same(Normalized("12:34"), "12 34", "a written clock time scores as its hour and minute");
        Same(Normalized("doce y media"), "12 30", "a Spanish half hour scores as thirty minutes");
        Same(Normalized("cuarenta y dos, dos mil veinticinco, ciento veinte"), "42 2025 120", "Spanish tens, thousands and hundreds compose");
        Same(Normalized("zweiundvierzig, dreihundertzwölf, zweitausendfünf"), "42 312 2005", "German compound number words compose");
        Same(Normalized("um zwölf Uhr dreißig"), Normalized("um 12.30 Uhr"), "a German clock time scores alike spoken or written");
        Same(Normalized("one hundred and twenty three, 1,000 and 09"), "123 1000 and 9", "hundreds, thousands separators and leading zeros normalize");
        Same(Normalized("Stunde rund Kunden Tausende"), "stunde rund kunden tausende", "German words containing number parts stay words");
        Same(Normalized("one two three"), "1 2 3", "a digit sequence keeps each digit");
        var perfect = TranscriptScoring.Score(["We are at [minute 42].", "It did [not] fail."], "We are at minute forty two. It did not fail.");
        Check(perfect.Errors().Rate == 0 && perfect.CriticalItems.All(item => item.Recalled), "an identical transcript has no word errors and recalls every critical item");
        var flawed = TranscriptScoring.Score(["We are at [minute 42].", "It did [not] fail.", "Call [Priya] at [twelve thirty-four]."], "We are at minute forty. It did fail. Call Maria at 12:34 now.");
        var flawedErrors = flawed.Errors();
        Check(flawedErrors is { ReferenceWords: 14, Substitutions: 2, Deletions: 1, Insertions: 1 } && Math.Abs(flawedErrors.Rate - 4 / 14.0) < 1e-9, "word error rate counts substitutions, deletions and insertions");
        Check(flawed.CriticalItems.Select(item => item.Recalled).SequenceEqual([false, false, false, true]), "critical recall misses a wrong number, a dropped negation and a wrong name, and finds the written time");
        Same(flawed.CriticalItems[0].Heard, "minute 40", "a missed critical item shows what was heard instead");
        Check(flawed.Errors(utterance => utterance == 1) is { ReferenceWords: 4, Deletions: 1, Insertions: 0 } && flawed.Errors(utterance => utterance == 2) is { Substitutions: 1, Insertions: 1 }, "errors attribute to the utterance whose words they follow");
        Same(flawed.Heard(2), "call maria at 12 34 now", "an utterance shows the words aligned to it");
        Check(TranscriptScoring.Score([], "hello").Errors().Rate == 1 && TranscriptScoring.Score([], "").Errors().Rate == 0, "text where no speech was expected counts as a full error");
        Check(TranscriptScoring.Score(["[minute 42]"], "minute the 42").CriticalItems.Single() is { Recalled: false, FormattingOnly: false }, "a critical item split by an inserted word is not recalled");
        Check(TranscriptScoring.Score(["finished at [twelve thirty-four]"], "finished at 1234").CriticalItems.Single() is { Recalled: false, FormattingOnly: true, Heard: "1234" }, "a time written without its colon is missed but marked as formatting only");
        Check(!flawed.CriticalItems[0].FormattingOnly, "a wrong number is not a formatting difference");
        Check(TranscriptScoring.Score(["one two three four"], [new TranscriptSegment(10, 12, "one two"), new TranscriptSegment(20, 20, "three four")]).Errors().Rate == 0, "a transcript in several segments scores as one text");
        Check(TranscriptScoring.SpokenLanguages([(0, 8, "en"), (9, 19, "es"), (19.8, 25, "de")], 0, 20).SequenceEqual(["es", "en"]), "a window lists its languages by spoken time, ignoring brief edge overlaps");
        Check(SpeechSegmentation.RecordingWindows(new float[16000 * 45], 320000, 96000).SequenceEqual([(0, 318400), (318400, 318400), (636800, 83200)])
            && SpeechSegmentation.RecordingWindows(new float[100], 320000, 96000).Count == 0, "silence is cut 100 ms before a full window; nothing under 10 ms is transcribed");
        Check(TranscriptScoring.Score(["cerca de [400 000] casos"], "cerca de 400.000 casos").CriticalItems.Single().Recalled && TranscriptScoring.Words("unit 42 and 7") is ["unit", "42", "and", "7"],
            "thousands grouped with a space match the same number written without; other numbers stay apart");
    }

    public static void VerifyScoringFormatting()
    {
        // Correct locale and unit formatting is no error; a lost colon is a formatting-only difference, counted apart from real misses.
        Same(Normalized("um zwölf Uhr dreißig"), Normalized("um 12.30"), "a German clock time written without Uhr scores alike");
        Same(Normalized("um fünf Uhr"), "um 5 uhr", "a German full hour keeps its Uhr");
        Same(Normalized("five o'clock"), Normalized("5:00"), "a full hour scores alike as o'clock or with zero minutes");
        Same(Normalized("las 12 y media"), Normalized("las doce y media"), "a Spanish half hour scores alike after a digit");
        Same(Normalized("a las doce y treinta y cuatro"), Normalized("a las 12:34"), "a Spanish clock time with y scores as hour and minutes");
        Same(Normalized("veinticinco y cuarenta"), "25 y 40", "y after a number above 23 stays a word");
        Same(Normalized("faltan 20 mil euros, 20.000 euros y veinte mil euros"), "faltan 20000 euro 20000 euro y 20000 euro", "digits with a written thousand score as the number");
        Same(Normalized("512 kilobytes, sixty decibels, five volts"), Normalized("512 KB, 60 dB, 5 V"), "written unit symbols score as their spoken names");
        Same(Normalized("ten percent, diez por ciento, zehn Prozent"), Normalized("10 %, 10 %, 10 %"), "a percent sign scores as the spoken word in each language");
        var lostColon = TranscriptScoring.Score(["finished at [twelve thirty-four] today"], "finished at 1234 today").Errors();
        Check(lostColon is { Substitutions: 1, Deletions: 1, FormattingOnly: 2 } && lostColon.Recall < 1 && lostColon.RecallIgnoringFormatting == 1, "words heard but written together count as formatting-only misses");
        Check(TranscriptScoring.Score(["we are at minute [42]"], "we are at minute 24").Errors().FormattingOnly == 0, "a wrong number is no formatting-only miss");
        Check((TranscriptScoring.Score(["a b"], "ab").Errors() + TranscriptScoring.Score(["c"], "d").Errors()) is { ReferenceWords: 3, FormattingOnly: 2 }, "formatting-only misses add up across utterances");
    }
}
