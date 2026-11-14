using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using SoundSplit.Core;

namespace SoundSplit.Audio;

/// <summary>What the microphone found for one speaker.</summary>
/// <param name="Heard">The microphone picked the test sound out clearly.</param>
/// <param name="DirectLatency">Seconds from Windows mixing a sound for this device to the microphone hearing it.</param>
/// <param name="StreamLatency">Seconds from the app's stream reaching a sound to the microphone hearing it.</param>
/// <param name="Snr">How clearly the test sound stood out (10 is marginal, 50+ is solid).</param>
/// <param name="LoopbackSeen">The test sound was also found in the device's own playback recording.</param>
internal sealed record SpeakerReading(
    string DeviceId,
    bool Heard,
    double DirectLatency,
    double StreamLatency,
    double Snr,
    bool LoopbackSeen,
    string? Problem);

internal sealed record CalibrationProgress(int Index, int Count, string DeviceId);

internal sealed class MicrophoneSilentException() : Exception("The microphone recorded only silence.");

/// <summary>
/// Measures how late each speaker is by playing a test sound on it and timing its arrival at
/// the microphone. All speakers are heard by the same microphone, so the microphone's own
/// delay cancels out when the speakers are compared with each other.
/// </summary>
internal static class Calibrator
{
    private const double PreRollSeconds = 0.7;    // wakes a sleeping Bluetooth link and lets the clocks settle
    private const double TailSeconds = 1.2;       // how late a speaker may be and still be measured

    /// <summary>Total time one speaker takes to measure.</summary>
    public static double SecondsPerSpeaker => PreRollSeconds + SyncSignal.TotalSeconds + TailSeconds;

    internal sealed class Recording(int rate)
    {
        private readonly List<(double Time, long Frame)> _stamps = new();
        private float[] _samples = new float[rate * 4];
        private int _count;

        public int Rate { get; } = rate;

        public void Add(ReadOnlySpan<float> stereo, int frames, double time, bool leftOnly)
        {
            lock (_stamps)
            {
                if (_count + frames > _samples.Length) Array.Resize(ref _samples, Math.Max(_samples.Length * 2, _count + frames));
                _stamps.Add((time, _count));
                for (int i = 0; i < frames; i++)
                    _samples[_count + i] = leftOnly ? stereo[2 * i] : (stereo[2 * i] + stereo[2 * i + 1]) * 0.5f;
                _count += frames;
            }
        }

        public float[] Snapshot()
        {
            lock (_stamps) return _samples.AsSpan(0, _count).ToArray();
        }

        /// <summary>First recorded frame at or after the given time.</summary>
        public int FrameAt(double time)
        {
            lock (_stamps)
            {
                foreach (var (t, f) in _stamps)
                    if (t >= time) return (int)f;
                return _count;
            }
        }

        /// <summary>When a frame was captured, from the packet timestamps around it.</summary>
        public double TimeOf(double frame)
        {
            var fit = new Timeline(Rate, 1024) { MinSpacing = 0 };
            lock (_stamps)
            {
                foreach (var (t, f) in _stamps)
                    if (Math.Abs(f - frame) < Rate * 1.0) fit.Add(t, f);
            }
            return fit.IsValid ? fit.TimeAt(frame) : double.NaN;
        }
    }

    /// <summary>Measures each speaker in turn. Blocks for <see cref="SecondsPerSpeaker"/> per speaker; makes sound.</summary>
    /// <param name="makeAudible">
    /// Called when the microphone could not hear a speaker. If it can do something about that
    /// (unmute it, turn it up) it returns true and the speaker is measured once more. Volumes
    /// are never touched for speakers that are heard first time.
    /// </param>
    public static IReadOnlyList<SpeakerReading> Run(
        IReadOnlyList<string> deviceIds, string micId, Action<CalibrationProgress>? progress,
        CancellationToken cancel, float amplitude = 0.35f, Func<string, bool>? makeAudible = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice micDevice = enumerator.GetDevice(micId);

        Recording? micRec = null;
        using var mic = new CaptureStream(micDevice, loopback: false, raw: true,
            (stereo, frames, time) => micRec!.Add(stereo, frames, time, leftOnly: true), "SoundSplit microphone");
        micRec = new Recording(mic.SampleRate);
        mic.Start();
        cancel.WaitHandle.WaitOne(250);

        var readings = new List<SpeakerReading>();
        for (int i = 0; i < deviceIds.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Invoke(new CalibrationProgress(i, deviceIds.Count, deviceIds[i]));
            try
            {
                using MMDevice device = enumerator.GetDevice(deviceIds[i]);
                SpeakerReading reading = Measure(device, micRec, amplitude, cancel);
                if (!reading.Heard && makeAudible != null && makeAudible(deviceIds[i]))
                {
                    cancel.ThrowIfCancellationRequested();
                    reading = Measure(device, micRec, amplitude, cancel);
                }
                readings.Add(reading);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                readings.Add(new SpeakerReading(deviceIds[i], false, 0, 0, 0, false, ex.Message));
            }
        }

        float loudest = 0;
        foreach (float v in micRec.Snapshot()) loudest = Math.Max(loudest, Math.Abs(v));
        if (loudest == 0) throw new MicrophoneSilentException();
        return readings;
    }

