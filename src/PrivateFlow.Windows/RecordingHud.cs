using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using PrivateFlow.Core;
using Color = System.Windows.Media.Color;

namespace PrivateFlow.Windows;

// Minimal black pill with a white outline and a waveform; no text. Stays nonactivating and click-through so the selected field
// keeps keyboard focus.
internal abstract class PillWindow : Window
{
    private readonly AnimatedWave wave;
    private readonly System.Windows.Controls.Border outline;
    private readonly string name;
    private bool starting;
    protected PillWindow(AnimatedWave wave, string name)
    {
        this.wave = wave; this.name = name;
        Width = 176; Height = 64;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        Content = outline = new System.Windows.Controls.Border
        {
            Margin = new Thickness(12), Padding = new Thickness(18, 0, 18, 0),
            Background = System.Windows.Media.Brushes.Black,
            BorderBrush = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(20), Child = wave
        };
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Bottom - Height - 20;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, (nint)(style | 0x08000000 | 0x00000080 | 0x00000020));
        };
        // No fade-in: it would run on the interface thread, which opening the microphone holds right after the pill shows.
        Loaded += (_, _) => wave.Start();
        Closed += (_, _) => wave.Stop();
        AutomationProperties.SetName(this, name);
    }
    // While the devices open the press is registered, but nothing records yet: a grey outline and faint bars, unlike a silent recording,
    // which has the white outline and white resting bars.
    private static readonly System.Windows.Media.Brush StartingOutline = AnimatedWave.Frozen(Color.FromRgb(0x5c, 0x5c, 0x5c));
    public bool Starting
    {
        get => starting;
        set
        {
            starting = value;
            outline.BorderBrush = value ? StartingOutline : System.Windows.Media.Brushes.White; wave.Opacity = value ? .3 : 1;
            AutomationProperties.SetName(this, value ? "Private Flow starting" : name);
        }
    }
    // Shown once off every screen and closed after its first frame, so a press does not wait for WPF to prepare the first pill.
    internal static void WarmUp(PillWindow pill)
    {
        pill.Left = pill.Top = -32000;
        pill.ContentRendered += (_, _) => pill.Close();
        pill.Show();
    }
    // Advance the animation by a fixed amount; used by the synthetic overlay diagnostic.
    internal void Step(double seconds) => wave.Advance(seconds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}

// A waveform that moves with the screen's frames while started.
internal abstract class AnimatedWave : FrameworkElement
{
    // One bar shape for the dictation and the meeting wave: its width, and the share of the height a full level reaches.
    protected const double BarWidth = 2.4, HeightShare = .7;
    protected static readonly System.Windows.Media.Brush Ink = Frozen(Color.FromRgb(255, 255, 255));
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last;
    private bool started;
    public bool Running => started;
    public void Start()
    {
        if (started) return;
        started = true; last = clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += Tick;
    }
    public void Stop()
    {
        if (!started) return;
        started = false;
        CompositionTarget.Rendering -= Tick;
    }
    private void Tick(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        Advance(Math.Min(now - last, .1));
        last = now;
    }
    public abstract void Advance(double seconds);
    internal static System.Windows.Media.Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
}

// The dictation pill. Its shape says "listening" (bars show the voice's loudness, newest in the middle) or "transcribing" (a calm
// travelling wave).
internal sealed class RecordingHud : PillWindow
{
    private readonly WaveformView waveform;
    public RecordingHud() : this(new WaveformView()) { }
    private RecordingHud(WaveformView waveform) : base(waveform, "Private Flow recording") => this.waveform = waveform;
    public void PushLevel(float rms) => waveform.Push(rms);
    public void SetTranscribing()
    {
        waveform.Transcribing = true;
        AutomationProperties.SetName(this, "Private Flow transcribing");
    }

    private sealed class WaveformView : AnimatedWave
    {
        private const int Count = 18, Half = Count / 2;
        // The pill shows one level per step, so the newest sits in the middle and the last ~225 ms of speech ripple out to both ends.
        private readonly LevelRipple ripple = new(Half);
        private readonly double[] heights = new double[Count];
        private double time;
        public bool Transcribing { get; set; }
        public void Push(float rms) => ripple.Push(rms);
        public override void Advance(double dt)
        {
            time += dt;
            ripple.Advance(dt);
            var animate = SystemParameters.ClientAreaAnimation;
            for (var i = 0; i < Count; i++)
            {
                var x = i / (Count - 1.0);
                // Distance from the middle in bars: 0 for the two centre bars, Half - 1 at the ends.
                var distance = i < Half ? Half - 1 - i : i - Half;
                double goal;
                if (Transcribing)
                {
                    // Bell envelope: tallest in the middle, tapering to the rounded ends of the pill.
                    var envelope = Math.Exp(-Math.Pow((x - .5) / .28, 2));
                    goal = animate ? .1 + .55 * envelope * (Math.Sin(x * 9 - time * 4.2) + 1) / 2 : .1 + .33 * envelope;
                }
                else
                {
                    // Reduced motion: all bars follow the current level in place instead of rippling.
                    var level = ripple[animate ? distance : 0];
                    var taper = 1 - .4 * Math.Pow(distance / (Half - 1.0), 2);
                    goal = .08 + .92 * level * taper;
                }
                // One short smoothing step: rise within ~15 ms, fall within ~80 ms, so pauses
                // between words show as dips instead of a slow swell.
                var rate = Transcribing ? 14 : goal > heights[i] ? 65 : 12;
                heights[i] += (goal - heights[i]) * (1 - Math.Exp(-dt * rate));
            }
            InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            var gap = (ActualWidth - Count * BarWidth) / (Count - 1);
            for (var i = 0; i < Count; i++)
            {
                var height = Math.Max(BarWidth, heights[i] * ActualHeight * HeightShare);
                drawing.DrawRoundedRectangle(Ink, null,
                    new Rect(i * (BarWidth + gap), (ActualHeight - height) / 2, BarWidth, height), BarWidth / 2, BarWidth / 2);
            }
        }
    }
}
