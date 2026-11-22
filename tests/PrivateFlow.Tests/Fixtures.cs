using PrivateFlow.Core;

static class Fixtures
{
    public static readonly FocusTarget SelectedTarget = new(10, 11, 12, false, PasswordField.No);
    public static readonly FocusTarget OtherTarget = new(20, 21, 22, false, PasswordField.No);
    public const int SlowGuestBoundMs = 10000;

    public static string Normalized(string text) => string.Join(" ", TranscriptScoring.Words(text));
}

// A clock the checks move by hand: timers created on it fire only when Advance passes their due time.
sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() { lock (timers) return now; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(TimeSpan time)
    {
        List<ManualTimer> due;
        lock (timers) { now += time; due = [.. timers.Where(timer => timer.Due is { } at && at <= now)]; foreach (var timer in due) timer.Due = null; }
        foreach (var timer in due) timer.Fire();
    }
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.timers)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                if (!clock.timers.Contains(this)) clock.timers.Add(this);
            }
            return true;
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (clock.timers) clock.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