    private static SpeakerReading Measure(MMDevice device, Recording micRec, float amplitude, CancellationToken cancel)
    {
        string id = device.ID;
        var positions = new List<(double Time, double Frame)>();
        float[] pattern = Array.Empty<float>();
        long written = 0;
        long patternStart = long.MaxValue;

        using var render = new RenderStream(device, lowLatency: false, (dst, max, _, _) =>
        {
            dst.Slice(0, max * 2).Clear();
            long start = Volatile.Read(ref patternStart);
            long w = written;
            for (int k = 0; k < max; k++)
            {
                long p = w + k - start;
                if (p >= 0 && p < pattern.Length)
                {
                    dst[2 * k] = pattern[p];
                    dst[2 * k + 1] = pattern[p];
                }
            }
            Volatile.Write(ref written, w + max);
            return max;
        }, "SoundSplit test sound");
        pattern = SyncSignal.Pattern(render.SampleRate, amplitude);
        render.PositionReported = (t, f) =>
        {
            lock (positions) positions.Add((t, f));
        };

        Recording? loopRec = null;
        using var loop = new CaptureStream(device, loopback: true, raw: false,
            (stereo, frames, time) => loopRec!.Add(stereo, frames, time, leftOnly: false), "SoundSplit test capture");
        loopRec = new Recording(loop.SampleRate);

        loop.Start();
        render.Start();
        double runStart = AudioTime.Now;
        if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(PreRollSeconds))) throw new OperationCanceledException(cancel);

        long startFrame = Volatile.Read(ref written) + render.SampleRate / 20;
        Volatile.Write(ref patternStart, startFrame);
        if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(SyncSignal.TotalSeconds + TailSeconds + 0.05)))
            throw new OperationCanceledException(cancel);
        double runEnd = AudioTime.Now;

        render.Dispose();
        loop.Dispose();

        // When did the device's own clock say the pattern started playing?
        var clock = new Timeline(render.SampleRate, 1024) { MinSpacing = 0 };
        lock (positions)
        {
            foreach (var (t, f) in positions)
                if (Math.Abs(f - startFrame) < render.SampleRate * 1.0) clock.Add(t, f);
        }
        if (!clock.IsValid) return new SpeakerReading(id, false, 0, 0, 0, false, "The device did not report its play position.");
        double streamTime = clock.TimeAt(startFrame);

        // When did the microphone hear it?
        float[] micAll = micRec.Snapshot();
        int from = micRec.FrameAt(runStart + PreRollSeconds - 0.1);
        int to = Math.Min(micAll.Length, micRec.FrameAt(runEnd));
        if (to - from < micRec.Rate) return new SpeakerReading(id, false, 0, 0, 0, false, "The microphone did not record anything.");
        Detection heard = ChirpDetector.Find(micAll.AsSpan(from, to - from), micRec.Rate);
        double micTime = micRec.TimeOf(from + heard.Frame);

        // When did Windows mix it for this device (as seen by a loopback recording)?
        Detection mixed = ChirpDetector.Find(loopRec.Snapshot(), loopRec.Rate);
        double mixTime = mixed.Found ? loopRec.TimeOf(mixed.Frame) : double.NaN;
        bool loopOk = mixed.Found && !double.IsNaN(mixTime);

        double stream = micTime - streamTime;
        double direct = loopOk ? micTime - mixTime : stream;

        if (!heard.Found || double.IsNaN(micTime))
            return new SpeakerReading(id, false, direct, stream, heard.Snr, loopOk, "The microphone could not hear this speaker.");
        if (stream < -0.05 || stream > PreRollSeconds + TailSeconds)
            return new SpeakerReading(id, false, direct, stream, heard.Snr, loopOk, "The measurement did not make sense.");
        return new SpeakerReading(id, true, direct, stream, heard.Snr, loopOk, null);
    }
}
