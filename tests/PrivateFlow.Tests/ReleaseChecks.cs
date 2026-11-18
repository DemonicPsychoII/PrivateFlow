using PrivateFlow.Core;
using static Harness;

static class ReleaseChecks
{
    public static void VerifyReleaseVersion()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout with src and docs was not found above " + AppContext.BaseDirectory);
        // Every pull request that changes the app raises the last number of the version and heads its release notes with it (the owner, 2026-11-17).
        var projectVersion = System.Xml.Linq.XDocument.Load(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows", "PrivateFlow.Windows.csproj"))
            .Descendants("Version").Single().Value;
        var notesVersion = File.ReadLines(Path.Combine(repository.FullName, "docs", "RELEASE_NOTES.md"))
            .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^# Private Flow v?(\d+\.\d+\.\d+)\b")).FirstOrDefault(match => match.Success)?.Groups[1].Value;
        Check(notesVersion == projectVersion, $"the topmost “Private Flow x.y.z” heading of docs/RELEASE_NOTES.md is the project version (notes {notesVersion}, project {projectVersion})");
        // The setup action names the size of the model the script downloads, which it reads from scripts/models.json.
        var polishScript = File.ReadAllText(Path.Combine(repository.FullName, "scripts", "setup-rewrite.ps1"));
        var polishSize = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(repository.FullName, "scripts", "models.json"))).RootElement.GetProperty("Models").EnumerateArray()
            .Single(model => model.GetProperty("Key").GetString() == "qwen3-4b").GetProperty("Bytes").GetInt64().ToString();
        Check(polishScript.Contains("Get-Pinned 'qwen3-4b'") && polishSize == PrivateFlow.Windows.LocalRewriter.PolishModelBytes.ToString() && PrivateFlow.Windows.LocalRewriter.PolishSetupLabel == "Set up Local polish (2,497 MB)",
            $"the Local polish setup action names the size of the model setup-rewrite.ps1 downloads ({polishSize} bytes; “{PrivateFlow.Windows.LocalRewriter.PolishSetupLabel}”)");
    }
}
