using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using SoundSplit.Core;

namespace SoundSplit.Audio;

/// <summary>A speaker to copy audio to, and how long to hold the audio back before it plays there.</summary>
internal sealed record SinkRequest(string DeviceId, double DelaySeconds);

internal sealed record SinkStatus(
    string DeviceId,
    double DelaySeconds,
    double MinDelaySeconds,
    double ErrorSeconds,
    double AdjustPpm,
    long Reseeks,
    long Starved,
    long Underruns,
    bool Locked,
    string? Fault);

/// <summary>
/// Copies whatever Windows is playing on one device (the leader) to other devices, each held
/// back by its own precisely maintained delay.
///
/// The leader is the Windows default output and plays untouched. Its output is recorded with a
/// loopback capture into a few seconds of shared history, and every other device reads from
/// that history at its own offset through a <see cref="SyncedReader"/>.
/// </summary>
internal sealed class ShareEngine : IDisposable
{
    private sealed class Sink
    {
        public required string Id;
        public required RenderStream Stream;
        public required SyncedReader Reader;
        public double Requested;
        public string? Fault;
    }

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<Sink> _sinks = new();
    private readonly CaptureStream _capture;
    private readonly RenderStream _keepAlive;
    private readonly StereoRing _ring;
    private readonly Timeline _timeline;
    private readonly int _sourceRate;
    private readonly object _recordGate = new();
    private volatile bool _mute;
    private float[]? _injectPattern;
    private long _injectStart = -1;
    private float[] _scratch = new float[9600];
    private List<float>? _record;
    private long _recordStart, _recordNext;
    private int _packetFrames;
    private float _peak;
    private long _gaps;
    private bool _disposed;

