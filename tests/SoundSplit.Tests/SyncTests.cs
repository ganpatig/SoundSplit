using SoundSplit.Core;
using Xunit.Abstractions;

namespace SoundSplit.Tests;

public class SyncedReaderTests(ITestOutputHelper output)
{
    /// <summary>
    /// Two simulated devices with different sample rates, clocks that disagree by a realistic
    /// amount, 10 ms packets and jittery timestamps. Returns the true capture-to-play latency
    /// of every block after the settle time.
    /// </summary>
    private List<double> Simulate(
        double srcNominal, double sinkNominal, double srcPpm, double sinkPpm,
        double delay, double seconds, double settle, Func<double, double>? delayAt = null, int seed = 1)
    {
        var rng = new Random(seed);
        double srcRate = srcNominal * (1 + srcPpm * 1e-6);
        double sinkRate = sinkNominal * (1 + sinkPpm * 1e-6);

        var ring = new StereoRing((int)(srcNominal * 4));
        var srcTimeline = new Timeline(srcNominal);
        var reader = new SyncedReader(ring, srcTimeline, srcNominal, sinkNominal) { DelaySeconds = delay };

        int srcPacket = (int)(srcNominal / 100);
        int sinkPeriod = (int)(sinkNominal / 100);
        int target = sinkPeriod * 2;

        // The source signal is a ramp that encodes its own frame number, so the frame that ends
        // up at any point in the output can be read straight back out of the audio.
        var packet = new float[srcPacket * 2];
        long srcFrames = 0;
        const double deliveryLag = 0.002;              // packet reaches us this long after its last frame
        double nextSrc = srcPacket / srcRate + deliveryLag;
        double nextSink = 0.0201;
        double sinkStart = double.NaN;                 // when the device began playing
        var outBuf = new float[sinkPeriod * 8 * 2];
        var latencies = new List<double>();
        double Jitter(double ms) => (rng.NextDouble() - 0.5) * 2 * ms * 1e-3;

        while (Math.Min(nextSrc, nextSink) < seconds)
        {
            if (nextSrc <= nextSink)
            {
                double t = nextSrc;
                for (int i = 0; i < srcPacket; i++)
                {
                    // Two ramps half a cycle apart, so one of them is always far from its wrap-around.
                    packet[2 * i] = (float)((srcFrames + i) % 65536) / 65536f;
                    packet[2 * i + 1] = (float)((srcFrames + i + 32768) % 65536) / 65536f;
                }
                // Timestamp = time of the first frame in the packet, with jitter.
                double stamp = srcFrames / srcRate + Jitter(0.4);
                srcTimeline.Add(stamp, srcFrames);
                ring.Write(packet);
                srcFrames += srcPacket;
                nextSrc = (srcFrames + srcPacket) / srcRate + deliveryLag + Math.Abs(Jitter(1.0));
            }
            else
            {
                double t = nextSink;
                if (delayAt != null) reader.DelaySeconds = delayAt(t);

                double played = double.IsNaN(sinkStart) ? 0 : Math.Min(reader.FramesWritten, (t - sinkStart) * sinkRate);
                // The engine takes whole periods at a time.
                long consumed = (long)(Math.Floor(played / sinkPeriod) * sinkPeriod);
                int queued = (int)(reader.FramesWritten - consumed);
                if (!double.IsNaN(sinkStart)) reader.ReportPosition(t + Jitter(0.3), played + Jitter(0.3) * sinkRate);

                int want = target - queued;
                long before = reader.FramesWritten;
                int got = want > 0 ? reader.Read(outBuf, want, t, queued) : 0;
                if (got > 0 && double.IsNaN(sinkStart)) sinkStart = t;

                if (got > 0 && t > settle)
                {
                    // Frame `before` of the output plays at sinkStart + before / sinkRate.
                    double playTime = sinkStart + before / sinkRate;
                    // Decode which source frame it carries from the ramp.
                    bool left = outBuf[0] > 0.2f && outBuf[0] < 0.8f;
                    double frac = left ? outBuf[0] * 65536.0 : outBuf[1] * 65536.0 - 32768;
                    double approx = (playTime - reader.DelaySeconds) * srcRate;
                    double srcFrame = frac + Math.Round((approx - frac) / 65536.0) * 65536.0;
                    latencies.Add(playTime - srcFrame / srcRate);
                }
                nextSink = t + sinkPeriod / sinkRate + Math.Abs(Jitter(0.8));
            }
        }

        output.WriteLine($"reseeks={reader.Reseeks} starved={reader.Starved} adjust={reader.Adjust * 1e6:F0} ppm samples={latencies.Count}");
        return latencies;
    }

