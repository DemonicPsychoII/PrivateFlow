using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

// Runs the pure checks as named groups. A failed check is recorded and the group goes on; an exception ends only its own
// group. Every group must set up what it needs, because --filter runs any subset, each group alone in its own process.
static class Harness
{
    sealed record GroupResult(string Name, string Result, int Checks, List<string> Failures, long DurationMilliseconds);

    static readonly List<GroupResult> results = [];
    static readonly List<string> names = [];
    static (string Pattern, Regex Expression)[] filters = [];
    static bool listOnly;
    static string? resultPath;
    static int groupChecks;
    static List<string> groupFailures = [];

    public static void Configure(string[] arguments)
    {
        var patterns = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--filter" when index + 1 < arguments.Length:
                    patterns.AddRange(arguments[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--result" when index + 1 < arguments.Length:
                    resultPath = Path.GetFullPath(arguments[++index]);
                    break;
                case "--list":
                    listOnly = true;
                    break;
                default:
                    throw new ArgumentException("Usage: PrivateFlow.Tests [--filter <group>[,<group>...]] [--list] [--result <file.json>]; "
                        + "a group pattern may use * as a wildcard. Unknown argument: " + arguments[index]);
            }
        }
        filters = [.. patterns.Select(pattern => (pattern, new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$")))];
    }

    public static void Check(bool condition, string description)
    {
        groupChecks++;
        if (condition) { Console.WriteLine("PASS " + description); return; }
        groupFailures.Add(description);
        Console.WriteLine("FAIL " + description);
    }

    public static void Same(string actual, string expected, string description) => Check(actual == expected, $"{description} (got \"{actual}\")");

    public static void Reject(Action action, string description)
    {
        try { action(); Check(false, description); }
        catch (InvalidOperationException) { Check(true, description); }
    }

    public static void Group(string name, Action body) => Group(name, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    public static async Task Group(string name, Func<Task> body)
    {
        if (names.Contains(name)) throw new InvalidOperationException("Duplicate check group: " + name);
        names.Add(name);
        if (filters.Length > 0 && !filters.Any(filter => filter.Expression.IsMatch(name))) return;
        if (listOnly) { Console.WriteLine(name); return; }
        Console.WriteLine("GROUP " + name);
        groupChecks = 0;
        groupFailures = [];
        var timer = Stopwatch.StartNew();
        try { await body(); }
        catch (Exception exception)
        {
            groupFailures.Add($"the group stopped with {exception.GetType().Name}: {exception.Message}");
            Console.WriteLine($"FAIL the group stopped with {exception}");
        }
        if (groupChecks == 0 && groupFailures.Count == 0) groupFailures.Add("the group ran no checks");
        results.Add(new(name, groupFailures.Count == 0 ? "pass" : "fail", groupChecks, groupFailures, timer.ElapsedMilliseconds));
    }

    public static int Finish()
    {
        if (listOnly) return 0;
        var unmatched = filters.Where(filter => !names.Any(filter.Expression.IsMatch)).Select(filter => filter.Pattern).ToArray();
        var checks = results.Sum(result => result.Checks);
        var failed = results.Where(result => result.Result == "fail").ToArray();
        var passed = failed.Length == 0 && unmatched.Length == 0 && results.Count > 0;
        if (resultPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new
            {
                result = passed ? "pass" : "fail",
                checks,
                failures = failed.Sum(result => result.Failures.Count),
                unmatchedFilters = unmatched,
                groups = results.Select(result => new
                {
                    name = result.Name, result = result.Result, checks = result.Checks, failures = result.Failures,
                    durationMilliseconds = result.DurationMilliseconds,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var result in failed)
            foreach (var failure in result.Failures)
                Console.WriteLine($"FAILED [{result.Name}] {failure}");
        foreach (var filter in unmatched) Console.WriteLine("FAILED no check group matches " + filter);
        Console.WriteLine(passed ? $"{checks} safety and preference checks passed in {results.Count} groups."
            : failed.Length > 0 ? $"FAIL: {failed.Sum(result => result.Failures.Count)} failures in {failed.Length} of {results.Count} groups ({checks} checks)."
            : "FAIL: a filter matched no check group.");
        return passed ? 0 : 1;
    }
}
