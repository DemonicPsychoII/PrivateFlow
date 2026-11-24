using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;
using PrivateFlow.Core;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// A dictation case is one recording with the words spoken (Reference) and, when the on-device edits change
// them, the text Clean and the rewrite styles should produce (Edited). Critical items are marked in square brackets.
internal sealed class DictationCase
{
    public string Name { get; set; } = "";
    public string Language { get; set; } = "";
    // A WAV or other audio file; "noise:<dBFS>" generates five seconds of room noise instead.
    public string Clip { get; set; } = "";
    public string Source { get; set; } = "synthetic";
    public string Reference { get; set; } = "";
    public string? Edited { get; set; }
    public bool SpokenFormatting { get; set; }
    public bool LiteralMode { get; set; }
    public bool TechnicalMode { get; set; }
    public string Vocabulary { get; set; } = "";
    public List<PhraseRule> Corrections { get; set; } = [];
    public List<PhraseRule> Snippets { get; set; } = [];
    public List<DictationCheck> Checks { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}
// Includes and Excludes compare normalized words unless Text compares the pasted text itself.
internal sealed class DictationCheck
{
    public string Description { get; set; } = "";
    public string Style { get; set; } = "Clean";
    public string? Includes { get; set; }
    public string? Excludes { get; set; }
    public bool Text { get; set; }
}
internal sealed class DictationSuite
{
    // "auto" lets Whisper detect each recording's language; "fixed" selects the language the case is spoken in.
    public string LanguageMode { get; set; } = "auto";
    public List<DictationCase> Cases { get; set; } = [];
}

internal sealed record CheckReport(string Description, string Style, bool Passed);
internal sealed record StyleReport(string Style, string Final, string? Notice, ErrorReport Words, int CriticalItems, int CriticalRecalled, int CriticalFormattingOnly, IReadOnlyList<string> Missed);
internal sealed record CaseReport(string Name, string Language, string Detected, bool WrongLanguage, string Source, IReadOnlyList<string> Tags, double Seconds, double ProcessingSeconds,
    string Raw, IReadOnlyList<StyleReport> Styles, IReadOnlyList<CheckReport> Checks);
internal sealed record DictationGroup(string Style, string Language, string Source, ErrorReport Words, int CriticalItems, int CriticalRecalled, int CriticalFormattingOnly, int Cases, int WrongLanguage);

internal static class DictationEvaluation
{
    private static readonly OutputStyle[] Styles = [OutputStyle.Exact, OutputStyle.Clean, OutputStyle.Concise, OutputStyle.LocalPolish];