    [Theory]
    [InlineData(48000, 48000, 0, 0, 0.180)]
    [InlineData(48000, 48000, 120, -150, 0.180)]     // clocks 270 ppm apart: 16 ms of drift per minute if uncorrected
    [InlineData(48000, 44100, -200, 200, 0.220)]     // different sample rates as well
    [InlineData(44100, 48000, 80, -60, 0.060)]
    [InlineData(48000, 48000, 150, -150, 0.040)]     // close to the minimum delay
    public void HoldsTheRequestedDelay(double srcNominal, double sinkNominal, double srcPpm, double sinkPpm, double delay)
    {
        var lat = Simulate(srcNominal, sinkNominal, srcPpm, sinkPpm, delay, seconds: 90, settle: 6);
        Assert.True(lat.Count > 1000);

        double mean = lat.Average();
        double worst = lat.Max(l => Math.Abs(l - delay));
        double rms = Math.Sqrt(lat.Average(l => (l - delay) * (l - delay)));
        output.WriteLine($"target {delay * 1000:F1} ms  mean {mean * 1000:F3} ms  rms err {rms * 1000:F3} ms  worst {worst * 1000:F3} ms");

        Assert.InRange(mean, delay - 0.0005, delay + 0.0005);
        Assert.True(worst < 0.002, $"worst error {worst * 1000:F2} ms");
    }

    [Fact]
    public void DoesNotDriftOverTenMinutes()
    {
        var lat = Simulate(48000, 48000, 200, -200, 0.150, seconds: 600, settle: 6);
        double early = lat.Take(2000).Average(), late = lat.Skip(lat.Count - 2000).Average();
        output.WriteLine($"first {early * 1000:F3} ms, last {late * 1000:F3} ms");
        // Uncorrected, 400 ppm over ten minutes would be a 240 ms slide.
        Assert.InRange(late - early, -0.0005, 0.0005);
    }

    [Fact]
    public void FollowsADelayChange()
    {
        // Small nudge (glides) then a big one (jumps).
        var lat = Simulate(48000, 48000, 50, -50, 0.100, seconds: 40, settle: 0,
            delayAt: t => t < 10 ? 0.100 : t < 20 ? 0.108 : 0.250);
        Assert.NotEmpty(lat);
        // Sample the tail: must be sitting on 250 ms.
        double tail = lat.Skip(lat.Count - 300).Average();
        Assert.InRange(tail, 0.2495, 0.2505);
    }
}

public class ChirpDetectorTests(ITestOutputHelper output)
{
    private static float[] Record(int rate, double seconds, double arrival, float level, float noise, int seed,
        (double delay, float gain)[]? echoes = null, bool invert = false)
    {
        var rng = new Random(seed);
        var rec = new float[(int)(rate * seconds)];
        for (int i = 0; i < rec.Length; i++)
        {
            // Roughly Gaussian room noise.
            double g = rng.NextDouble() + rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 2;
            rec[i] = (float)(g * noise);
        }
        var pattern = SyncSignal.Pattern(rate, invert ? -level : level);
        void Mix(double at, float gain)
        {
            int start = (int)Math.Round(at * rate);
            for (int i = 0; i < pattern.Length && start + i < rec.Length; i++) rec[start + i] += pattern[i] * gain;
        }
        Mix(arrival, 1);
        foreach (var (d, g) in echoes ?? []) Mix(arrival + d, g);
        return rec;
    }

    [Theory]
    [InlineData(48000, 0.0421)]
    [InlineData(48000, 0.2630)]
    [InlineData(44100, 0.5127)]
    [InlineData(16000, 0.1904)]
    public void FindsThePatternInNoise(int rate, double arrival)
    {
        // Chirp well below the noise floor in amplitude: the matched filter still pulls it out.
        var rec = Record(rate, 2.6, arrival, level: 0.02f, noise: 0.05f, seed: 7);
        var d = ChirpDetector.Find(rec, rate);
        output.WriteLine($"snr {d.Snr:F1}, at {d.Frame / (double)rate * 1000:F2} ms (true {arrival * 1000:F2})");
        Assert.True(d.Found);
        Assert.InRange(d.Frame / (double)rate, arrival - 0.0003, arrival + 0.0003);
    }

