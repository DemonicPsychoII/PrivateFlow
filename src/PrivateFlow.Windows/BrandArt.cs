using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace PrivateFlow.Windows;

internal enum BrandState { Idle, Recording, Processing }

// The Private Flow mark: five white wave bars on a black rounded square, the same shape
// language as the recording pill. Drawn as vectors so tray, window and exe icons stay crisp
// at every size; assets/PrivateFlow.ico is exported from here with --export-icon.
internal static class BrandArt
{
    private static readonly double[] Bars = [.36, .7, 1, .7, .36];
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private static readonly Dictionary<(BrandState, int), System.Drawing.Icon> trayCache = new();

    public static BitmapSource Render(int size, BrandState state = BrandState.Idle, double phase = 0)
    {
        var visual = new DrawingVisual();
        using (var g = visual.RenderOpen())
        {
            var radius = size * .22;
            g.DrawRoundedRectangle(Brushes.Black, new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Math.Max(1, size / 32.0)),
                new Rect(.5, .5, size - 1, size - 1), radius, radius);
            // Whole-pixel bar geometry keeps the 16 px tray icon sharp. Whole pixels only center
            // when the free space is even: five equal bars of odd width sat 1 px left in the
            // 16 px icon, so the middle bar takes the spare pixel and heights match size parity.
            var width = Math.Max(1, Math.Round(size * .09));
            var gap = Math.Max(1, Math.Round(size * .07));
            var middle = width + (size - Bars.Length * width) % 2;
            var x = (size - (Bars.Length - 1) * (width + gap) - middle) / 2;
            var maxHeight = size * .58;
            for (var i = 0; i < Bars.Length; i++)
            {
                var level = state == BrandState.Processing ? .45 + .55 * (Math.Sin(i * .9 - phase) + 1) / 2 * Bars[i] : Bars[i];
                var height = Math.Max(width, Math.Round(maxHeight * level));
                height += (size - height) % 2;
                var barWidth = i == Bars.Length / 2 ? middle : width;
                g.DrawRoundedRectangle(Brushes.White, null, new Rect(x, (size - height) / 2, barWidth, height), barWidth / 2, barWidth / 2);
                x += barWidth + gap;
            }
            if (state == BrandState.Recording)
            {
                var dot = Math.Max(3, size * .2);
                g.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 69, 58)), new System.Windows.Media.Pen(Brushes.Black, Math.Max(1, size / 24.0)),
                    new System.Windows.Point(size - dot * .75, dot * .75), dot / 2, dot / 2);
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // Vista+ icons may hold PNG frames; one file covers every shell size.
    public static byte[] IconFile(BrandState state = BrandState.Idle, double phase = 0)
    {
        var frames = IconSizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(size, state, phase)));
            using var png = new MemoryStream();
            encoder.Save(png);
            return (size, data: png.ToArray());
        }).ToArray();
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((short)0); writer.Write((short)1); writer.Write((short)frames.Length);
        var offset = 6 + 16 * frames.Length;
        foreach (var (size, data) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((short)1); writer.Write((short)32);
            writer.Write(data.Length); writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames) writer.Write(data);
        writer.Flush();
        return output.ToArray();
    }

    public static System.Drawing.Icon TrayIcon(BrandState state, int frame = 0)
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        if (trayCache.TryGetValue((state, frame), out var cached)) return cached;
        var icon = new System.Drawing.Icon(new MemoryStream(IconFile(state, frame * .7)), size, size);
        trayCache[(state, frame)] = icon;
        return icon;
    }
}
