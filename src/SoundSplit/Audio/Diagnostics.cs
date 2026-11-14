using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using SoundSplit.Core;
using SoundSplit.Services;

namespace SoundSplit.Audio;

/// <summary>
/// Command-line checks that write a report to a file instead of opening the window:
///   --probe report.txt            list devices and what they support (silent)
///   --drytest report.txt [secs]   run the whole engine on real devices, sending silence (silent)
///   --calibrate report.txt        measure every speaker with the microphone (plays test sounds)
///   --verify report.txt [name..]  share for real and measure how far apart the speakers land (plays test sounds;
///                                 speakers whose name contains one of the given words are turned up first)
/// </summary>
internal static class Diagnostics
{
    public static bool TryRun(string[] args)
    {
        if (args.Length < 2) return false;
        string mode = args[0];
        if (mode != "--probe" && mode != "--drytest" && mode != "--calibrate" && mode != "--verify") return false;

        var report = new StringBuilder();
        void Line(string s) => report.AppendLine(s);
        try
        {
            switch (mode)
            {
                case "--probe": Probe(Line); break;
                case "--drytest": DryTest(Line, args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 12, args.Contains("--low")); break;
                case "--calibrate": Calibrate(Line); break;
                case "--verify": Verify(Line, args.Skip(2).Where(x => !x.StartsWith("--")).ToList()); break;
            }
        }
        catch (Exception ex)
        {
            Line("FAILED: " + ex);
        }
        File.WriteAllText(args[1], report.ToString());
        return true;
    }

