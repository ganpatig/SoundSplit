using System;

namespace SoundSplit.Core;

/// <summary>
/// Reads stereo audio at a fractional, slowly varying rate. This does two jobs at once: it
/// converts between device sample rates (48 kHz to 44.1 kHz, say), and it lets an output run a
/// hair faster or slower than the source so two unrelated device clocks stay locked together.
/// Windowed-sinc interpolation, so it is transparent rather than merely "good enough".
/// </summary>
public sealed class SincResampler
{
    /// <summary>Source frames needed on each side of the read position.</summary>
    public const int HalfTaps = 16;
    private const int Taps = HalfTaps * 2;
    private const int Phases = 256;

    private readonly float[] _table = new float[(Phases + 1) * Taps];

    /// <param name="step">Nominal source frames per output frame; above 1 the filter also low-passes to avoid aliasing.</param>
    public SincResampler(double step)
    {
        double cutoff = step > 1 ? 0.97 / step : 1.0;
        const double beta = 6.5;
        double i0Beta = BesselI0(beta);

        Span<double> row = stackalloc double[Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int j = 0; j < Taps; j++)
            {
                // Tap j sits at source frame (floor(pos) - HalfTaps + 1 + j).
                double x = j - (HalfTaps - 1) - frac;
                double w = x / HalfTaps;
                double window = Math.Abs(w) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - w * w)) / i0Beta;
                double arg = Math.PI * cutoff * x;
                double sinc = Math.Abs(arg) < 1e-9 ? 1 : Math.Sin(arg) / arg;
                row[j] = cutoff * sinc * window;
                sum += row[j];
            }
            for (int j = 0; j < Taps; j++) _table[p * Taps + j] = (float)(row[j] / sum);
        }
    }

    /// <summary>
    /// Produces <paramref name="frames"/> output frames. <paramref name="src"/> is a contiguous stereo
    /// window whose first frame is absolute frame <paramref name="srcBase"/>; it must cover
    /// HalfTaps frames either side of every position read.
    /// </summary>
    public void Process(ReadOnlySpan<float> src, long srcBase, ref double pos, double step, Span<float> dst, int frames)
    {
        double p = pos;
        for (int k = 0; k < frames; k++)
        {
            double fl = Math.Floor(p);
            double frac = p - fl;
            int first = (int)((long)fl - srcBase) - (HalfTaps - 1);

            double ph = frac * Phases;
            int pi = (int)ph;
            float t = (float)(ph - pi);
            ReadOnlySpan<float> a = _table.AsSpan(pi * Taps, Taps);
            ReadOnlySpan<float> b = _table.AsSpan((pi + 1) * Taps, Taps);
            ReadOnlySpan<float> s = src.Slice(first * 2, Taps * 2);

            float l = 0, r = 0;
            for (int j = 0; j < Taps; j++)
            {
                float c = a[j] + (b[j] - a[j]) * t;
                l += s[2 * j] * c;
                r += s[2 * j + 1] * c;
            }
            dst[2 * k] = l;
            dst[2 * k + 1] = r;
            p += step;
        }
        pos = p;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 40; k++)
        {
            term *= half / k;
            double add = term * term;
            sum += add;
            if (add < sum * 1e-14) break;
        }
        return sum;
    }
}
