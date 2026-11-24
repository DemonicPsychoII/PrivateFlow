using NAudio.Wave;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// A channel's chunk files read as one recording; gaps between chunks, such as loopback silence, read as zeros.
internal sealed class ChannelAudio
{
    private readonly (string File, int Start, int Length)[] chunks;
    public int Length { get; }
    public double Seconds => Length / (double)SpeechSegmentation.SampleRate;
    // 16-bit mono 16 kHz WAV files with their place in the recording, in seconds.
    public ChannelAudio(IEnumerable<(string File, double Offset, double Duration)> files)
    {
        chunks = files.OrderBy(file => file.Offset).Select(file => (file.File, (int)Math.Round(file.Offset * SpeechSegmentation.SampleRate),
            (int)Math.Round(file.Duration * SpeechSegmentation.SampleRate))).ToArray();
        Length = chunks.Length == 0 ? 0 : chunks.Max(chunk => chunk.Start + chunk.Length);
    }
    public float[] Read(double start, double end) =>
        Read(SpeechSegmentation.Index(start, Length), Math.Max(0, SpeechSegmentation.Index(end, Length) - SpeechSegmentation.Index(start, Length)));
    public float[] Read(int start, int count)
    {
        var samples = new float[count];
        foreach (var (file, chunkStart, chunkLength) in chunks.Where(chunk => chunk.Start < start + count && chunk.Start + chunk.Length > start))
        {
            using var reader = new WaveFileReader(file);
            var skip = Math.Max(0, start - chunkStart);
            reader.Position = (long)skip * reader.WaveFormat.BlockAlign;
            var provider = reader.ToSampleProvider();
            // Straight into the result at the chunk's place, without a buffer per chunk.
            var offset = Math.Max(0, chunkStart - start); var end = offset + Math.Min(count - offset, chunkLength - skip); int got;
            while (offset < end && (got = provider.Read(samples, offset, end - offset)) > 0) offset += got;
        }
        return samples;
    }
}
