using System.IO;
using System.Media;

namespace Counter.App.Services;

/// <summary>
/// Plays a short two-note completion chime. The waveform is synthesised once at startup so the
/// app ships no audio assets and never touches the network.
/// </summary>
public sealed class ChimePlayer : IDisposable
{
    private const int SampleRate = 44100;

    private readonly byte[] _wav;
    private SoundPlayer? _player;
    private bool _disposed;

    public ChimePlayer() => _wav = BuildChime();

    public void Play()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _player ??= new SoundPlayer(new MemoryStream(_wav, writable: false));
            _player.Stream!.Position = 0;
            _player.Play();
        }
        catch (Exception ex)
        {
            // Audio devices come and go. A missing chime must never interrupt a focus session.
            Log.Warn("Could not play the completion chime.", ex);
        }
    }

    private static byte[] BuildChime()
    {
        // Two soft sine notes (A5 then E6) with an exponential decay, about 620 ms total.
        var notes = new[] { (Frequency: 880.0, Start: 0.00, Length: 0.34), (Frequency: 1318.5, Start: 0.20, Length: 0.42) };
        var totalSeconds = 0.62;
        var sampleCount = (int)(SampleRate * totalSeconds);
        var samples = new float[sampleCount];

        foreach (var note in notes)
        {
            var startSample = (int)(note.Start * SampleRate);
            var noteSamples = (int)(note.Length * SampleRate);

            for (var i = 0; i < noteSamples && startSample + i < sampleCount; i++)
            {
                var t = i / (double)SampleRate;
                var envelope = Math.Exp(-5.5 * t / note.Length) * (1 - Math.Exp(-260 * t));
                samples[startSample + i] += (float)(Math.Sin(2 * Math.PI * note.Frequency * t) * envelope * 0.28);
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        var dataBytes = sampleCount * 2;

        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + dataBytes);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);                       // PCM chunk size
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);           // byte rate
        writer.Write((short)2);                 // block align
        writer.Write((short)16);                // bits per sample
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            var clipped = Math.Clamp(sample, -1f, 1f);
            writer.Write((short)(clipped * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player?.Dispose();
        _player = null;
    }
}
