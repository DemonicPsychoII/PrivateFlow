using System.Globalization;
using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

// The progress of a setup script from the lines it prints, as a progress bar shows it: the step it last named, such as
// "Downloading Qwen3.5-4B-Q4_K_M.gguf", and the share its download percentages ("Local model download: 42%") have reached. The share
// never falls, and a step without a percentage keeps the last one, as the finishing of a meeting does.
public sealed partial class SetupProgress
{
    public string Step { get; private set; } = "Starting";
    public double Share { get; private set; }
    public string Text => $"{Step} · {(int)Math.Round(Share * 100)}%";
    public void Report(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return;
        if (PercentagePattern().Match(trimmed) is { Success: true } percentage)
            Share = Math.Max(Share, Math.Min(100, int.Parse(percentage.Groups[1].Value, CultureInfo.InvariantCulture)) / 100.0);
        else Step = trimmed.TrimEnd('.');
    }
    [GeneratedRegex(@"(\d{1,3})\s*%$", RegexOptions.CultureInvariant)]
    private static partial Regex PercentagePattern();
}
