using System;
using System.Threading;

namespace SoundSplit.Core;

/// <summary>
/// Maps a device's frame counter to wall-clock time (and back) from a stream of noisy
/// (time, frame) observations. A least-squares line over the last few seconds averages out
/// packet jitter, so the mapping is good to a fraction of a millisecond even though each
/// individual observation is only good to a few.
/// </summary>
public sealed class Timeline
{
    private readonly double[] _t;
    private readonly double[] _f;
    private int _head;
    private int _count;
    private double _lastT = double.NegativeInfinity;
    private double _meanT;
    private double _meanF;
    private double _rate;
    private int _version;   // odd while the fit is being rewritten

    public Timeline(double nominalRate, int capacity = 512)
    {
        if (nominalRate <= 0) throw new ArgumentOutOfRangeException(nameof(nominalRate));
        NominalRate = nominalRate;
        _rate = nominalRate;
        _t = new double[capacity];
        _f = new double[capacity];
    }

    /// <summary>Frames per second the device claims to run at.</summary>
    public double NominalRate { get; }

    /// <summary>Observations closer together than this are dropped, so the window always spans a few seconds.</summary>
    public double MinSpacing { get; init; } = 0.005;

    /// <summary>Real device clocks are within a fraction of a percent of nominal; anything else is a bad fit.</summary>
    public double MaxRateDeviation { get; init; } = 0.005;

    public int Count => _count;

    /// <summary>At least one observation: the mapping is usable (with the nominal rate).</summary>
    public bool IsValid => _count > 0;

    /// <summary>Enough observations that jitter has been averaged out.</summary>
    public bool IsLocked { get; private set; }

    /// <summary>Measured frames per second.</summary>
    public double Rate => _rate;

    public void Reset()
    {
        _head = 0;
        _count = 0;
        _lastT = double.NegativeInfinity;
        Interlocked.Increment(ref _version);
        _rate = NominalRate;
        Interlocked.Increment(ref _version);
        IsLocked = false;
    }

    public void Add(double time, double frame)
    {
        if (time - _lastT < MinSpacing) return;
        _lastT = time;

        _t[_head] = time;
        _f[_head] = frame;
        _head = (_head + 1) % _t.Length;
        if (_count < _t.Length) _count++;

        // Refit. A few hundred points every few milliseconds is nothing, and refitting from
        // scratch around the newest point keeps full double precision over hours of running.
        double sumT = 0, sumF = 0;
        for (int i = 0; i < _count; i++)
        {
            sumT += _t[i] - time;
            sumF += _f[i] - frame;
        }
        double mt = sumT / _count, mf = sumF / _count;
        double sxx = 0, sxy = 0, minT = 0;
        for (int i = 0; i < _count; i++)
        {
            double dt = _t[i] - time - mt;
            sxx += dt * dt;
            sxy += dt * (_f[i] - frame - mf);
            if (_t[i] - time < minT) minT = _t[i] - time;
        }

        double span = -minT;
        double rate = NominalRate;
        if (_count >= 8 && span >= 0.05 && sxx > 0)
        {
            rate = sxy / sxx;
            double lo = NominalRate * (1 - MaxRateDeviation), hi = NominalRate * (1 + MaxRateDeviation);
            if (!(rate >= lo)) rate = lo;
            if (rate > hi) rate = hi;
        }

        // One thread adds observations while others read the fit; the version counter lets a
        // reader notice it caught the three numbers half-updated and try again.
        Interlocked.Increment(ref _version);
        _rate = rate;
        _meanT = time + mt;
        _meanF = frame + mf;
        Interlocked.Increment(ref _version);
        IsLocked = _count >= 16 && span >= 0.12;
    }

    private void Snapshot(out double meanT, out double meanF, out double rate)
    {
        int before;
        do
        {
            before = Volatile.Read(ref _version);
            meanT = _meanT;
            meanF = _meanF;
            rate = _rate;
        }
        while ((before & 1) != 0 || before != Volatile.Read(ref _version));
    }

    /// <summary>The (fractional) frame the device is at, at the given time.</summary>
    public double FrameAt(double time)
    {
        Snapshot(out double meanT, out double meanF, out double rate);
        return meanF + rate * (time - meanT);
    }

    /// <summary>The time at which the device reaches the given frame.</summary>
    public double TimeAt(double frame)
    {
        Snapshot(out double meanT, out double meanF, out double rate);
        return meanT + (frame - meanF) / rate;
    }
}
