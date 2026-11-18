namespace PrivateFlow.Core;

// What arrives while a dictation or a meeting is starting: its pill shows, but its devices are not open and recording yet.
public enum StartingEvent { Press, HeldRepeat, Release, Escape, Cancel, Stop, Quit, WindowClosed, OpenFailed }

public enum StartingOutcome
{
    // The start goes on.
    Continue,
    // The start ends: no device opens (one already opening is closed at once), nothing is recorded, the pill goes.
    Cancel,
    // As Cancel, said as a hold-to-talk tap: "Released too soon".
    Discard,
    // The window stays open, as it does while a meeting records.
    RefuseClose,
    // The device did not open: the pill goes and the error is shown.
    Fail
}

// From the moment its pill shows, a start is stopped as a running recording is: whatever ends a recording ends the start, and it
// wins over the device open that follows.
public static class StartingCapture
{
    public static StartingOutcome Dictation(StartingEvent startingEvent) => startingEvent switch
    {
        // A held shortcut's repeated message, and the window closed to the tray, leave a running dictation alone too.
        StartingEvent.HeldRepeat or StartingEvent.WindowClosed => StartingOutcome.Continue,
        // In hold to talk the keys were released before anything was recorded: a tap.
        StartingEvent.Release => StartingOutcome.Discard,
        StartingEvent.OpenFailed => StartingOutcome.Fail,
        _ => StartingOutcome.Cancel,
    };
    public static StartingOutcome Meeting(StartingEvent startingEvent) => startingEvent switch
    {
        // Meetings have no hold to talk.
        StartingEvent.HeldRepeat or StartingEvent.Release => StartingOutcome.Continue,
        StartingEvent.WindowClosed => StartingOutcome.RefuseClose,
        StartingEvent.OpenFailed => StartingOutcome.Fail,
        _ => StartingOutcome.Cancel,
    };
}

// Numbers a feature's starts; the one whose pill shows while its devices do not record yet is starting. A meeting holds one; a dictation's
// starting state is DictationState.Starting, identified by its session.
public sealed class StartingGate
{
    private long starts;
    private long? starting;
    public bool IsStarting => starting is not null;
    public long Begin() => (starting = ++starts).Value;
    // Whatever ends a recording ends the start; so does its devices recording.
    public void End() => starting = null;
    // A device open that returns after its start ended, also when another start has begun meanwhile, is closed at once.
    public bool Keeps(long start) => starting == start;
}
