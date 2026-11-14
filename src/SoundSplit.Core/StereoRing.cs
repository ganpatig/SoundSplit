using System;
using System.Threading;

namespace SoundSplit.Core;

/// <summary>
/// The last few seconds of captured audio (stereo float), addressed by absolute frame number.
/// One thread writes; any number of threads read, each at its own position — that is what lets
/// every output run at a different delay.
/// </summary>
public sealed class StereoRing
{
    private readonly float[] _data;
    private readonly int _mask;
    private long _write;

    public StereoRing(int minFrames)
    {
        int cap = 1024;
        while (cap < minFrames) cap <<= 1;
        Capacity = cap;
        _mask = cap - 1;
        _data = new float[cap * 2];
    }

    public int Capacity { get; }

    /// <summary>Number of frames written so far; frames [WriteIndex - Capacity, WriteIndex) are readable.</summary>
    public long WriteIndex => Volatile.Read(ref _write);

    public void Write(ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        long w = _write;
        int done = 0;
        while (done < frames)
        {
            int at = (int)((w + done) & _mask);
            int n = Math.Min(frames - done, Capacity - at);
            stereo.Slice(done * 2, n * 2).CopyTo(_data.AsSpan(at * 2, n * 2));
            done += n;
        }
        Volatile.Write(ref _write, w + frames);
    }

    public void WriteSilence(long frames)
    {
        long w = _write;
        long clear = Math.Min(frames, Capacity);
        long start = w + frames - clear;
        long done = 0;
        while (done < clear)
        {
            int at = (int)((start + done) & _mask);
            int n = (int)Math.Min(clear - done, Capacity - at);
            _data.AsSpan(at * 2, n * 2).Clear();
            done += n;
        }
        Volatile.Write(ref _write, w + frames);
    }

    /// <summary>Copies frames [start, start + frames); anything not (or no longer) in the ring reads as silence.</summary>
    public void CopyTo(long start, Span<float> dst, int frames)
    {
        long w = Volatile.Read(ref _write);
        long oldest = w - Capacity;
        dst = dst.Slice(0, frames * 2);

        long from = Math.Max(start, oldest);
        long to = Math.Min(start + frames, w);
        if (to <= from)
        {
            dst.Clear();
            return;
        }

        int lead = (int)(from - start);
        if (lead > 0) dst.Slice(0, lead * 2).Clear();
        int tail = (int)(start + frames - to);
        if (tail > 0) dst.Slice((frames - tail) * 2).Clear();

        int count = (int)(to - from), done = 0;
        while (done < count)
        {
            int at = (int)((from + done) & _mask);
            int n = Math.Min(count - done, Capacity - at);
            _data.AsSpan(at * 2, n * 2).CopyTo(dst.Slice((lead + done) * 2, n * 2));
            done += n;
        }
    }
}
