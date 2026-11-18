namespace PrivateFlow.Core;

public static class TextDifference
{
    public static string Describe(string original, string final)
    {
        if (original == final) return "No changes.";
        var before = original.Split('\n'); var after = final.Split('\n');
        var common = 0;
        while (common < Math.Min(before.Length, after.Length) && before[common] == after[common]) common++;
        return string.Join("\n", before.Skip(common).Select(line => "− " + line).Concat(after.Skip(common).Select(line => "+ " + line)));
    }
}
