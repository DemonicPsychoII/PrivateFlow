using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PrivateFlow.Core;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// Stopping the Local polish server while a dictation awaits the polish on the interface thread. The stand-in llama-server is this
// host started with llama-server's arguments; this process debugs it and holds its exit for a few seconds, as a real server's exit
// can take while it frees the model's memory.
internal static partial class WorkspaceVerification
{
    private const double StandInExitSeconds = 3;

    // llama-server's arguments as LocalRewriter passes them, the model first.
    public static bool IsPolishServerStandIn(string[] arguments) => arguments is ["-m", _, "--host", "127.0.0.1", "--port", _, ..];

    // The model file's first line is what the stand-in does: "load-forever" never ends loading, "fail" fails every completion and
    // "answer" answers with the second line. Until this host debugs it, so its exit is held, it is loading too. It reports its start
    // with a file named after its process in the "servers" folder beside the model file.
    public static int RunPolishServerStandIn(string[] arguments)
    {
        string Argument(string name) => arguments[Array.IndexOf(arguments, name) + 1];
        var model = Argument("-m");
        var behaviour = File.ReadAllLines(model);
        var listener = new TcpListener(IPAddress.Loopback, int.Parse(Argument("--port")));
        listener.Start();
        var servers = Path.Combine(Path.GetDirectoryName(model)!, "servers");
        var report = Path.Combine(servers, Environment.ProcessId + ".writing");
        File.WriteAllText(report, model);
        File.Move(report, Path.Combine(servers, Environment.ProcessId.ToString()));
        while (true)
        {
            using var client = listener.AcceptTcpClient();
            try { AnswerAsStandIn(client, behaviour); } catch (IOException) { } catch (SocketException) { }
        }
    }

