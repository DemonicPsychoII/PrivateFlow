using PrivateFlow.Core;

static class Fixtures
{
    public static readonly FocusTarget SelectedTarget = new(10, 11, 12, true);
    public static readonly FocusTarget OtherTarget = new(20, 21, 22, true);
    public const int SlowGuestBoundMs = 10000;

    public static string Normalized(string text) => string.Join(" ", TranscriptScoring.Words(text));
}