    /// <param name="leaderId">The device Windows plays to directly (must be the Windows default output).</param>
    /// <param name="lowLatency">Run the devices at their shortest period; costs a little CPU, saves about 20 ms of minimum delay.</param>
    /// <param name="mute">Run everything but send silence. Used to test on real hardware without making a sound.</param>
    public ShareEngine(string leaderId, IEnumerable<SinkRequest> sinks, bool lowLatency, bool mute = false)
    {
        LeaderId = leaderId;
        LowLatency = lowLatency;
        _mute = mute;
        try
        {
            using MMDevice leader = _enumerator.GetDevice(leaderId);

            // Windows stops handing out loopback packets when nothing is playing, which would stall
            // the clock everything else is measured against. A silent stream keeps it running, and
            // in low-latency mode it is also what makes the leader's audio engine tick faster.
            _keepAlive = new RenderStream(leader, lowLatency, static (dst, max, _, _) =>
            {
                dst.Slice(0, max * 2).Clear();
                return max;
            }, "SoundSplit keep-alive");
            _keepAlive.Faulted += ex => LeaderFaulted?.Invoke(ex);

            _capture = new CaptureStream(leader, loopback: true, raw: false, OnCapture, "SoundSplit capture");
            _capture.Faulted += ex => LeaderFaulted?.Invoke(ex);
            _sourceRate = _capture.SampleRate;
            _packetFrames = (int)Math.Round((_keepAlive.IsLowLatency ? _keepAlive.PeriodSeconds : _capture.PeriodSeconds) * _sourceRate);
            _ring = new StereoRing(_sourceRate * 4);
            _timeline = new Timeline(_sourceRate);

            foreach (SinkRequest request in sinks)
            {
                if (request.DeviceId == leaderId) continue;
                using MMDevice device = _enumerator.GetDevice(request.DeviceId);
                Sink? sink = null;
                var stream = new RenderStream(device, lowLatency,
                    (dst, max, now, queued) => Render(sink!, dst, max, now, queued), "SoundSplit output");
                var reader = new SyncedReader(_ring, _timeline, _sourceRate, stream.SampleRate);
                sink = new Sink { Id = request.DeviceId, Stream = stream, Reader = reader, Requested = request.DelaySeconds };
                reader.DelaySeconds = Math.Max(request.DelaySeconds, MinDelay(sink));
                stream.PositionReported = reader.ReportPosition;
                stream.Faulted += ex =>
                {
                    sink.Fault = ex.Message;
                    SinkFaulted?.Invoke(sink.Id, ex);
                };
                _sinks.Add(sink);
            }

            _keepAlive.Start();
            _capture.Start();
            foreach (Sink s in _sinks) s.Stream.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string LeaderId { get; }
    public bool LowLatency { get; }
    public int SourceRate => _sourceRate;

    /// <summary>Whether the leader really is running at a short period (the driver may not support it).</summary>
    public bool LeaderIsLowLatency => _keepAlive.IsLowLatency;

    /// <summary>Loudest sample captured recently (0..1); decays between reads.</summary>
    public float Peak => Volatile.Read(ref _peak);

    /// <summary>Number of times capture skipped ahead (Windows delivered nothing for a while).</summary>
    public long Gaps => Interlocked.Read(ref _gaps);

    /// <summary>Longest wait between capture packets so far.</summary>
    public double WorstCaptureIntervalSeconds => _capture.WorstIntervalSeconds;

    public double CapturePacketSeconds => (double)Volatile.Read(ref _packetFrames) / _sourceRate;

    /// <summary>The leader stopped working (unplugged, switched off): sharing cannot continue.</summary>
    public event Action<Exception>? LeaderFaulted;

    /// <summary>One of the other devices stopped working; the rest carry on.</summary>
    public event Action<string, Exception>? SinkFaulted;

    /// <summary>
    /// The shortest delay a device can be given: a frame cannot be sent on before the packet it
    /// arrived in is complete, and it has to be queued a little ahead of when it is played.
    /// </summary>
    private double MinDelay(Sink sink) => CapturePacketSeconds + sink.Stream.TargetQueueSeconds + 0.004;

    /// <summary>The shortest delay any output of a typical device can be given at normal (10 ms) periods.</summary>
    public const double TypicalMinDelay = 0.034;

    /// <summary>Send silence to the other speakers while everything else keeps running.</summary>
    public bool Mute
    {
        get => _mute;
        set => _mute = value;
    }

    /// <summary>
    /// Test hook: mixes a mono signal into the captured audio, so the other speakers play it
    /// (after their delay) while the leader stays silent. <see cref="InjectStart"/> then gives
    /// the frame it began at.
    /// </summary>
    public void Inject(float[] mono)
    {
        Volatile.Write(ref _injectStart, -1);
        Volatile.Write(ref _injectPattern, mono);
    }

    public long InjectStart => Volatile.Read(ref _injectStart);

    /// <summary>Test hook: start keeping a mono copy of what is captured from the leader.</summary>
    public void StartRecording()
    {
        lock (_recordGate)
        {
            _record = new List<float>(_sourceRate * 4);
            _recordStart = -1;
        }
    }

    /// <summary>Returns the recording and the frame number of its first sample.</summary>
    public (float[] Mono, long StartFrame) StopRecording()
    {
        lock (_recordGate)
        {
            float[] mono = _record?.ToArray() ?? Array.Empty<float>();
            _record = null;
            return (mono, _recordStart);
        }
    }

    /// <summary>When the leader's audio engine produced the given captured frame.</summary>
    public double TimeOfFrame(double frame) => _timeline.TimeAt(frame);

    public void SetDelay(string deviceId, double seconds)
    {
        foreach (Sink s in _sinks)
            if (s.Id == deviceId) s.Requested = seconds;
    }

    public IReadOnlyList<SinkStatus> Status() => _sinks.Select(s => new SinkStatus(
        s.Id,
        s.Reader.DelaySeconds,
        MinDelay(s),
        s.Reader.ErrorSeconds,
        s.Reader.Adjust * 1e6,
        s.Reader.Reseeks,
        s.Reader.Starved,
        s.Stream.Underruns,
        s.Reader.SinkClock.IsLocked,
        s.Fault)).ToList();

    private void OnCapture(ReadOnlySpan<float> stereo, int frames, double time)
    {
        // Track the packet size actually delivered; it sets how soon audio can be passed on.
        int packet = Volatile.Read(ref _packetFrames);
        if (frames > packet) Volatile.Write(ref _packetFrames, frames);

        long at = _ring.WriteIndex;
        if (_timeline.IsLocked)
        {
            double early = _timeline.TimeAt(at);
            double gap = time - early;
            if (gap > 0.015)
            {
                // Windows delivered nothing for a while (the device went idle). Keep the frame
                // counter in step with real time by accounting for the silence that was skipped.
                long missing = (long)Math.Round(gap * _sourceRate);
                _ring.WriteSilence(Math.Min(missing, _ring.Capacity * 8L));
                at = _ring.WriteIndex;
                Interlocked.Increment(ref _gaps);
                if (gap > 0.25) _timeline.Reset();
            }
            else if (gap < -0.015)
            {
                _timeline.Reset();
                Interlocked.Increment(ref _gaps);
            }
        }
        _timeline.Add(time, at);

        float[]? pattern = Volatile.Read(ref _injectPattern);
        if (pattern != null)
        {
            long start = Volatile.Read(ref _injectStart);
            if (start < 0)
            {
                start = at;
                Volatile.Write(ref _injectStart, start);
            }
            long offset = at - start;
            if (offset >= pattern.Length)
            {
                Volatile.Write(ref _injectPattern, null);
            }
            else
            {
                if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];
                stereo.CopyTo(_scratch);
                for (int k = 0; k < frames && offset + k < pattern.Length; k++)
                {
                    _scratch[2 * k] += pattern[offset + k];
                    _scratch[2 * k + 1] += pattern[offset + k];
                }
                stereo = _scratch.AsSpan(0, frames * 2);
            }
        }

        lock (_recordGate)
        {
            if (_record != null)
            {
                if (_recordStart < 0) _recordStart = _recordNext = at;
                for (long gapFrame = _recordNext; gapFrame < at; gapFrame++) _record.Add(0);
                for (int k = 0; k < frames; k++) _record.Add((stereo[2 * k] + stereo[2 * k + 1]) * 0.5f);
                _recordNext = at + frames;
            }
        }

        _ring.Write(stereo);

        float peak = Volatile.Read(ref _peak) * 0.9f;
        for (int i = 0; i < stereo.Length; i += 8)
        {
            float v = Math.Abs(stereo[i]);
            if (v > peak) peak = v;
        }
        Volatile.Write(ref _peak, peak);
    }

    private int Render(Sink sink, Span<float> dst, int max, double now, int queued)
    {
        double delay = Math.Max(sink.Requested, MinDelay(sink));
        if (Math.Abs(delay - sink.Reader.DelaySeconds) > 1e-5) sink.Reader.DelaySeconds = delay;

        int n = sink.Reader.Read(dst, max, now, queued);
        if (_mute && n > 0) dst.Slice(0, n * 2).Clear();
        return n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Sink s in _sinks)
        {
            try { s.Stream.Dispose(); } catch { }
        }
        try { _capture?.Dispose(); } catch { }
        try { _keepAlive?.Dispose(); } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
