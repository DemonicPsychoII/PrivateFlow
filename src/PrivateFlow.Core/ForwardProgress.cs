namespace PrivateFlow.Core;

// One progress bar over several attempts of the same work, such as a retry or a fallback to another model: each attempt
// counts from 0 to 1 again, and the bar continues from where it stands instead of falling back to the start.
public sealed class ForwardProgress
{
    private double start, shown;
    public double Map(double share)
    {
        shown = Math.Max(shown, Math.Min(1, start + (1 - start) * Math.Clamp(share, 0, 1)));
        return shown;
    }
    public void Restart() => start = shown;
}