    public static async Task RunAsync(string suiteFile, string reportFile)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            var suite = JsonSerializer.Deserialize<DictationSuite>(await File.ReadAllTextAsync(suiteFile)) ?? throw new InvalidDataException(suiteFile);
            await using var stub = new RewriteStub();
            var settings = Settings.Load().Snapshot();
            settings.RewriteEndpoint = stub.Endpoint; settings.RewriteKeyVariable = stub.KeyVariable; settings.RewriteModel = "evaluation-echo";
            var localPolishInstalled = File.Exists(settings.LocalRewriteEnginePath) && File.Exists(settings.LocalRewriteModelPath);
            var loading = Stopwatch.StartNew();
            await WhisperEngine.TranscribeChunkAsync(new float[16000], settings, CancellationToken.None);
            var modelLoadSeconds = loading.Elapsed.TotalSeconds;
            var folder = Path.Combine(Settings.Root, "evaluation-dictation"); Directory.CreateDirectory(folder);
            var cases = new List<CaseReport>(); var conciseSent = 0;
            foreach (var entry in suite.Cases)
            {
                var session = settings.Snapshot();
                session.Language = suite.LanguageMode == "fixed" && SpeechLanguages.IsSpoken(entry.Language) ? entry.Language : "auto";
                session.Vocabulary = entry.Vocabulary; session.SpokenFormatting = entry.SpokenFormatting; session.LiteralMode = entry.LiteralMode; session.TechnicalMode = entry.TechnicalMode;
                session.Corrections = entry.Corrections; session.Snippets = entry.Snippets;
                var clip = entry.Clip.StartsWith("noise:", StringComparison.Ordinal) ? Noise(folder, double.Parse(entry.Clip[6..], System.Globalization.CultureInfo.InvariantCulture)) : entry.Clip;
                var watch = Stopwatch.StartNew();
                var speech = await WhisperEngine.TranscribeRecordingAsync(clip, session, CancellationToken.None);
                var processing = watch.Elapsed.TotalSeconds;
                // The same language resolution as a live dictation: libraries then use the detected language.
                if (session.Language == "auto" && SpeechLanguages.IsSpoken(speech.Language)) session.Language = speech.Language;
                var styles = new List<StyleReport>(); var finals = new Dictionary<OutputStyle, string>();
                foreach (var style in Styles)
                {
                    var styled = session.Snapshot(); styled.OutputStyle = style;
                    var result = await DictationPipeline.ProcessAsync(speech.Text, styled, CancellationToken.None);
                    finals[style] = result.Final;
                    if (style == OutputStyle.Concise && !session.LiteralMode && !string.IsNullOrWhiteSpace(result.Final)) conciseSent++;
                    var reference = style == OutputStyle.Exact || entry.Edited is null ? entry.Reference : entry.Edited;
                    var score = TranscriptScoring.Score(reference.Length == 0 ? [] : [reference], result.Final);
                    styles.Add(new(style.ToString(), result.Final, result.Notice, ErrorReport.From(score.Errors()), score.CriticalItems.Count, score.CriticalItems.Count(item => item.Recalled),
                        score.CriticalItems.Count(item => item.FormattingOnly), score.CriticalItems.Where(item => !item.Recalled).Select(item => $"'{item.Text}' heard '{item.Heard}'").ToArray()));
                }
                var checks = entry.Checks.Select(check => new CheckReport(check.Description, check.Style, Holds(check, finals[Enum.Parse<OutputStyle>(check.Style)]))).ToList();
                cases.Add(new(entry.Name, entry.Language, speech.Language, SpeechLanguages.IsSpoken(entry.Language) && speech.Language != entry.Language, entry.Source, entry.Tags,
                    Math.Round(speech.Seconds, 2), Math.Round(processing, 3), speech.Text, styles, checks));
            }
            var functional = new List<CheckReport>();
            void Functional(string description, bool passed) => functional.Add(new(description, "", passed));
            // Literal mode keeps the words as spoken, so neither rewrite runs for it (DictationPipeline).
            var literal = suite.Cases.Where(entry => entry.LiteralMode).Select(entry => entry.Name).ToHashSet();
            Functional("Concise reached the loopback stub once per non-empty dictation outside literal mode, always with its key", stub.Requests == conciseSent && stub.Authorized == stub.Requests);
            Functional("Concise with the echo stub pastes what Clean pastes", cases.All(entry => entry.Styles.Single(style => style.Style == "Concise") is var concise && concise.Notice is null
                && concise.Final == entry.Styles.Single(style => style.Style == "Clean").Final));
            if (!localPolishInstalled)
                Functional("Local polish, not installed, falls back to Clean with a notice, and pastes Clean without one in literal mode", cases.All(entry => entry.Styles.Single(style => style.Style == "LocalPolish") is var local
                    && local.Final == entry.Styles.Single(style => style.Style == "Clean").Final
                    && (literal.Contains(entry.Name) ? local.Notice is null : string.IsNullOrWhiteSpace(local.Final) || local.Notice?.StartsWith("Used Clean") == true)));
            Functional($"Keep speech model warm {(settings.KeepModelWarm ? "on keeps" : "off unloads")} the worker after a dictation", WhisperEngine.IsWarm == settings.KeepModelWarm);
            var flipped = settings.Snapshot(); flipped.KeepModelWarm = !settings.KeepModelWarm;
            var longest = suite.Cases.Where(entry => !entry.Clip.StartsWith("noise:", StringComparison.Ordinal)).MaxBy(entry => new FileInfo(entry.Clip).Length);
            if (longest is not null)
            {
                var (file, _) = await WhisperEngine.TranscribeSegmentsAsync(longest.Clip, flipped, CancellationToken.None);
                Functional($"Keep speech model warm {(flipped.KeepModelWarm ? "on keeps" : "off unloads")} the worker after a dictation", WhisperEngine.IsWarm == flipped.KeepModelWarm);
                Functional("File transcription returns ordered segments inside the recording and exports them as subtitles",
                    file.Segments.Count > 0 && file.Segments.Zip(file.Segments.Skip(1)).All(pair => pair.First.Start <= pair.Second.Start)
                    && file.Segments.All(segment => segment.Start >= 0 && segment.End <= file.Seconds + 0.5)
                    && Regex.Matches(MeetingArchive.ExportSrt(file.Segments), @"^\d\d:\d\d:\d\d,\d{3} --> ", RegexOptions.Multiline).Count == file.Segments.Count);
            }
            Directory.Delete(folder, true);
            var groups = cases.SelectMany(entry => entry.Styles.Select(style => (entry, style)))
                .GroupBy(pair => (pair.style.Style, pair.entry.Language, pair.entry.Source))
                .Select(group => new DictationGroup(group.Key.Style, group.Key.Language, group.Key.Source,
                    ErrorReport.From(group.Aggregate(WordErrors.None, (sum, pair) => sum + Errors(pair.style.Words))),
                    group.Sum(pair => pair.style.CriticalItems), group.Sum(pair => pair.style.CriticalRecalled), group.Sum(pair => pair.style.CriticalFormattingOnly),
                    group.Count(), group.Count(pair => pair.entry.WrongLanguage))).OrderBy(group => group.Source).ThenBy(group => group.Style).ThenBy(group => group.Language).ToArray();
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            {
                Passed = true, Created = DateTimeOffset.Now, Model = Path.GetFileName(settings.ModelPath), Backend = SpeechSetup.Processor(settings.EnginePath) == "GPU" ? "CUDA" : "CPU",
                suite.LanguageMode, settings.SpeechDetection, settings.KeepModelWarm, LocalPolishInstalled = localPolishInstalled, ModelLoadSeconds = Math.Round(modelLoadSeconds, 2),
                StubRequests = stub.Requests, FailedChecks = cases.Sum(entry => entry.Checks.Count(check => !check.Passed)) + functional.Count(check => !check.Passed),
                Functional = functional, Groups = groups, Cases = cases
            }, options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }, options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static WordErrors Errors(ErrorReport words) => new(words.ReferenceWords, words.Substitutions, words.Deletions, words.Insertions, words.FormattingOnly);
    private static bool Holds(DictationCheck check, string final)
    {
        bool Contains(string text) => check.Text ? final.Contains(text, StringComparison.Ordinal)
            : (" " + string.Join(" ", TranscriptScoring.Words(final)) + " ").Contains(" " + string.Join(" ", TranscriptScoring.Words(text)) + " ", StringComparison.Ordinal);
        return (check.Includes is null || Contains(check.Includes)) && (check.Excludes is null || !Contains(check.Excludes));
    }
    // Uniform room noise at the given RMS level, as the meeting scenarios add to the microphone.
    private static string Noise(string folder, double level)
    {
        var file = Path.Combine(folder, $"noise{level}.wav");
        var random = new Random(1); var amplitude = (float)(Math.Pow(10, level / 20) * Math.Sqrt(3));
        var samples = Enumerable.Range(0, 5 * 16000).Select(_ => (float)(random.NextDouble() * 2 - 1) * amplitude).ToArray();
        using (var writer = new WaveFileWriter(file, new WaveFormat(16000, 16, 1))) writer.WriteSamples(samples, 0, samples.Length);
        return file;
    }
}

