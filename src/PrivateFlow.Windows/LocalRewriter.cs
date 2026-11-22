using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// The local models, Local polish and the meeting notes model, each served by its own llama-server on this PC; nothing leaves the PC.
// One task at a time holds the shared lock, loading and unloading included, and at most one model server runs: the notes stop a warm
// polish server before they load. A dictation that arrives while notes are written waits for them instead of failing.
internal static class LocalRewriter
{
    private sealed class AnswerRejectedException(string message) : InvalidOperationException(message);
    private sealed record Launch(string Engine, string Model, string[] Arguments, string MissingMessage)
    {
        // What the server runs: a server started otherwise, such as on the processor instead of the GPU, is started again.
        public string Identity => string.Join("\u0001", [Engine, Model, .. Arguments]);
        // Only a CUDA build of llama.cpp offloads layers; the processor build runs on the processor whatever -ngl says.
        public bool UsesGpu => ModelList.IsCudaBuild(Engine) && !Arguments.Contains("--device") && !(Arguments.Length > 1 && Arguments[0] == "-ngl" && Arguments[1] == "0");
    }
    // For the checks: what the Local polish server would run with these settings.
    internal static string PolishServerIdentity(Settings settings) => PolishLaunch(settings).Identity;
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) { running = null; gate.Release(); } }
    }
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly LocalServer Polish = new(), Notes = new();
    private static volatile string? running;
    public const string NotesTask = "the meeting notes";
    public const string PolishMissing = "Local polish is not set up on this PC.";
    // The model scripts/setup-rewrite.ps1 downloads for Local polish, named on the setup action; the runtime comes on top.
    public static long PolishModelBytes => ModelCatalogue.Model(ModelCatalogue.PolishModel).Bytes;
    public static string PolishSetupLabel => SetupLabel(PolishModelBytes);
    // "Set up Local polish (2,497 MB)": the size of the model the setup of Local polish downloads, the one chosen under Models.
    public static string SetupLabel(long modelBytes) => $"Set up Local polish ({(modelBytes / 1_000_000).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} MB)";
    public static bool PolishInstalled(Settings settings) => File.Exists(settings.LocalRewriteEnginePath) && File.Exists(settings.LocalRewriteModelPath);
    public const string NotesMissing = "The meeting notes model is not installed.";
    public const string PolishWithNotesModelMissing = "Local polish uses the meeting notes model, which is not installed. Install it under Settings → Models.";

    // What a waiting task tells the user; null when the local back end is free.
    public static string? Running => running;

    // Waits for the lock; when another task holds it, waiting hears at once what the task waits for. Cancellation ends the wait immediately;
    // a wait beyond waitLimit throws TimeoutException. waitEnded hears when a wait ends other than by cancellation: true when the task
    // got the lock, false when the wait ran out.
    internal static async Task<IDisposable> EnterAsync(string task, Action<string>? waiting, TimeSpan waitLimit, CancellationToken token, Action<bool>? waitEnded = null)
    {
        if (!Gate.Wait(0, CancellationToken.None))
        {
            waiting?.Invoke(running ?? "another local model task");
            var entered = await Gate.WaitAsync(waitLimit, token);
            waitEnded?.Invoke(entered);
            if (!entered) throw new TimeoutException($"Waited too long for {running ?? "another local model task"}.");
        }
        running = task;
        return new Lease(Gate);
    }

    public static async Task<string> RewriteAsync(string text, Settings settings, Action<string>? waiting, CancellationToken token, Action<bool>? waitEnded = null)
    {
        // Checked before the wait, so a dictation without the polish model does not wait for the notes only to fall back to Clean.
        var launch = PolishLaunch(settings);
        if (!File.Exists(launch.Engine) || !File.Exists(launch.Model)) throw new InvalidOperationException(launch.MissingMessage);
        var quotes = new List<string>();
        var sent = ConcisePrompt.Protect(text, quotes);
        string answer;
        IDisposable lease;
        // The wait is limited by Settings.PolishWaitSeconds; past it the dictation pastes Clean and says why.
        try { lease = await EnterAsync("the local polish", waiting, TimeSpan.FromSeconds(settings.PolishWaitSeconds), token, waitEnded); }
        catch (TimeoutException) { throw new TimeoutException($"Polish skipped because the wait for {Running ?? "another local model task"} ran out."); }
        using (lease)
            answer = await Polish.CompleteAsync(launch, ConcisePrompt.System(settings.Vocabulary), ConcisePrompt.Wrap(sent) + "\n/no_think", 4096,
                TimeSpan.FromMinutes(3), null, "Local polishing", token);
        var result = ConcisePrompt.Unwrap(answer);
        if (string.IsNullOrWhiteSpace(result) || !ConcisePrompt.IsPlausible(sent, result)) throw new InvalidOperationException("Local model did not return a valid rewrite.");
        result = ConcisePrompt.Restore(result, quotes) ?? throw new InvalidOperationException("Local model changed quoted text.");
        if (!RewriteIntegrity.PreservesMeaning(text, result, settings.TechnicalMode)) throw new InvalidOperationException("Local model changed protected facts or technical text.");
        return result;
    }

    // One request to the meeting notes model, constrained to the JSON schema. The model is loaded for this request only and unloaded
    // right after the answer or failure, so its GPU memory is free again for speech recognition.
    public static async Task<string> WriteNotesAsync(Settings settings, string system, string user, int maximumTokens, TimeSpan timeLimit, string jsonSchema, CancellationToken token)
    {
        // Checked before the lock, so a missing model neither waits for a polish dictation nor stops a warm polish server.
        if (!MeetingNotesModel.IsInstalled(settings)) throw new InvalidOperationException(NotesMissing);
        // Stopping a server waits up to 10 s for its exit, so the notes run on the thread pool, not on the interface thread that pressed the button.
        return await Task.Run(async () =>
        {
            using (await EnterAsync(NotesTask, null, Timeout.InfiniteTimeSpan, token))
            {
                Polish.Shutdown();
                try { return await Notes.CompleteAsync(NotesLaunch(settings), system, user, maximumTokens, timeLimit, jsonSchema, "Local notes", token); }
                finally { Notes.Shutdown(); }
            }
        }, CancellationToken.None);
    }

    // The meeting notes model can polish too; it then runs where the notes run, with the polish model's context.
    private static Launch PolishLaunch(Settings settings) => settings.LocalPolishModel == LocalPolishModel.MeetingNotesModel
        ? new(settings.MeetingNotesEnginePath, settings.MeetingNotesModelPath,
            [.. GpuArguments(settings.MeetingNotesGpuLayers), "-c", "8192", "-np", "1", "-t", Threads, "--reasoning", "off", "--chat-template-kwargs", "{\"enable_thinking\":false}"],
            PolishWithNotesModelMissing)
        : new(settings.LocalRewriteEnginePath, settings.LocalRewriteModelPath,
            ["-ngl", PolishModelGpuLayers.ToString(), "-c", "8192", "-t", Threads, "--chat-template-kwargs", "{\"enable_thinking\":false}"],
            PolishMissing);
    // The notes runtime offloads to the GPU; automatic layers let llama.cpp keep its default 1 GiB margin of GPU memory free and leave the
    // remaining layers on the processor when the speech models already use the GPU. One slot gets the whole context.
    private static Launch NotesLaunch(Settings settings) => new(settings.MeetingNotesEnginePath, settings.MeetingNotesModelPath,
        [.. GpuArguments(settings.MeetingNotesGpuLayers), "-c", settings.MeetingNotesContextTokens.ToString(), "-np", "1",
         "-t", Threads, "--reasoning", "off", "--chat-template-kwargs", "{\"enable_thinking\":false}"],
        NotesMissing);
    // The Local polish model runs on the processor; its llama.cpp is the processor build.
    public const int PolishModelGpuLayers = 0;
    private static string Threads => Math.Min(8, Environment.ProcessorCount).ToString();
    // -1 lets llama.cpp fit as many layers as free GPU memory allows. With 0 a GPU build of llama.cpp would still hand large operations
    // to the GPU, so no device is used at all.
    private static string[] GpuArguments(int gpuLayers) => gpuLayers < 0 ? ["-ngl", "auto"] : gpuLayers == 0 ? ["-ngl", "0", "--device", "none"] : ["-ngl", gpuLayers.ToString()];

    // Called by the window's timer. Stopping a server waits up to 10 s for its exit, so it runs on the thread pool, holding the lock until then.
    public static void UnloadIfIdle()
    {
        if (!Polish.IdleFor(TimeSpan.FromMinutes(5)) || !Gate.Wait(0)) return;
        _ = Task.Run(() => { try { Polish.Shutdown(); } finally { Gate.Release(); } });
    }
    public static void Shutdown() { Polish.Shutdown(); Notes.Shutdown(); }
    // Before a meeting's final pass checks GPU memory: an idle Local polish server on the GPU (the meeting notes model holds about 3.1 GB)
    // is stopped under the shared lock; a polish in progress finishes first, and notes stop it themselves.
    public static async Task FreeGpuAsync(CancellationToken token)
    {
        if (!Polish.OnGpu) return;
        // Stopping a server waits up to 10 s for its exit, so it runs on the thread pool, as for the notes.
        await Task.Run(async () =>
        {
            bool entered;
            try { entered = await Gate.WaitAsync(TimeSpan.FromSeconds(30), token); } catch (OperationCanceledException) { return; }
            if (!entered) return;
            try { Polish.Shutdown(); } finally { Gate.Release(); }
        }, CancellationToken.None);
    }
    // Stops both servers unless a task holds them, so a model file can be removed; false while one is in use. On the thread pool, as
    // for the notes, since stopping waits for the exit.
    public static Task<bool> StopIdleAsync() => Task.Run(() =>
    {
        if (!Gate.Wait(0)) return false;
        try { Shutdown(); return true; } finally { Gate.Release(); }
    });
    public const string SetupTask = "the setup of a local model";
    public const string RuntimeInPlace = "Local model runtime is in place.";
    // For a setup that extracts over the local model runtimes: both servers stop and the shared lock stays held, so a dictation waits for
    // the setup as it waits for notes, until the lease is disposed; null while another task holds the lock.
    public static Task<IDisposable?> ReserveForSetupAsync() => Task.Run<IDisposable?>(() =>
    {
        if (!Gate.Wait(0)) return null;
        running = SetupTask;
        var lease = new Lease(Gate);
        try { Shutdown(); return lease; }
        catch { lease.Dispose(); throw; }
    });

    // One llama-server process; callers hold the shared lock.
    private sealed class LocalServer
    {
        private Process? process;
        private string? identity, key;
        private bool onGpu;
        public bool OnGpu => process is { HasExited: false } && onGpu;
        private Uri? address;
        private DateTime lastUse;
        public bool IdleFor(TimeSpan span) => process is not null && DateTime.UtcNow - lastUse >= span;

        // With a JSON schema the server constrains the answer to it. An answer that arrives keeps the model loaded however the caller
        // judges it; failures unload it.
        public async Task<string> CompleteAsync(Launch launch, string system, string user, int maximumTokens, TimeSpan timeLimit, string? jsonSchema, string task, CancellationToken token)
        {
            try
            {
                await StartAsync(launch, token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(timeLimit);
                using var request = Request("v1/chat/completions", HttpMethod.Post);
                var messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } };
                var thinking = new { enable_thinking = false };
                request.Content = jsonSchema is not null
                    ? JsonContent.Create(new { model = "local", temperature = 0, max_tokens = maximumTokens, chat_template_kwargs = thinking, response_format = new { type = "json_object", schema = JsonNode.Parse(jsonSchema) }, messages })
                    : JsonContent.Create(new { model = "local", temperature = 0, max_tokens = maximumTokens, chat_template_kwargs = thinking, messages });
                using var response = await Http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(task + " failed.");
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                var choice = document.RootElement.GetProperty("choices")[0];
                lastUse = DateTime.UtcNow;
                if (choice.GetProperty("finish_reason").GetString() != "stop") throw new AnswerRejectedException("Local output was incomplete.");
                return choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { Shutdown(); throw new TimeoutException(task + " timed out."); }
            catch (OperationCanceledException) { if (identity is null) Shutdown(); else lastUse = DateTime.UtcNow; throw; }
            catch (AnswerRejectedException) { throw; }
            catch { Shutdown(); throw; }
        }
        private HttpRequestMessage Request(string path, HttpMethod method)
        {
            // Quit clears the address without the lock, so a request starting meanwhile fails as any stopped model does.
            var request = new HttpRequestMessage(method, new Uri(address ?? throw new InvalidOperationException("The local model was stopped."), path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return request;
        }
        private async Task StartAsync(Launch launch, CancellationToken token)
        {
            if (process is { HasExited: false } && identity == launch.Identity) return;
            Shutdown();
            if (!File.Exists(launch.Engine) || !File.Exists(launch.Model)) throw new InvalidOperationException(launch.MissingMessage);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            address = new($"http://127.0.0.1:{port}/");
            var start = new ProcessStartInfo(launch.Engine) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(launch.Engine)! };
            foreach (var arg in new[] { "-m", launch.Model, "--host", "127.0.0.1", "--port", port.ToString(), "--api-key", key }
                .Concat(launch.Arguments).Concat(["--jinja", "--no-webui", "--no-slots"])) start.ArgumentList.Add(arg);
            // Held locally: Quit may stop and clear the field while this waits for the server.
            var started = process = Process.Start(start) ?? throw new InvalidOperationException("The local model could not start.");
            HelperProcessJob.Attach(started);
            _ = DrainAsync(started.StandardOutput); _ = DrainAsync(started.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (started.HasExited) throw new InvalidOperationException("The local model could not load. Check memory and setup.");
                // The port was free when chosen but is released before llama-server binds it, so another
                // program could take it. The bearer key and text go only to a listener llama-server owns.
                var owners = LoopbackListeners.OwnerProcessIds(port);
                if (owners.Count == 0) { await Task.Delay(250, timeout.Token); continue; }
                if (owners.Any(owner => owner != started.Id)) throw new InvalidOperationException("Another program took the local model's port. Try again.");
                try
                {
                    using var request = Request("health", HttpMethod.Get);
                    using var response = await Http.SendAsync(request, timeout.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, timeout.Token);
            }
            identity = launch.Identity; onGpu = launch.UsesGpu;
            lastUse = DateTime.UtcNow;
        }
        private static async Task DrainAsync(StreamReader reader)
        { try { var buffer = new char[2048]; while (await reader.ReadAsync(buffer) > 0) { } } catch (IOException) { } catch (ObjectDisposedException) { } }
        public void Shutdown()
        {
            // Taken atomically: Quit stops the servers without the lock while a notes request may be stopping its own on the thread pool.
            if (Interlocked.Exchange(ref process, null) is { } stopping)
            {
                // Waiting for the exit frees the model's memory before the next model loads.
                try { if (!stopping.HasExited) { stopping.Kill(true); stopping.WaitForExit(10_000); } } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                stopping.Dispose();
            }
            identity = key = null; address = null; onGpu = false;
        }
    }
}
