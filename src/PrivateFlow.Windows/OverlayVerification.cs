using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PrivateFlow.Windows;

// An explicit diagnostic mode: synthetic levels, a disposable focus fixture, no audio or input dispatch.
internal static class OverlayVerification
{
    public static async Task RunAsync(string output)
    {
        Window? fixture = null;
        RecordingHud? hud = null;
        MeetingHud? meeting = null;
        try
        {
            Directory.CreateDirectory(output);
            fixture = new Window { Title = "Private Flow isolated overlay fixture", Width = 420, Height = 160,
                Content = new System.Windows.Controls.TextBox { Text = "Synthetic overlay verification — no microphone", Margin = new Thickness(20) } };
            fixture.Show(); fixture.Activate();
            await Task.Delay(150);
            var focus = GetForegroundWindow();
            if (focus != new WindowInteropHelper(fixture).Handle) throw new InvalidOperationException("Fixture did not acquire focus.");
            // Starting: shown at the press, before the microphone records.
            hud = new RecordingHud { Starting = true }; hud.Show();
            await Task.Delay(250);
            if (GetForegroundWindow() != focus) throw new InvalidOperationException("Starting overlay stole focus.");
            var starting = Capture(hud, Path.Combine(output, "starting.png"));
            hud.Starting = false;
            await Task.Delay(150);
            // Recording, silent so far: it must not look like starting.
            var silent = Capture(hud, Path.Combine(output, "recording-silent.png"));
            if (Outline(starting) >= Outline(silent) / 2) throw new InvalidOperationException("Starting looks like a silent recording.");
            // Synthetic syllables: ~-25 dBFS bursts separated by near-silence, one level per 25 ms.
            for (var i = 0; i < 30; i++) { hud.PushLevel((float)(.002 + .06 * Math.Max(0, Math.Sin(i * .9)))); hud.Step(.025); }
            await Task.Delay(250);
            if (GetForegroundWindow() != focus) throw new InvalidOperationException("Recording overlay stole focus.");
            var recording = Capture(hud, Path.Combine(output, "recording.png"));
            hud.SetTranscribing();
            for (var i = 0; i < 30; i++) hud.Step(1 / 60.0);
            await Task.Delay(120);
            var first = Capture(hud, Path.Combine(output, "transcribing.png"));
            await Task.Delay(250);
            var second = Capture(hud, Path.Combine(output, "transcribing-next.png"));
            if (starting.SequenceEqual(recording)) throw new InvalidOperationException("Starting and recording states look identical.");
            if (recording.SequenceEqual(first)) throw new InvalidOperationException("Recording and processing states look identical.");
            if (SystemParameters.ClientAreaAnimation && first.SequenceEqual(second)) throw new InvalidOperationException("Processing waveform did not animate.");
            if (GetForegroundWindow() != focus) throw new InvalidOperationException("Processing overlay stole focus.");
            hud.Close(); hud = null;
            // The meeting pill: the microphone grows upwards in white, the system audio downwards in its own colour.
            var green = (Color)ColorConverter.ConvertFromString("#3FB950");
            meeting = new MeetingHud(green); meeting.Show();
            await Task.Delay(200);
            var flat = Bars(meeting, green);
            for (var i = 0; i < 30; i++) { meeting.Wave.PushMicrophone((float)(.002 + .06 * Math.Max(0, Math.Sin(i * .9)))); meeting.Step(.025); }
            var microphoneOnly = Bars(meeting, green);
            Capture(meeting, Path.Combine(output, "meeting-microphone.png"));
            for (var i = 0; i < 30; i++) { meeting.Wave.PushSystemAudio((float)(.002 + .06 * Math.Max(0, Math.Sin(i * .9)))); meeting.Step(.025); }
            var systemOnly = Bars(meeting, green);
            Capture(meeting, Path.Combine(output, "meeting-system-audio.png"));
            if (flat.Upper > 0 || flat.Lower > 0) throw new InvalidOperationException($"A meeting pill without levels is not flat ({flat}).");
            if (microphoneOnly.Upper == 0 || microphoneOnly.Lower > 0) throw new InvalidOperationException($"Microphone levels did not raise only the upper half ({microphoneOnly}).");
            if (systemOnly.Lower == 0 || systemOnly.Upper > 0) throw new InvalidOperationException($"System audio levels did not raise only the lower half in its colour ({systemOnly}).");
            meeting.Wave.SystemColor = Colors.White;
            for (var i = 0; i < 4; i++) { meeting.Wave.PushSystemAudio(.06f); meeting.Step(.025); }
            if (Bars(meeting, green).Lower > 0) throw new InvalidOperationException("A new system audio colour did not apply at once.");
            if (GetForegroundWindow() != focus) throw new InvalidOperationException("Meeting overlay stole focus.");
            File.WriteAllText(Path.Combine(output, "result.txt"), "PASS synthetic starting (unlike a silent recording), recording, processing animation, meeting wave halves per channel, colour change and retained fixture focus.\nGenerated by Private Flow overlay diagnostic.\n");
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(output, "result.txt"), "FAIL " + ex.Message); }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            finally { System.Windows.Application.Current.Shutdown(1); }
        }
        finally { hud?.Close(); meeting?.Close(); fixture?.Close(); }
    }
    private static byte[] Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var bytes = stream.ToArray(); File.WriteAllBytes(path, bytes); return bytes;
    }
    // Bright pixels, such as the white outline and resting bars of a recording pill.
    private static int Outline(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        var bright = 0;
        for (var index = 0; index < pixels.Length; index += 4) if (pixels[index] > 200 && pixels[index + 1] > 200 && pixels[index + 2] > 200 && pixels[index + 3] > 200) bright++;
        return bright;
    }
    // Pixels of tall bars in each half of the meeting wave: white above the centre line, the system colour below it. The flat
    // rest marks next to the centre line are left out.
    private static (int Upper, int Lower) Bars(MeetingHud pill, Color system)
    {
        pill.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)pill.ActualWidth, (int)pill.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(pill);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var bounds = pill.Wave.TransformToAncestor(pill).TransformBounds(new Rect(pill.Wave.RenderSize));
        var centre = bounds.Top + bounds.Height / 2;
        bool Near(byte value, byte target) => Math.Abs(value - target) < 40;
        int upper = 0, lower = 0;
        for (var y = (int)bounds.Top; y < (int)bounds.Bottom; y++)
            for (var x = (int)bounds.Left; x < (int)bounds.Right; x++)
            {
                var index = (y * bitmap.PixelWidth + x) * 4;
                var (blue, green, red) = (pixels[index], pixels[index + 1], pixels[index + 2]);
                if (y < centre - 4 && red > 200 && green > 200 && blue > 200) upper++;
                if (y > centre + 4 && Near(red, system.R) && Near(green, system.G) && Near(blue, system.B)) lower++;
            }
        return (upper, lower);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
