using System;
using System.Threading;
using NAudio.CoreAudioApi;

namespace SoundSplit.Audio;

/// <param name="stereo">Captured audio, interleaved stereo.</param>
/// <param name="frames">Number of frames in <paramref name="stereo"/>.</param>
/// <param name="time">When the first frame was captured (seconds, <see cref="AudioTime"/>).</param>
internal delegate void CaptureHandler(ReadOnlySpan<float> stereo, int frames, double time);

/// <summary>
/// Records from a device on its own thread: either what a speaker is playing (loopback) or a microphone.
/// </summary>
internal sealed class CaptureStream : IDisposable
{
    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private readonly DeviceFormat _format;
    private readonly CaptureHandler _handler;
    private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset);
    private readonly Thread _thread;
    private volatile bool _stop;
    private bool _disposed;
    private float[] _buffer = new float[9600];

    /// <param name="raw">Ask Windows not to "enhance" the signal (echo cancelling, noise removal). Needed to hear our own speakers through a mic.</param>
    public CaptureStream(MMDevice device, bool loopback, bool raw, CaptureHandler handler, string name)
    {
        _handler = handler;
        var flags = AudioClientStreamFlags.EventCallback | (loopback ? AudioClientStreamFlags.Loopback : 0);

        AudioClient? client = null;
        if (raw)
        {
            try
            {
                client = device.CreateAudioClient();
                client.SetClientProperties(AudioStreamCategory.Other, AudioClientStreamOptions.Raw);
                client.Initialize(AudioClientShareMode.Shared, flags, 2_000_000, 0, client.MixFormat, Guid.Empty);
                IsRaw = true;
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
                client.Initialize(AudioClientShareMode.Shared, flags, 2_000_000, 0, client.MixFormat, Guid.Empty);
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
        _capture = _client.AudioCaptureClient;
        PeriodSeconds = _client.DefaultDevicePeriod * 1e-7;
        _thread = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.Highest };
    }

    public int SampleRate => _format.SampleRate;

    public int Channels => _format.Channels;

    /// <summary>How often Windows hands over a packet, unless another stream makes the device run faster.</summary>
    public double PeriodSeconds { get; }

    public bool IsRaw { get; }

    /// <summary>Largest gap seen between successive packets, a measure of how punctual capture is.</summary>
    public double WorstIntervalSeconds { get; private set; }

    public event Action<Exception>? Faulted;

    public void Start() => _thread.Start();

    private void Run()
    {
        IntPtr task = AudioTime.EnterAudioThread();
        try
        {
            _client.Start();
            double last = double.NaN;   // the wait for the very first packet is start-up, not a stall
            while (!_stop)
            {
                // The timeout also covers devices that never signal for loopback: we just poll.
                _event.WaitOne(20);
                if (_stop) break;

                while (_capture.GetNextPacketSize() > 0)
                {
                    using var lease = _capture.GetBufferLease(_format.BytesPerFrame);
                    int frames = lease.FrameCount;
                    if (frames <= 0) continue;

                    double now = AudioTime.Now;
                    if (!double.IsNaN(last) && now - last > WorstIntervalSeconds) WorstIntervalSeconds = now - last;
                    last = now;

                    double time = AudioTime.FromWasapi(lease.QPCPosition);
                    bool badStamp = (lease.Flags & AudioClientBufferFlags.TimestampError) != 0;
                    if (badStamp || Math.Abs(time - now) > 1.0) time = now - (double)frames / _format.SampleRate;

                    if (_buffer.Length < frames * 2) _buffer = new float[frames * 2];
                    Span<float> stereo = _buffer.AsSpan(0, frames * 2);
                    if ((lease.Flags & AudioClientBufferFlags.Silent) != 0) stereo.Clear();
                    else _format.ToStereo(lease.Buffer, frames, stereo);

                    _handler(stereo, frames, time);
                }
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
        try { _capture.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        _event.Dispose();
    }
}
