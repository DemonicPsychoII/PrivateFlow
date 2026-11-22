using System.Text.RegularExpressions;
using static Harness;

// Every resource the app's windows look up exists in Theme.xaml, the dictionary the app and the verification host both load, or in the
// resources of the file that uses it. A resource left behind in App.xaml by a merge would otherwise fail only when the window opens,
// which nothing in the Linux CI does. Keys built at run time are covered where they follow the Icon… naming of the geometries.
static class ThemeChecks
{
    public static void VerifyThemeResources()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout with src was not found above " + AppContext.BaseDirectory);
        var app = Path.Combine(repository.FullName, "src", "PrivateFlow.Windows");
        static HashSet<string> Defined(string text) => Regex.Matches(text, @"x:Key=""([^""]+)""").Select(match => match.Groups[1].Value).ToHashSet();
        var theme = File.ReadAllText(Path.Combine(app, "Theme.xaml"));
        var themeKeys = Defined(theme);
        // Implicit styles: keyed by their type, without x:Key.
        var themeTypes = Regex.Matches(theme, @"<Style\s+TargetType=""(?:\{x:Type\s+)?(\w+)\}?""(?![^>]*x:Key)").Select(match => match.Groups[1].Value).ToHashSet();
        Check(themeKeys.Count > 50, $"Theme.xaml defines the app's resources ({themeKeys.Count} keys, {themeTypes.Count} implicit styles)");
        var appXaml = File.ReadAllText(Path.Combine(app, "App.xaml"));
        Check(Defined(appXaml).Count == 0 && appXaml.Contains(@"<ResourceDictionary Source=""Theme.xaml""/>"), "App.xaml defines no resource of its own and loads Theme.xaml");
        var missing = new List<string>();
        var used = 0;
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml"))
        {
            var text = File.ReadAllText(file);
            var local = Defined(text);
            foreach (Match match in Regex.Matches(text, @"\{(?:StaticResource|DynamicResource)\s+(\{x:Type\s+(\w+)\}|[^}\s]+)"))
            {
                used++;
                var known = match.Groups[2].Success ? themeTypes.Contains(match.Groups[2].Value) : themeKeys.Contains(match.Groups[1].Value) || local.Contains(match.Groups[1].Value);
                if (!known) missing.Add($"{Path.GetFileName(file)}: {match.Groups[1].Value}");
            }
        }
        foreach (var file in Directory.EnumerateFiles(app, "*.cs"))
        {
            var text = File.ReadAllText(file);
            var keys = Regex.Matches(text, @"(?:\bTheme(?:<[\w.]+>)?|\bFindResource|\bTryFindResource|\bIcon)\(""([^""]+)""").Select(match => match.Groups[1].Value)
                .Concat(Regex.Matches(text, @"""(Icon[A-Z]\w*)""").Select(match => match.Groups[1].Value));
            foreach (var key in keys) { used++; if (!themeKeys.Contains(key)) missing.Add($"{Path.GetFileName(file)}: {key}"); }
        }
        Check(used > 200 && missing.Count == 0, $"every resource the app's XAML and code look up by name is in Theme.xaml or its own file ({used} uses; missing: {string.Join(", ", missing.Distinct().DefaultIfEmpty("none"))})");
    }
}
