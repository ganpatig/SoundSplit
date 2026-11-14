using System;
using System.Threading;

namespace SoundSplit.Core;

/// <summary>
/// Feeds one output device from the shared capture ring so that every captured frame comes out
/// of that device exactly <see cref="DelaySeconds"/> after it was captured — and keeps it there.
///
/// Two devices never run at quite the same speed (their crystals differ by tens of parts per
/// million), so a fixed delay line drifts by milliseconds per minute. Instead, on every block
/// this works out which source frame *should* be next from the two device clocks, compares it
/// with the frame it is actually about to read, and nudges the resampling rate by a fraction of
/// a percent to close the gap.
/// </summary>
public sealed class SyncedReader
{
    private readonly StereoRing _ring;
    private readonly Timeline _source;
    private readonly SincResampler _resampler;
    private readonly double _sourceRate;
    private readonly double _sinkRate;
    private readonly double _nominalStep;
    private float[] _window = new float[4096];

    private double _position;
    private bool _started;
    private bool _wasLocked;
    private double _integral;
    private double _lastNow = double.NaN;
    private double _delay;
    private long _reseeks;
    private long _starved;
    private double _errorSeconds;

    /// <summary>Proportional gain, 1/s: an error decays with a time constant of 1/Kp seconds.</summary>
    public double Kp { get; init; } = 2.0;

    /// <summary>Integral gain; absorbs the constant clock-speed difference between the two devices.</summary>
    public double Ki { get; init; } = 1.0;

    /// <summary>Largest speed change used to correct an error (1% is about a sixth of a semitone, and only briefly).</summary>
    public double MaxAdjust { get; init; } = 0.01;

    /// <summary>Errors larger than this are fixed by jumping rather than by gliding.</summary>
    public double ReseekSeconds { get; init; } = 0.025;

    public SyncedReader(StereoRing ring, Timeline source, double sourceRate, double sinkRate)
    {
        _ring = ring;
        _source = source;
        _sourceRate = sourceRate;
        _sinkRate = sinkRate;
        _nominalStep = sourceRate / sinkRate;
        _resampler = new SincResampler(_nominalStep);
        SinkClock = new Timeline(sinkRate);
    }

    /// <summary>Frames played by the output device against time, as reported by the device itself.</summary>
    public Timeline SinkClock { get; }

    /// <summary>How long after capture each frame should reach the output device's play position.</summary>
    public double DelaySeconds
    {
        get => Volatile.Read(ref _delay);
        set => Volatile.Write(ref _delay, value);
    }

    /// <summary>Output frames produced so far.</summary>
    public long FramesWritten { get; private set; }

    /// <summary>Signed timing error at the last block: positive means this output is early.</summary>
    public double ErrorSeconds => Volatile.Read(ref _errorSeconds);

    /// <summary>Number of times the read position had to jump (start-up, delay changes, glitches).</summary>
    public long Reseeks => Interlocked.Read(ref _reseeks);

    /// <summary>Blocks for which the capture side had not delivered enough audio yet.</summary>
    public long Starved => Interlocked.Read(ref _starved);

    /// <summary>Current speed correction relative to nominal, e.g. 0.0001 = 100 ppm fast.</summary>
    public double Adjust { get; private set; }

    /// <summary>Records where the output device's play position was at a given time.</summary>
    public void ReportPosition(double time, double playedFrames) => SinkClock.Add(time, playedFrames);

    /// <summary>Forget the device clock, e.g. after the device ran dry and its position stalled.</summary>
    public void ResetClock() => SinkClock.Reset();

    /// <summary>
    /// Produces up to <paramref name="maxFrames"/> stereo frames to hand to the device now.
    /// </summary>
    /// <param name="now">Current time, same clock as the timelines.</param>
    /// <param name="queuedFrames">Frames already handed to the device and not yet played.</param>
    /// <returns>Frames written to <paramref name="dst"/>; fewer than asked when capture has not caught up.</returns>
    public int Read(Span<float> dst, int maxFrames, double now, int queuedFrames)
    {
        if (maxFrames <= 0) return 0;
        double dt = double.IsNaN(_lastNow) ? 0 : Math.Clamp(now - _lastNow, 0, 0.1);
        _lastNow = now;

        if (!_source.IsValid)
            return queuedFrames <= maxFrames ? Silence(dst, maxFrames) : 0;

        // When will the next frame we write actually be played?
        double playTime = SinkClock.IsLocked
            ? SinkClock.TimeAt(FramesWritten)
            : now + queuedFrames / _sinkRate;

        // ...and which source frame should that be?
        double desired = _source.FrameAt(playTime - DelaySeconds);

        // The first estimates are rough; once both clocks have settled, line up exactly in one
        // step rather than gliding there over a couple of seconds.
        bool locked = SinkClock.IsLocked && _source.IsLocked;
        bool justLocked = locked && !_wasLocked;
        _wasLocked = locked;

        double error = (_position - desired) / _sourceRate;
        if (!_started || Math.Abs(error) > ReseekSeconds || (justLocked && Math.Abs(error) > 0.0005))
        {
            _position = desired;
            _started = true;
            _integral = 0;
            error = 0;
            Interlocked.Increment(ref _reseeks);
        }
        Volatile.Write(ref _errorSeconds, error);

        _integral = Math.Clamp(_integral + Ki * error * dt, -0.002, 0.002);
        double adjust = Math.Clamp(-Kp * error - _integral, -MaxAdjust, MaxAdjust);
        Adjust = adjust;
        double step = _nominalStep * (1 + adjust);

        // Only frames the capture side has already delivered can be read.
        long newest = _ring.WriteIndex - SincResampler.HalfTaps - 1;
        double room = newest - _position;
        int frames = room <= 0 ? 0 : (int)Math.Min(maxFrames, Math.Floor(room / step));
        if (frames <= 0)
        {
            Interlocked.Increment(ref _starved);
            // Nothing to read. If the device is about to run dry, feed it silence so its clock keeps
            // running; the gap shows up as an error next block and is corrected then.
            return queuedFrames <= maxFrames ? Silence(dst, maxFrames) : 0;
        }

        long first = (long)Math.Floor(_position) - (SincResampler.HalfTaps - 1);
        int need = (int)Math.Ceiling(step * frames) + SincResampler.HalfTaps * 2 + 2;
        if (_window.Length < need * 2) _window = new float[need * 2];
        _ring.CopyTo(first, _window, need);
        _resampler.Process(_window.AsSpan(0, need * 2), first, ref _position, step, dst, frames);

        FramesWritten += frames;
        return frames;
    }

    private int Silence(Span<float> dst, int frames)
    {
        dst.Slice(0, frames * 2).Clear();
        FramesWritten += frames;
        return frames;
    }
}