    [Fact]
    public void PrefersTheDirectSoundOverALouderEcho()
    {
        var rec = Record(48000, 2.6, 0.2, 0.05f, 0.01f, 3, echoes: [(0.0085, 1.4f), (0.021, 0.7f)]);
        var d = ChirpDetector.Find(rec, 48000);
        Assert.True(d.Found);
        Assert.InRange(d.Frame / 48000.0, 0.1997, 0.2003);
    }

    [Fact]
    public void HandlesInvertedPolarity()
    {
        var rec = Record(48000, 2.6, 0.31, 0.05f, 0.01f, 5, invert: true);
        var d = ChirpDetector.Find(rec, 48000);
        Assert.True(d.Found);
        Assert.InRange(d.Frame / 48000.0, 0.3097, 0.3103);
    }

    [Fact]
    public void ReportsNothingWhenThereIsOnlyNoise()
    {
        var rec = Record(48000, 2.6, 0.3, level: 0f, noise: 0.05f, seed: 11);
        var d = ChirpDetector.Find(rec, 48000);
        output.WriteLine($"snr {d.Snr:F1}");
        Assert.False(d.Found);
    }

    [Fact]
    public void ReportsNothingForMusicAlone()
    {
        // Loud tonal content in the chirp's own band must not be mistaken for it.
        var rec = new float[48000 * 3];
        for (int i = 0; i < rec.Length; i++)
        {
            double t = i / 48000.0;
            rec[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 880 * t) + 0.2 * Math.Sin(2 * Math.PI * 2637 * t)
                + 0.2 * Math.Sin(2 * Math.PI * (1200 + 300 * Math.Sin(2 * Math.PI * 3 * t)) * t));
        }
        var d = ChirpDetector.Find(rec, 48000);
        output.WriteLine($"snr {d.Snr:F1}");
        Assert.False(d.Found);
    }
}

public class SyncPlannerTests
{
    [Fact]
    public void SlowBluetoothSpeakerLeadsAndTheLaptopIsDelayed()
    {
        var plan = SyncPlanner.Plan(
        [
            new SpeakerMeasurement("laptop", DirectLatency: 0.045, StreamLatency: 0.030),
            new SpeakerMeasurement("bluetooth", DirectLatency: 0.230, StreamLatency: 0.215),
        ], minDelay: 0.035, preferredLeader: "laptop");

        Assert.Equal("bluetooth", plan.LeaderId);
        Assert.Equal(0.200, plan.Delays["laptop"], 6);
        Assert.Equal(0, plan.Residuals["laptop"], 6);
    }

    [Fact]
    public void KeepsTheCurrentDefaultWhenEitherWouldDo()
    {
        var speakers = new[]
        {
            new SpeakerMeasurement("a", 0.200, 0.100),
            new SpeakerMeasurement("b", 0.200, 0.100),
        };
        Assert.Equal("a", SyncPlanner.Plan(speakers, 0.035, "a").LeaderId);
        Assert.Equal("b", SyncPlanner.Plan(speakers, 0.035, "b").LeaderId);
    }

    [Fact]
    public void NearlyEqualSpeakersReportTheLeftoverOffset()
    {
        var plan = SyncPlanner.Plan(
        [
            new SpeakerMeasurement("a", 0.040, 0.030),
            new SpeakerMeasurement("b", 0.046, 0.036),
        ], minDelay: 0.035);

        Assert.Equal("b", plan.LeaderId);
        Assert.Equal(0.035, plan.Delays["a"], 6);
        Assert.Equal(0.019, plan.Residuals["a"], 6);
    }

    [Fact]
    public void ThreeSpeakersAllMeetTheSlowest()
    {
        var plan = SyncPlanner.Plan(
        [
            new SpeakerMeasurement("wired", 0.040, 0.030),
            new SpeakerMeasurement("bt-fast", 0.180, 0.170),
            new SpeakerMeasurement("bt-slow", 0.290, 0.280),
        ], minDelay: 0.035);

        Assert.Equal("bt-slow", plan.LeaderId);
        Assert.Equal(0.260, plan.Delays["wired"], 6);
        Assert.Equal(0.120, plan.Delays["bt-fast"], 6);
        Assert.Equal(0, plan.WorstResidual, 6);
    }
}
