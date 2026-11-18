using System.Windows;
using System.Windows.Media;
using PrivateFlow.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PrivateFlow.Windows;

// Both channels of a meeting around one centre line, with the dictation pill's rounded bars: the microphone grows upwards in white,
// the system audio downwards in its own colour. A channel that is not recorded or delivers nothing stays flat.
internal sealed class MirroredWaveform : AnimatedWave
{
    private const int Count = 18, Half = Count / 2;
    private LevelRipple microphone = new(Half), systemAudio = new(Half);
    private readonly double[] upward = new double[Count], downward = new double[Count];
    private Brush systemInk = Ink;
    public MirroredWaveform(Color systemColor) => SystemColor = systemColor;
    public Color SystemColor
    {
        get => ((SolidColorBrush)systemInk).Color;
        set { systemInk = Frozen(value); InvalidateVisual(); }
    }
    public void PushMicrophone(float rms) => microphone.Push(rms);
    public void PushSystemAudio(float rms) => systemAudio.Push(rms);
    // Both halves flat, without the last meeting's levels.
    public void Flatten()
    {
        microphone = new(Half); systemAudio = new(Half);
        Array.Clear(upward); Array.Clear(downward);
        InvalidateVisual();
    }
    // The tallest bar of each half, from 0 (flat) to 1; read by the workspace verification.
    internal double MicrophoneHeight => upward.Max();
    internal double SystemAudioHeight => downward.Max();
    public override void Advance(double seconds)
    {
        microphone.Advance(seconds); systemAudio.Advance(seconds);
        var animate = SystemParameters.ClientAreaAnimation;
        var moved = false;
        for (var i = 0; i < Count; i++)
        {
            // Distance from the middle in bars, as in the dictation pill; reduced motion keeps every bar on the current level.
            var distance = i < Half ? Half - 1 - i : i - Half;
            var taper = 1 - .4 * Math.Pow(distance / (Half - 1.0), 2);
            moved |= WaveBars.Approach(upward, i, microphone[animate ? distance : 0] * taper, seconds);
            moved |= WaveBars.Approach(downward, i, systemAudio[animate ? distance : 0] * taper, seconds);
        }
        // Both halves at rest, such as a silent meeting, need no redraw on every frame.
        if (moved) { InvalidateVisual(); Redraws++; }
    }
    // How often a change of the bars asked for a redraw; read by the workspace verification.
    internal int Redraws { get; private set; }
    protected override void OnRender(DrawingContext drawing)
    {
        var gap = (ActualWidth - Count * BarWidth) / (Count - 1);
        var centre = ActualHeight / 2;
        for (var i = 0; i < Count; i++)
        {
            var left = i * (BarWidth + gap);
            var up = Math.Max(BarWidth, upward[i] * centre * HeightShare);
            var down = Math.Max(BarWidth, downward[i] * centre * HeightShare);
            drawing.DrawRoundedRectangle(Ink, null, new Rect(left, centre - up, BarWidth, up), BarWidth / 2, BarWidth / 2);
            drawing.DrawRoundedRectangle(systemInk, null, new Rect(left, centre, BarWidth, down), BarWidth / 2, BarWidth / 2);
        }
    }
}

// The meeting pill: as minimal as the dictation pill, with both channels as a mirrored wave, for the whole capture of a meeting.
internal sealed class MeetingHud : PillWindow
{
    public MeetingHud(Color systemColor) : this(new MirroredWaveform(systemColor)) { }
    private MeetingHud(MirroredWaveform wave) : base(wave, "Private Flow meeting recording") => Wave = wave;
    public MirroredWaveform Wave { get; }
}
