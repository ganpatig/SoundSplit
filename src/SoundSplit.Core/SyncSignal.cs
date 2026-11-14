using System;
using System.Numerics;

namespace SoundSplit.Core;

/// <summary>
/// The test sound used to measure a speaker: three short rising chirps at uneven spacing.
/// A chirp has a razor-sharp correlation peak (a fraction of a millisecond wide) even in a
/// noisy, echoey room, and the uneven spacing means the pattern can only line up one way, so
/// there is no ambiguity even when a speaker is half a second late.
/// </summary>
public static class SyncSignal
{
    public const double ChirpSeconds = 0.12;
    public const double StartHz = 700;
    public const double EndHz = 5000;

    /// <summary>Start time of each chirp within the pattern.</summary>
    public static readonly double[] Offsets = { 0.0, 0.31, 0.74 };

    public static double TotalSeconds => Offsets[^1] + ChirpSeconds;

    /// <summary>One chirp at unit amplitude, with a smooth fade in and out so it does not click.</summary>
    public static float[] Chirp(int sampleRate)
    {
        int n = (int)Math.Round(ChirpSeconds * sampleRate);
        var c = new float[n];
        double k = (EndHz - StartHz) / ChirpSeconds;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double phase = 2 * Math.PI * (StartHz * t + 0.5 * k * t * t);
            double edge = Math.Min(1.0, Math.Min(i, n - 1 - i) / (0.15 * n));
            double window = 0.5 - 0.5 * Math.Cos(Math.PI * edge);
            c[i] = (float)(Math.Sin(phase) * window);
        }
        return c;
    }

    /// <summary>The whole pattern (mono) at the given amplitude.</summary>
    public static float[] Pattern(int sampleRate, float amplitude)
    {
        var chirp = Chirp(sampleRate);
        var p = new float[(int)Math.Ceiling(TotalSeconds * sampleRate) + 1];
        foreach (double off in Offsets)
        {
            int at = (int)Math.Round(off * sampleRate);
            for (int i = 0; i < chirp.Length && at + i < p.Length; i++) p[at + i] += chirp[i] * amplitude;
        }
        return p;
    }
}

/// <summary>Result of looking for the test pattern in a recording.</summary>
/// <param name="Found">Whether the pattern stood clearly out of the noise.</param>
/// <param name="Frame">Frame of the recording at which the pattern starts.</param>
/// <param name="Snr">Peak height over the background level; around 10 is a marginal detection, 50+ is solid.</param>
public readonly record struct Detection(bool Found, int Frame, double Snr);

public static class ChirpDetector
{
    public const double MinSnr = 9.0;

    /// <summary>Finds where <see cref="SyncSignal.Pattern"/> starts in a mono recording.</summary>
    public static Detection Find(ReadOnlySpan<float> recording, int sampleRate)
    {
        float[] chirp = SyncSignal.Chirp(sampleRate);
        int m = chirp.Length;
        int lags = recording.Length - m + 1;
        if (lags <= 0) return new Detection(false, 0, 0);

        // Matched filter: how strongly does the recording resemble one chirp at each lag?
        var corr = new float[lags];
        for (int k = 0; k < lags; k++) corr[k] = Dot(recording.Slice(k, m), chirp);

        // Add the three chirps' responses together at their known spacing.
        int o1 = (int)Math.Round(SyncSignal.Offsets[1] * sampleRate);
        int o2 = (int)Math.Round(SyncSignal.Offsets[2] * sampleRate);
        int n = lags - o2;
        if (n <= 0) return new Detection(false, 0, 0);

        var score = new float[n];
        int best = 0;
        float peak = 0;
        double energy = 0;
        for (int k = 0; k < n; k++)
        {
            float s = Math.Abs(corr[k] + corr[k + o1] + corr[k + o2]);
            score[k] = s;
            energy += (double)s * s;
            if (s > peak) { peak = s; best = k; }
        }
        if (peak <= 0) return new Detection(false, 0, 0);

        // Background level: everything except the neighbourhood of the peak.
        int guard = sampleRate / 50;
        int lo = Math.Max(0, best - guard), hi = Math.Min(n, best + guard);
        double inside = 0;
        for (int k = lo; k < hi; k++) inside += (double)score[k] * score[k];
        int outside = n - (hi - lo);
        double noise = outside > 0 ? Math.Sqrt(Math.Max(0, energy - inside) / outside) : 0;
        double snr = noise > 0 ? peak / noise : double.PositiveInfinity;

        // A room adds reflections after the direct sound and one of them can be the loudest.
        // Sync should follow the direct sound, so take the first strong peak, not the biggest.
        int from = Math.Max(0, best - sampleRate / 40);
        int first = best;
        for (int k = from; k < best; k++)
        {
            if (score[k] >= 0.6f * peak)
            {
                first = k;
                int end = Math.Min(best, k + sampleRate / 2000);
                for (int j = k; j <= end; j++)
                    if (score[j] > score[first]) first = j;
                break;
            }
        }

        return new Detection(snr >= MinSnr, first, snr);
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int i = 0;
        var acc = Vector<float>.Zero;
        int w = Vector<float>.Count;
        for (; i <= a.Length - w; i += w)
            acc += new Vector<float>(a.Slice(i, w)) * new Vector<float>(b.Slice(i, w));
        float sum = Vector.Sum(acc);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
