using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

// One recording of a test audio library: what was said (critical items in square brackets, as in every reference of the suite),
// its language, tags such as noisy, quiet, code-switching, technical or long, and optionally the text one output style should give.
public sealed record LibraryClip(string Id, string AudioPath, string Language, string Reference, IReadOnlyList<string> CriticalItems,
    IReadOnlyList<string> Tags, OutputStyle? ExpectedStyle, string? ExpectedText, string Source);

// A folder of clips for the test build: one folder per clip with its audio and a manifest.json. The library lives outside the
// repository, so it never puts audio or a user's dictations into it.
public static partial class AudioLibrary
{
    public const string ManifestName = "manifest.json";
    public const int Schema = 1;

    private sealed record Manifest(int Schema, string? Audio, string? Language, string? Reference, string[]? Tags, string? ExpectedStyle, string? Expected, string? Source);

    // Every clip in the folder, ordered by its folder name so the same seed draws the same clips on every PC.
    public static IReadOnlyList<LibraryClip> Load(string folder)
    {
        if (!Directory.Exists(folder)) throw new InvalidDataException($"The audio library {folder} does not exist.");
        var clips = Directory.GetDirectories(folder).Where(clip => File.Exists(Path.Combine(clip, ManifestName)))
            .OrderBy(clip => Path.GetFileName(clip), StringComparer.Ordinal).Select(Read).ToArray();
        return clips.Length > 0 ? clips : throw new InvalidDataException($"The audio library {folder} holds no clip with a {ManifestName}.");
    }

    public static LibraryClip Read(string clipFolder)
    {
        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(clipFolder));
        Manifest manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(clipFolder, ManifestName)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException("empty"); }
        catch (JsonException error) { throw new InvalidDataException($"Clip {id}: {ManifestName} is not valid JSON ({error.Message})."); }
        if (manifest.Schema != Schema) throw new InvalidDataException($"Clip {id}: schema {manifest.Schema} is not {Schema}.");
        if (!SpeechLanguages.IsSpoken(manifest.Language)) throw new InvalidDataException($"Clip {id}: language must be en, de or es.");
        if (manifest.Reference is null) throw new InvalidDataException($"Clip {id}: reference is missing; use \"\" for a clip without speech.");
        // The audio stays inside the clip's folder: a manifest cannot point the test build at other files.
        if (string.IsNullOrWhiteSpace(manifest.Audio) || Path.IsPathRooted(manifest.Audio) || Path.GetFileName(manifest.Audio) != manifest.Audio)
            throw new InvalidDataException($"Clip {id}: audio must be a file name in the clip's folder.");
        var audio = Path.Combine(clipFolder, manifest.Audio);
        if (!File.Exists(audio)) throw new InvalidDataException($"Clip {id}: {manifest.Audio} is missing.");
        var tags = manifest.Tags ?? [];
        if (tags.Any(tag => !TagPattern().IsMatch(tag))) throw new InvalidDataException($"Clip {id}: tags are lower-case words joined by hyphens.");
        OutputStyle? style = null;
        if (manifest.ExpectedStyle is not null)
            style = Enum.TryParse<OutputStyle>(manifest.ExpectedStyle, true, out var parsed) && Enum.IsDefined(parsed) ? parsed
                : throw new InvalidDataException($"Clip {id}: expectedStyle must be Exact, Clean, Concise or LocalPolish.");
        if (manifest.Expected is not null && style is null) throw new InvalidDataException($"Clip {id}: expected needs an expectedStyle.");
        return new(id, audio, manifest.Language!, manifest.Reference, CriticalItems(manifest.Reference), tags, style, manifest.Expected, manifest.Source ?? "");
    }

    public static IReadOnlyList<string> CriticalItems(string reference) => [.. CriticalPattern().Matches(reference).Select(match => match.Groups[1].Value)];

    // The manifest of a new clip. expected is the reference for expectedStyle when that style should change the words.
    public static string ManifestJson(string audio, string language, string reference, IEnumerable<string> tags, string source, OutputStyle? expectedStyle = null, string? expected = null) =>
        JsonSerializer.Serialize(new Manifest(Schema, audio, language, reference, [.. tags], expectedStyle?.ToString(), expected, source),
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    [GeneratedRegex(@"\[([^\[\]]+)\]")] private static partial Regex CriticalPattern();
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")] private static partial Regex TagPattern();
}

// Draws the clips of a test run. The same seed and the same library give the same clips in the same order, so a failure replays.
public sealed class ClipPicker(IReadOnlyList<LibraryClip> clips, int seed)
{
    private readonly Random random = new(seed);
    public int Seed { get; } = seed;
    public int Drawn { get; private set; }

    // A clip with the tag, or any clip without one; the draw counts either way, so later draws stay where they were.
    public LibraryClip Next(string? tag = null)
    {
        var candidates = tag is null ? clips : clips.Where(clip => clip.Tags.Contains(tag)).ToArray();
        var index = random.Next(int.MaxValue);
        Drawn++;
        return candidates.Count > 0 ? candidates[index % candidates.Count] : throw new InvalidOperationException($"No clip of the library has the tag {tag}.");
    }
}

// Harder versions of a clip for the library: quieter, under room noise, or two languages one after the other.
public static class LibraryVariants
{
    public static float[] Scaled(float[] samples, double decibels)
    {
        var gain = (float)Math.Pow(10, decibels / 20);
        return [.. samples.Select(sample => sample * gain)];
    }

    // White noise at the given RMS level in dBFS, the same for the same seed.
    public static float[] WithNoise(float[] samples, double noiseDecibels, int seed)
    {
        var random = new Random(seed);
        var amplitude = (float)(Math.Pow(10, noiseDecibels / 20) * Math.Sqrt(3));
        return [.. samples.Select(sample => Math.Clamp(sample + (float)(random.NextDouble() * 2 - 1) * amplitude, -1f, 1f))];
    }

    public static float[] Joined(float[] first, float[] second, double gapSeconds) =>
        [.. first, .. new float[(int)(gapSeconds * SpeechSegmentation.SampleRate)], .. second];
}