    private static void Probe(Action<string> line)
    {
        using var e = new MMDeviceEnumerator();
        string? def = Devices.DefaultOutputId(e);
        line("Outputs:");
        foreach (OutputInfo o in Devices.Outputs(e))
        {
            line($"  {(o.Id == def ? "*" : " ")} {o.Name}  [{o.Kind}{(o.IsBluetooth ? ", Bluetooth" : "")}]");
            line($"      id {o.Id}");
            try
            {
                using MMDevice d = e.GetDevice(o.Id);
                using AudioClient c = d.CreateAudioClient();
                var mix = c.MixFormat;
                line($"      mix {mix.SampleRate} Hz, {mix.Channels} ch, {mix.BitsPerSample} bit, {mix.Encoding}; period {c.DefaultDevicePeriod / 10000.0:F2} ms (min {c.MinimumDevicePeriod / 10000.0:F2} ms)");
                if (c.SupportsAudioClient3)
                {
                    var p = c.GetSharedModeEnginePeriod(mix);
                    line($"      shared periods: default {p.DefaultPeriodInFrames}, min {p.MinPeriodInFrames}, fundamental {p.FundamentalPeriodInFrames}, max {p.MaxPeriodInFrames} frames");
                }
                line($"      volume {d.AudioEndpointVolume.MasterVolumeLevelScalar:P0}{(d.AudioEndpointVolume.Mute ? " (muted)" : "")}");
                if (Environment.GetEnvironmentVariable("SOUNDSPLIT_PROPS") == "1")
                {
                    for (int i = 0; i < d.Properties.Count; i++)
                    {
                        try
                        {
                            var p = d.Properties[i];
                            object? v = p.Value;
                            if (v is string or uint or int or bool)
                                line($"        {p.Key.formatId},{p.Key.propertyId} = {v}");
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                line("      error: " + ex.Message);
            }
        }
        string? mic = Devices.DefaultMicId(e);
        line("Microphones:");
        foreach (MicInfo m in Devices.Microphones(e)) line($"  {(m.Id == mic ? "*" : " ")} {m.Name}");
    }

    private static void DryTest(Action<string> line, double seconds, bool low)
    {
        using var e = new MMDeviceEnumerator();
        string leader = Devices.DefaultOutputId(e) ?? throw new InvalidOperationException("No default output.");
        var outputs = Devices.Outputs(e);
        var others = outputs.Where(o => o.Id != leader).ToList();
        line($"Leader: {outputs.First(o => o.Id == leader).Name}");
        if (others.Count == 0)
        {
            line("No second output device to test with.");
            return;
        }

        // Ask for a different delay on each so both a long and a near-minimum delay are exercised.
        var requests = others.Select((o, i) => new SinkRequest(o.Id, i == 0 ? 0.180 : 0.0)).ToList();
        using var engine = new ShareEngine(leader, requests, lowLatency: low, mute: true);
        line($"Source {engine.SourceRate} Hz, low latency asked {low}, leader got it {engine.LeaderIsLowLatency}");

        var worst = new Dictionary<string, double>();
        double end = AudioTime.Now + seconds, settle = AudioTime.Now + 4;
        int tick = 0;
        while (AudioTime.Now < end)
        {
            Thread.Sleep(250);
            tick++;
            foreach (SinkStatus s in engine.Status())
            {
                if (AudioTime.Now > settle)
                    worst[s.DeviceId] = Math.Max(worst.GetValueOrDefault(s.DeviceId), Math.Abs(s.ErrorSeconds));
                if (tick % 4 == 0)
                {
                    string name = outputs.First(o => o.Id == s.DeviceId).Name;
                    line(string.Create(CultureInfo.InvariantCulture,
                        $"  t={tick / 4,3}s {name,-42} delay {s.DelaySeconds * 1000,6:F1} ms (min {s.MinDelaySeconds * 1000:F1})  err {s.ErrorSeconds * 1000,7:F3} ms  adj {s.AdjustPpm,6:F0} ppm  reseeks {s.Reseeks} starved {s.Starved} underruns {s.Underruns} locked {s.Locked}{(s.Fault != null ? " FAULT " + s.Fault : "")}"));
                }
            }
        }
        line($"Capture: packet {engine.CapturePacketSeconds * 1000:F2} ms, worst interval {engine.WorstCaptureIntervalSeconds * 1000:F1} ms, gaps {engine.Gaps}");
        foreach (var (id, w) in worst)
            line($"Worst timing error after settling, {outputs.First(o => o.Id == id).Name}: {w * 1000:F3} ms");
    }

    /// <summary>Unmutes / turns up speakers for a test and puts them back exactly as they were afterwards.</summary>
    private sealed class VolumeGuard(MMDeviceEnumerator enumerator, Action<string> line) : IDisposable
    {
        private readonly List<(MMDevice Device, AudioEndpointVolume Control, float Volume, bool Muted)> _saved = new();

        public bool MakeAudible(string id)
        {
            MMDevice device = enumerator.GetDevice(id);
            AudioEndpointVolume control = device.AudioEndpointVolume;
            float volume = control.MasterVolumeLevelScalar;
            bool muted = control.Mute;
            if (!muted && volume >= 0.4f)
            {
                device.Dispose();
                return false;
            }
            _saved.Add((device, control, volume, muted));
            line($"  (turning up {device.FriendlyName} for the test: it was at {volume:P0}{(muted ? ", muted" : "")})");
            if (volume < 0.4f) control.MasterVolumeLevelScalar = 0.4f;
            control.Mute = false;
            return true;
        }

        public void Dispose()
        {
            foreach (var (device, control, volume, muted) in _saved)
            {
                try
                {
                    control.MasterVolumeLevelScalar = volume;
                    control.Mute = muted;
                }
                catch
                {
                }
                device.Dispose();
            }
            _saved.Clear();
        }
    }

    private static string PickMic(MMDeviceEnumerator e, AppSettings settings)
    {
        var mics = Devices.Microphones(e);
        if (settings.MicId != null && mics.Any(m => m.Id == settings.MicId)) return settings.MicId;
        return Devices.DefaultMicId(e) ?? throw new InvalidOperationException("No microphone.");
    }

    private static void Calibrate(Action<string> line)
    {
        using var e = new MMDeviceEnumerator();
        var outputs = Devices.Outputs(e);
        AppSettings settings = AppSettings.Load();
        string mic = PickMic(e, settings);

        IReadOnlyList<SpeakerReading> readings;
        using (var volumes = new VolumeGuard(e, line))
            readings = Calibrator.Run(outputs.Select(o => o.Id).ToList(), mic, null, CancellationToken.None, makeAudible: volumes.MakeAudible);

        foreach (SpeakerReading r in readings)
        {
            string name = outputs.First(o => o.Id == r.DeviceId).Name;
            line(string.Create(CultureInfo.InvariantCulture,
                $"{name}: heard {r.Heard} snr {r.Snr:F1} direct {r.DirectLatency * 1000:F1} ms stream {r.StreamLatency * 1000:F1} ms loopback {r.LoopbackSeen} (loopback sees a frame {(r.StreamLatency - r.DirectLatency) * 1000:F2} ms after the device clock says it plays) {r.Problem}"));
        }
        var good = readings.Where(r => r.Heard).ToList();
        if (good.Count >= 2)
        {
            var measured = good.Select(r => new SpeakerMeasurement(r.DeviceId, r.DirectLatency, r.StreamLatency)).ToList();
            SyncPlan plan = SyncPlanner.Plan(measured, ShareEngine.TypicalMinDelay, Devices.DefaultOutputId(e));
            line($"Leader: {outputs.First(o => o.Id == plan.LeaderId).Name}");
            foreach (var (id, d) in plan.Delays)
                line(string.Create(CultureInfo.InvariantCulture,
                    $"  delay {outputs.First(o => o.Id == id).Name} by {d * 1000:F1} ms (leftover {plan.Residuals[id] * 1000:F1} ms)"));

            settings.Calibration = good.ToDictionary(r => r.DeviceId,
                r => new CalibrationEntry { Direct = r.DirectLatency, Stream = r.StreamLatency, Snr = r.Snr });
            settings.CalibratedAt = DateTime.Now;
            foreach (SpeakerReading r in good) settings.TrimMs.Remove(r.DeviceId);   // old nudges no longer apply
            settings.Save();
            line("Saved to settings.");
        }
    }

    /// <summary>
    /// The real proof: share with the saved measurements, then time the leader on its own and
    /// the copied speakers on their own against the same reference. If sync is right the two
    /// numbers are equal.
    /// </summary>
    private static void Verify(Action<string> line, List<string> turnUp)
    {
        using var e = new MMDeviceEnumerator();
        var outputs = Devices.Outputs(e);
        AppSettings settings = AppSettings.Load();
        var measured = outputs.Where(o => settings.Calibration.ContainsKey(o.Id))
            .Select(o => new SpeakerMeasurement(o.Id, settings.Calibration[o.Id].Direct, settings.Calibration[o.Id].Stream)).ToList();
        if (measured.Count < 2)
        {
            line("Fewer than two measured speakers are connected. Run --calibrate first.");
            return;
        }

        string Name(string id) => outputs.First(o => o.Id == id).Name;
        string? original = Devices.DefaultOutputId(e);
        SyncPlan plan = SyncPlanner.Plan(measured, ShareEngine.TypicalMinDelay, original);
        SpeakerMeasurement lead = measured.First(m => m.Id == plan.LeaderId);
        var requests = measured.Where(m => m.Id != plan.LeaderId)
            .Select(m => new SinkRequest(m.Id, lead.DirectLatency - m.StreamLatency)).ToList();
        line($"Leader: {Name(plan.LeaderId)}");
        foreach (SinkRequest r in requests)
            line(string.Create(CultureInfo.InvariantCulture, $"Copy to: {Name(r.DeviceId)}, held back {r.DelaySeconds * 1000:F1} ms"));

        using var volumes = new VolumeGuard(e, line);
        foreach (SpeakerMeasurement m in measured)
            if (turnUp.Any(word => Name(m.Id).Contains(word, StringComparison.OrdinalIgnoreCase))) volumes.MakeAudible(m.Id);

        bool switched = plan.LeaderId != original;
        if (switched) Devices.SetDefaultOutput(plan.LeaderId);
        try
        {
            bool low = requests.Any(r => r.DelaySeconds < ShareEngine.TypicalMinDelay);
            using var engine = new ShareEngine(plan.LeaderId, requests, low);
            using MMDevice micDevice = e.GetDevice(PickMic(e, settings));
            Calibrator.Recording? micRec = null;
            using var mic = new CaptureStream(micDevice, loopback: false, raw: true,
                (stereo, frames, time) => micRec!.Add(stereo, frames, time, leftOnly: true), "SoundSplit microphone");
            micRec = new Calibrator.Recording(mic.SampleRate);
            mic.Start();
            Thread.Sleep(3000);
            foreach (SinkStatus s in engine.Status())
                line(string.Create(CultureInfo.InvariantCulture,
                    $"  {Name(s.DeviceId)}: delay {s.DelaySeconds * 1000:F1} ms, clock error {s.ErrorSeconds * 1000:F3} ms, locked {s.Locked}, underruns {s.Underruns}"));

            // 1. Leader alone: the copies are silenced, and the test sound is played the way any app would play it.
            engine.Mute = true;
            Thread.Sleep(300);
            double direct, directSnr;
            using (MMDevice leader = e.GetDevice(plan.LeaderId))
            {
                float[] pattern = Array.Empty<float>();
                long written = 0, start = long.MaxValue;
                using var play = new RenderStream(leader, lowLatency: false, (dst, max, _, _) =>
                {
                    dst.Slice(0, max * 2).Clear();
                    long begin = Volatile.Read(ref start);
                    long w = written;
                    for (int k = 0; k < max; k++)
                    {
                        long p = w + k - begin;
                        if (p >= 0 && p < pattern.Length)
                        {
                            dst[2 * k] = pattern[p];
                            dst[2 * k + 1] = pattern[p];
                        }
                    }
                    Volatile.Write(ref written, w + max);
                    return max;
                }, "SoundSplit test sound");
                pattern = SyncSignal.Pattern(play.SampleRate, 0.35f);
                engine.StartRecording();
                double t0 = AudioTime.Now;
                play.Start();
                Thread.Sleep(600);
                Volatile.Write(ref start, Volatile.Read(ref written) + play.SampleRate / 20);
                Thread.Sleep((int)((SyncSignal.TotalSeconds + 1.1) * 1000));
                var (loop, loopStart) = engine.StopRecording();
                Detection mixed = ChirpDetector.Find(loop, engine.SourceRate);
                double mixTime = engine.TimeOfFrame(loopStart + mixed.Frame);
                float[] all = micRec.Snapshot();
                int from = micRec.FrameAt(t0 + 0.5);
                Detection heard = ChirpDetector.Find(all.AsSpan(from), micRec.Rate);
                direct = micRec.TimeOf(from + heard.Frame) - mixTime;
                directSnr = heard.Snr;
                line(string.Create(CultureInfo.InvariantCulture,
                    $"Leader alone:  heard {direct * 1000:F2} ms after Windows mixed it (snr {heard.Snr:F1}, found in loopback {mixed.Found})"));
            }

            // 2. Copies alone: the test sound is slipped into the captured audio, so the leader never plays it.
            engine.Mute = false;
            Thread.Sleep(500);
            double t1 = AudioTime.Now;
            engine.Inject(SyncSignal.Pattern(engine.SourceRate, 0.35f));
            Thread.Sleep((int)((SyncSignal.TotalSeconds + requests.Max(r => r.DelaySeconds) + 1.2) * 1000));
            double injectTime = engine.TimeOfFrame(engine.InjectStart);
            float[] all2 = micRec.Snapshot();
            int from2 = micRec.FrameAt(t1 - 0.02);
            Detection heard2 = ChirpDetector.Find(all2.AsSpan(from2), micRec.Rate);
            double copied = micRec.TimeOf(from2 + heard2.Frame) - injectTime;
            line(string.Create(CultureInfo.InvariantCulture,
                $"Copies alone:  heard {copied * 1000:F2} ms after Windows mixed it (snr {heard2.Snr:F1})"));
            foreach (SinkStatus s in engine.Status())
                line(string.Create(CultureInfo.InvariantCulture,
                    $"  {Name(s.DeviceId)}: clock error {s.ErrorSeconds * 1000:F3} ms, underruns {s.Underruns}, starved {s.Starved}, reseeks {s.Reseeks}"));

            if (directSnr >= ChirpDetector.MinSnr && heard2.Snr >= ChirpDetector.MinSnr)
                line(string.Create(CultureInfo.InvariantCulture, $"SYNC ERROR: {(copied - direct) * 1000:F2} ms (positive = the copied speaker is late)"));
            else
                line("One of the two was not heard clearly, so the sync error could not be measured.");
        }
        finally
        {
            if (switched && original != null) Devices.SetDefaultOutput(original);
        }
    }
}
