using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Windows;
using Application = System.Windows.Application;

namespace PrivateFlow.Verification;

// --check-package <package folder> <report>: the release package carries no verification code. It holds no file of this host, none of
// its assemblies defines a type in this host's namespace or with the name of one of this host's types, and its PrivateFlow.exe ends
// every mode that moved here with exit code 2, given the arguments and the isolated data folder with which the app once ran them.
internal static class PackageCheck
{
    // The modes that moved here from the app, with the number of arguments the app took for each.
    internal static readonly Dictionary<string, int> MovedModes = new()
    {
        ["--verify-workspace"] = 1, ["--diagnostic-ui"] = 0, ["--verify-overlay"] = 1, ["--verify-insert"] = 1, ["--insert-fixture"] = 1,
        ["--evaluate-meeting"] = 2, ["--evaluate-dictation"] = 2, ["--evaluate-recordings"] = 2, ["--write-notes"] = 2, ["--build-audio-library"] = 2, ["--verify-imports"] = 2, ["--verify-worker-faults"] = 1,
    };

    public static async Task RunAsync(Application host, string package, string report)
    {
        var lines = new List<string>();
        var passed = true;
        void Check(bool condition, string description) { passed &= condition; lines.Add((condition ? "PASS " : "FAIL ") + description); }
        try
        {
            package = Path.GetFullPath(package);
            var hostFiles = Directory.EnumerateFiles(package, "PrivateFlow.Verification*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(package, file)).ToArray();
            Check(hostFiles.Length == 0, $"the package holds no file of the verification host ({(hostFiles.Length == 0 ? "none" : string.Join(", ", hostFiles))})");
            var hostNamespace = typeof(PackageCheck).Namespace!;
            var hostTypes = typeof(PackageCheck).Assembly.GetTypes().Where(type => type.DeclaringType is null && type.Namespace == hostNamespace && !type.Name.StartsWith('<')).Select(type => type.Name).ToHashSet();
            // The host checks only the build it carries: the package's app assembly must be its own, byte for byte.
            var packageAssembly = Path.Combine(package, "PrivateFlow.dll");
            var hostAssembly = Path.Combine(AppContext.BaseDirectory, "PrivateFlow.dll");
            var sameBuild = File.Exists(packageAssembly) && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(packageAssembly)))
                == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hostAssembly)));
            Check(sameBuild, $"the package's PrivateFlow.dll is byte for byte the one this host runs ({Program.Identity(typeof(PrivateFlow.Windows.App).Assembly)})");
            // A development build names its commit; a release build (Directory.Build.props) names none, and a build from a tree
            // without Git history names nothing after the version.
            var identity = Program.Identity(typeof(PrivateFlow.Windows.App).Assembly);
            Check(System.Text.RegularExpressions.Regex.IsMatch(identity, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:\+(?:[0-9a-f]{40}|release))?\z"),
                $"the build identity is the version with a development commit, \"+release\" or nothing ({identity})");
            var assemblies = Directory.GetFiles(package, "PrivateFlow*.dll");
            Check(assemblies.Any(file => Path.GetFileName(file) == "PrivateFlow.dll"), $"the package holds the app's assembly ({assemblies.Length} PrivateFlow assemblies)");
            foreach (var assembly in assemblies)
            {
                var found = TopLevelTypes(assembly).Where(type => type.Namespace.StartsWith(hostNamespace, StringComparison.Ordinal) || hostTypes.Contains(type.Name))
                    .Select(type => $"{type.Namespace}.{type.Name}").ToArray();
                Check(found.Length == 0, $"{Path.GetFileName(assembly)} defines no type of the verification host ({(found.Length == 0 ? $"{hostTypes.Count} type names compared" : string.Join(", ", found))})");
            }
            var data = Path.Combine(Path.GetTempPath(), "PrivateFlow-package-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(data);
            try
            {
                foreach (var (mode, argumentCount) in MovedModes)
                {
                    var start = new ProcessStartInfo(Path.Combine(package, "PrivateFlow.exe")) { UseShellExecute = false, CreateNoWindow = true };
                    start.ArgumentList.Add(mode);
                    for (var index = 0; index < argumentCount; index++) start.ArgumentList.Add(Path.Combine(data, $"{mode.TrimStart('-')}-{index}"));
                    start.Environment["PRIVATE_FLOW_DATA_DIR"] = data;
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("PrivateFlow.exe did not start.");
                    var ended = process.WaitForExitAsync();
                    if (await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(30))) != ended) { process.Kill(true); await ended; Check(false, $"PrivateFlow.exe {mode} still ran after 30 s"); continue; }
                    Check(process.ExitCode == 2, $"PrivateFlow.exe {mode} ends with exit code 2 and runs nothing (exit code {process.ExitCode})");
                }
            }
            finally { try { Directory.Delete(data, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
        catch (Exception ex) { Check(false, $"the check stopped: {ex.GetType().Name}: {ex.Message}"); }
        await File.WriteAllLinesAsync(report, lines);
        host.Shutdown(passed ? 0 : 1);
    }

    private static List<(string Namespace, string Name)> TopLevelTypes(string assembly)
    {
        using var stream = File.OpenRead(assembly);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        var types = new List<(string, string)>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (type.GetDeclaringType().IsNil) types.Add((metadata.GetString(type.Namespace), metadata.GetString(type.Name)));
        }
        return types;
    }
}