    private static void AnswerAsStandIn(TcpClient client, string[] behaviour)
    {
        var stream = client.GetStream();
        var received = new MemoryStream();
        var buffer = new byte[4096];
        string Received() => Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
        bool Read() { var count = stream.Read(buffer); received.Write(buffer, 0, count); return count > 0; }
        int headerEnd;
        while ((headerEnd = Received().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0) if (!Read()) return;
        var head = Received()[..headerEnd];
        // The whole request is read before the answer, so closing the connection does not reset it: a JSON body comes chunked.
        var chunked = Regex.IsMatch(head, @"(?im)^transfer-encoding:\s*chunked");
        var length = Regex.Match(head, @"(?im)^content-length:\s*(\d+)") is { Success: true } header ? int.Parse(header.Groups[1].Value) : 0;
        while (chunked ? !Received().EndsWith("\r\n0\r\n\r\n", StringComparison.Ordinal) : received.Length < headerEnd + 4 + length) if (!Read()) return;
        var (status, json) = head.Split(' ')[1] switch
        {
            "/health" => behaviour[0] == "load-forever" || !IsDebuggerPresent() ? (503, """{"error":{"code":503,"message":"Loading model"}}""") : (200, """{"status":"ok"}"""),
            "/v1/chat/completions" when behaviour[0] == "fail" => (500, """{"error":{"code":500,"message":"Stand-in failure"}}"""),
            "/v1/chat/completions" => (200, JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = behaviour[1] } } } })),
            _ => (404, """{"error":{"code":404,"message":"Not found"}}"""),
        };
        var body = Encoding.UTF8.GetBytes(json);
        stream.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Stand-in\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
        stream.Write(body);
        client.Client.Shutdown(SocketShutdown.Send);
    }

    // Escape while the server starts, a failed completion and a dictation with another launch each stop a server while the dictation
    // awaits the polish here, on the interface thread: the window stays responsive while the stop waits for the exit, and at most one
    // server runs afterwards. A cancel that arrives with the lock leaves the warm server of another launch running and starts none; one
    // that arrives while that server stops starts none either.
    private static async Task VerifyPolishServerStopsAsync(string output, Action<bool, string> check)
    {
        var folder = Path.Combine(output, "polish-server-stand-in");
        var servers = Directory.CreateDirectory(Path.Combine(folder, "servers")).FullName;
        const string Spoken = "Please send the report today.";
        Settings Polishing(string name, string behaviour)
        {
            var model = Path.Combine(folder, name + ".gguf");
            File.WriteAllText(model, behaviour + "\n" + Spoken);
            return new() { OutputStyle = OutputStyle.LocalPolish, LocalPolishModel = LocalPolishModel.PolishModel, LocalRewriteEnginePath = Environment.ProcessPath!, LocalRewriteModelPath = model };
        }
        // As the controller calls it: Escape on a dictation that did not wait cancels the dictation, one that waited keeps its Clean text.
        Task<DictationResult> Dictate(Settings polishing, CancellationToken dictation = default, CancellationToken skipPolish = default) =>
            DictationPipeline.ProcessAsync(Spoken, polishing, dictation, skipPolish: skipPolish);
        // The watchdog runs from before the polish starts, as a stop at its start would block this thread before the first await, and
        // a while after it ends, so a block right before the end is counted. A cancelled or failed dictation has no result.
        static async Task<(DictationResult? Result, double Seconds, double LongestGap)> Timed(Func<Task<DictationResult>> polish)
        {
            using var watchdog = new DispatcherWatchdog();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            DictationResult? result = null;
            try { result = await polish(); } catch (Exception) { }
            var seconds = clock.Elapsed.TotalSeconds;
            await Task.Delay(100);
            return (result, seconds, watchdog.LongestGap);
        }
        using var exits = new HeldServerExits(servers);
        var onInterfaceThread = System.Windows.Application.Current.Dispatcher.CheckAccess();

        var loading = Polishing("loading", "load-forever");
        using var escape = new CancellationTokenSource();
        var starting = Dictate(loading, escape.Token);
        await WaitAsync(() => exits.Running.Contains(loading.LocalRewriteModelPath), 30);
        var debugged = exits.Running.Contains(loading.LocalRewriteModelPath);
        var cancelled = await Timed(() => { escape.Cancel(); return starting; });
        check(onInterfaceThread && debugged && cancelled.Result is null && starting.IsCanceled
            && cancelled.Seconds >= StandInExitSeconds * 0.9 && cancelled.LongestGap < ResponsiveBound && exits.Running.Length == 0,
            $"Escape while the Local polish server starts cancels the dictation and stops the server off the interface thread: the window stays responsive while the stand-in takes {cancelled.Seconds:0.0} s to exit (longest gap {cancelled.LongestGap:0.00} s, bound {ResponsiveBound} s) and no server runs afterwards (running {exits.Running.Length})");

        var failed = await Timed(() => Dictate(Polishing("failing", "fail")));
        check(failed.Result?.Notice == "Used Clean: Local polishing failed." && failed.Seconds >= StandInExitSeconds * 0.9 && failed.LongestGap < ResponsiveBound && exits.Running.Length == 0,
            $"a failed Local polish stops its server off the interface thread: the window stays responsive while the stand-in takes {failed.Seconds:0.0} s to exit (longest gap {failed.LongestGap:0.00} s), the Clean text is used and no server runs afterwards ({failed.Result?.Notice}; running {exits.Running.Length})");

        var first = Polishing("first", "answer");
        var warm = await Dictate(first);
        var warmRunning = exits.Running;
        var second = Polishing("second", "answer");
        var changed = await Timed(() => Dictate(second));
        check(warm.Notice is null && warmRunning.SequenceEqual([first.LocalRewriteModelPath]) && changed.Result is { Notice: null, Final: Spoken }
            && changed.Seconds >= StandInExitSeconds * 0.9 && changed.LongestGap < ResponsiveBound && exits.Running.SequenceEqual([second.LocalRewriteModelPath]),
            $"a dictation with another launch stops the warm server off the interface thread before it starts its own: the window stays responsive while the old stand-in takes {changed.Seconds:0.0} s to exit (longest gap {changed.LongestGap:0.00} s), the polish runs and only the new server runs afterwards ({changed.Result?.Notice ?? "polished"}; running {exits.Running.Length})");

        // Escape or Cancel on a dictation that waited, arriving as the wait ends.
        using var cancelledAtLock = new CancellationTokenSource();
        cancelledAtLock.Cancel();
        var atLock = await Timed(() => Dictate(Polishing("third", "answer"), skipPolish: cancelledAtLock.Token));
        var keptRunning = exits.Running;
        var stopped = await LocalRewriter.StopIdleAsync();
        check(atLock.Result?.Notice == "Used Clean: " + DictationPipeline.PolishCancelled && atLock.Seconds < StandInExitSeconds / 2 && atLock.LongestGap < ResponsiveBound
            && keptRunning.SequenceEqual([second.LocalRewriteModelPath]) && stopped && exits.Running.Length == 0,
            $"a polish cancelled when it gets the lock keeps the Clean text and leaves the warm server of another launch running without starting one ({atLock.Result?.Notice}; {atLock.Seconds:0.0} s; running {keptRunning.Length})");

        // The next launch's engine cannot start, so starting it after the cancel would fail the dictation instead of cancelling it.
        var rewarmed = await Dictate(first);
        var rewarmedRunning = exits.Running;
        var unstartable = Polishing("unstartable", "answer");
        unstartable.LocalRewriteEnginePath = unstartable.LocalRewriteModelPath;
        using var escapeWhileStopping = new CancellationTokenSource();
        Task<DictationResult>? stopping = null;
        var cancelledWhileHeld = false;
        var whileStopping = await Timed(async () =>
        {
            stopping = Dictate(unstartable, escapeWhileStopping.Token);
            await WaitAsync(() => exits.HoldingExit, 10);
            cancelledWhileHeld = exits.HoldingExit;
            escapeWhileStopping.Cancel();
            return await stopping;
        });
        check(rewarmed.Notice is null && rewarmedRunning.SequenceEqual([first.LocalRewriteModelPath]) && cancelledWhileHeld && stopping is { IsCanceled: true }
            && whileStopping.LongestGap < ResponsiveBound && exits.Running.Length == 0,
            $"Escape while the warm server of another launch stops cancels the dictation without starting the next server, and the window stays responsive meanwhile ({(stopping is { IsCanceled: true } ? "cancelled" : stopping?.Exception?.InnerException?.Message)}; {whileStopping.Seconds:0.0} s, longest gap {whileStopping.LongestGap:0.00} s; running {exits.Running.Length})");
    }

    // Debugs every stand-in that reports its start and answers its exit report only after StandInExitSeconds: Windows ends a debugged
    // process only then, so the app's wait for the exit after Kill takes that long. Running names the model of each stand-in that has
    // not ended.
    private sealed class HeldServerExits : IDisposable
    {
        private const uint ExceptionEvent = 1, CreateProcessEvent = 3, ExitProcessEvent = 5, LoadDllEvent = 6;
        private const uint Continue = 0x00010002, ExceptionNotHandled = 0x80010001, Breakpoint = 0x80000003, Wow64Breakpoint = 0x4000001F;
        private readonly string servers;
        private readonly HashSet<string> seen = [];
        private readonly ConcurrentDictionary<uint, string> running = new();
        private readonly Thread debugger;
        private volatile bool disposed, holdingExit;
        public HeldServerExits(string servers)
        {
            this.servers = servers;
            debugger = new Thread(DebugStandIns) { IsBackground = true, Name = "Stand-in llama-server debugger" };
            debugger.Start();
        }
        public string[] Running => [.. running.Values.Order(StringComparer.Ordinal)];
        public bool HoldingExit => holdingExit;
        public void Dispose() { disposed = true; debugger.Join(); }
        // Debugging belongs to the thread that attached: it waits for the events and answers them. When it ends, the stand-ins are
        // detached and run on.
        private void DebugStandIns()
        {
            // DEBUG_EVENT on x64: the event code, process and thread at 0, 4 and 8, the event's own fields from 16.
            var debugEvent = new byte[176];
            while (!disposed)
            {
                foreach (var report in Directory.GetFiles(servers))
                    if (seen.Add(report) && uint.TryParse(Path.GetFileName(report), out var standIn) && DebugActiveProcess(standIn))
                    {
                        DebugSetProcessKillOnExit(false);
                        running[standIn] = File.ReadAllText(report);
                    }
                if (!WaitForDebugEvent(debugEvent, 50)) continue;
                var code = BitConverter.ToUInt32(debugEvent, 0);
                var processId = BitConverter.ToUInt32(debugEvent, 4);
                var threadId = BitConverter.ToUInt32(debugEvent, 8);
                var status = Continue;
                if (code == ExceptionEvent && BitConverter.ToUInt32(debugEvent, 16) is not (Breakpoint or Wow64Breakpoint)) status = ExceptionNotHandled;
                if (code is CreateProcessEvent or LoadDllEvent && BitConverter.ToInt64(debugEvent, 16) is var file and not 0) CloseHandle((nint)file);
                if (code == ExitProcessEvent)
                {
                    holdingExit = true;
                    Thread.Sleep(TimeSpan.FromSeconds(StandInExitSeconds));
                    running.TryRemove(processId, out _);
                    holdingExit = false;
                }
                ContinueDebugEvent(processId, threadId, status);
            }
        }
    }

    [DllImport("kernel32.dll")] private static extern bool IsDebuggerPresent();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcess(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugSetProcessKillOnExit(bool killOnExit);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WaitForDebugEvent(byte[] debugEvent, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ContinueDebugEvent(uint processId, uint threadId, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(nint handle);
}
