using System;
using System.Threading;
using NAudio.CoreAudioApi;

namespace SoundSplit.Audio;

/// <summary>Fills <paramref name="stereo"/> with up to <paramref name="maxFrames"/> frames and returns how many it wrote.</summary>
/// <param name="now">Current time (seconds, <see cref="AudioTime"/>).</param>
/// <param name="queuedFrames">Frames already handed to the device and not yet played.</param>
internal delegate int RenderHandler(Span<float> stereo, int maxFrames, double now, int queuedFrames);

/// <summary>Plays to one device on its own thread, pulling audio from a handler a few milliseconds ahead of need.</summary>
internal sealed class RenderStream : IDisposable
{
    private readonly AudioClient _client;
    private readonly AudioRenderClient _render;
    private readonly AudioClockClient _clock;
    private readonly DeviceFormat _format;
    private readonly RenderHandler _handler;
    private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset);
    private readonly Thread _thread;
    private readonly double _clockToFrames;
    private readonly float[] _buffer;
    private volatile bool _stop;
    private bool _disposed;
    private long _underruns;

    /// <param name="lowLatency">Ask the device for its shortest period (often 3 ms instead of 10) where the driver allows it.</param>
    public RenderStream(MMDevice device, bool lowLatency, RenderHandler handler, string name)
    {
        _handler = handler;
        AudioClient? client = null;
        int period = 0;

        if (lowLatency)
        {
            try
            {
                client = device.CreateAudioClient();
                if (!client.SupportsAudioClient3) throw new NotSupportedException();
                var mix = client.MixFormat;
                uint frames = client.GetSharedModeEnginePeriod(mix).ChooseLowestLatencyPeriod();
                client.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, frames, mix, Guid.Empty);
                period = (int)frames;
                IsLowLatency = true;
            }
            catch
            {
                client?.Dispose();
                client = null;
            }
        }
        if (client == null)
        {
            client = device.CreateAudioClient();
            try
            {
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, 600_000, 0, client.MixFormat, Guid.Empty);
                period = (int)Math.Round(client.DefaultDevicePeriod * 1e-7 * client.MixFormat.SampleRate);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        _client = client;
        _format = new DeviceFormat(client.MixFormat);
        _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        _render = _client.AudioRenderClient;
        _clock = _client.AudioClockClient;
        _clockToFrames = (double)_format.SampleRate / _clock.Frequency;

        PeriodFrames = Math.Max(1, period);
        BufferFrames = _client.BufferSize;
        // Keep two periods queued (and never less than 6 ms): enough to ride out a late wake-up, no more.
        TargetQueueFrames = Math.Min(BufferFrames, Math.Max(PeriodFrames * 2, _format.SampleRate * 6 / 1000));
        _buffer = new float[BufferFrames * 2];
        _thread = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.Highest };
    }

    public int SampleRate => _format.SampleRate;
    public int Channels => _format.Channels;
    public int PeriodFrames { get; }
    public int BufferFrames { get; }
    public int TargetQueueFrames { get; }
    public bool IsLowLatency { get; }
    public double PeriodSeconds => (double)PeriodFrames / SampleRate;
    public double TargetQueueSeconds => (double)TargetQueueFrames / SampleRate;

    /// <summary>Times the device ran completely dry after it had started (an audible gap).</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Called with (time, frames played so far) each time the device reports its play position.</summary>
    public Action<double, double>? PositionReported { get; set; }

    public event Action<Exception>? Faulted;

    public void Start() => _thread.Start();

    private void Run()
    {
        IntPtr task = AudioTime.EnterAudioThread();
        try
        {
            ulong lastPosition = 0;
            bool playing = false;
            _client.Start();
            while (!_stop)
            {
                if (!_event.WaitOne(100)) continue;
                if (_stop) break;

                double now = AudioTime.Now;
                int queued = _client.CurrentPadding;
                if (playing && queued == 0) Interlocked.Increment(ref _underruns);

                if (PositionReported != null)
                {
                    _clock.GetPosition(out ulong position, out ulong qpc);
                    if (position > lastPosition)
                    {
                        lastPosition = position;
                        PositionReported(AudioTime.FromWasapi((long)qpc), position * _clockToFrames);
                    }
                }

                int want = Math.Min(BufferFrames - queued, TargetQueueFrames - queued);
                if (want <= 0) continue;

                int got = _handler(_buffer.AsSpan(0, want * 2), want, now, queued);
                if (got <= 0) continue;

                using var lease = _render.GetBufferLease(got, _format.BytesPerFrame);
                _format.FromStereo(_buffer, got, lease.Buffer);
                lease.Release(got);
                playing = true;
            }
        }
        catch (Exception ex)
        {
            if (!_stop) Faulted?.Invoke(ex);
        }
        finally
        {
            try { _client.Stop(); } catch { }
            AudioTime.LeaveAudioThread(task);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _event.Set();
        if (_thread.IsAlive) _thread.Join(1000);
        try { _clock.Dispose(); } catch { }
        try { _render.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        _event.Dispose();
    }
}