// An OpenAI-compatible chat completions endpoint on loopback that returns the transcript it was sent,
// so Concise runs its whole path (key, request, quotation tokens, guards, layout) without a real model.
internal sealed class RewriteStub : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly Task serving;
    private int requests, authorized;
    public string KeyVariable { get; } = "PRIVATE_FLOW_EVALUATION_KEY_" + Guid.NewGuid().ToString("N");
    public string Endpoint { get; }
    public int Requests => requests;
    public int Authorized => authorized;
    public RewriteStub()
    {
        listener.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        Environment.SetEnvironmentVariable(KeyVariable, key);
        serving = ServeAsync();
    }
    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; }
            using (client) try { await AnswerAsync(client.GetStream()); } catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or FormatException) { }
        }
    }
    private async Task AnswerAsync(NetworkStream stream)
    {
        var header = new List<byte>(); var one = new byte[1];
        while (!(header.Count > 3 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10))
        {
            await stream.ReadExactlyAsync(one, stop.Token); header.Add(one[0]);
            if (header.Count > 65536) throw new InvalidDataException();
        }
        var head = Encoding.ASCII.GetString([.. header]);
        var body = new MemoryStream();
        if (Regex.Match(head, @"Content-Length: (\d+)", RegexOptions.IgnoreCase) is { Success: true } length)
        { var bytes = new byte[int.Parse(length.Groups[1].Value)]; await stream.ReadExactlyAsync(bytes, stop.Token); body.Write(bytes); }
        else
            while (true)
            {
                var line = new List<byte>();
                do { await stream.ReadExactlyAsync(one, stop.Token); line.Add(one[0]); } while (one[0] != 10);
                var size = Convert.ToInt32(Encoding.ASCII.GetString([.. line]).Trim(), 16);
                var chunk = new byte[size + 2]; await stream.ReadExactlyAsync(chunk, stop.Token); body.Write(chunk, 0, size);
                if (size == 0) break;
            }
        Interlocked.Increment(ref requests);
        if (head.StartsWith("POST /v1/chat/completions ", StringComparison.Ordinal) && head.Contains("Authorization: Bearer " + key + "\r\n", StringComparison.Ordinal)) Interlocked.Increment(ref authorized);
        using var request = JsonDocument.Parse(body.ToArray());
        var transcript = ConcisePrompt.Unwrap(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? "");
        var reply = JsonSerializer.SerializeToUtf8Bytes(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = transcript } } } });
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {reply.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
        await stream.WriteAsync(reply, stop.Token);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        try { await serving; } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        Environment.SetEnvironmentVariable(KeyVariable, null); stop.Dispose();
    }
}
