using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundSplit.Audio;

/// <summary>Converts between a device's own sample layout and the stereo float the app works in.</summary>
internal sealed class DeviceFormat
{
    private enum Kind { Float32, Int16, Int24, Int32 }

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly Kind _kind;
    private readonly int _bytesPerSample;

    public DeviceFormat(WaveFormat format)
    {
        Wave = format;
        SampleRate = format.SampleRate;
        Channels = format.Channels;
        BytesPerFrame = format.BlockAlign;
        _bytesPerSample = format.BlockAlign / Math.Max(1, format.Channels);

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm;
        if (format is WaveFormatExtensible ext)
        {
            isFloat = ext.SubFormat == FloatSubFormat;
            isPcm = ext.SubFormat == PcmSubFormat;
        }

        if (isFloat && _bytesPerSample == 4) _kind = Kind.Float32;
        else if (isPcm && _bytesPerSample == 2) _kind = Kind.Int16;
        else if (isPcm && _bytesPerSample == 3) _kind = Kind.Int24;
        else if (isPcm && _bytesPerSample == 4) _kind = Kind.Int32;
        else throw new NotSupportedException($"Unsupported audio format: {format}");
    }

    public WaveFormat Wave { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BytesPerFrame { get; }

    private float Read(ReadOnlySpan<byte> s) => _kind switch
    {
        Kind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
        Kind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
        Kind.Int24 => (s[0] | (s[1] << 8) | ((sbyte)s[2] << 16)) / 8388608f,
        _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
    };

    private void Write(Span<byte> d, float v)
    {
        switch (_kind)
        {
            case Kind.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(d, v);
                break;
            case Kind.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(d, (short)Math.Clamp(MathF.Round(v * 32767f), -32768f, 32767f));
                break;
            case Kind.Int24:
                int i = (int)Math.Clamp(MathF.Round(v * 8388607f), -8388608f, 8388607f);
                d[0] = (byte)i;
                d[1] = (byte)(i >> 8);
                d[2] = (byte)(i >> 16);
                break;
            default:
                BinaryPrimitives.WriteInt32LittleEndian(d, (int)Math.Clamp(Math.Round(v * 2147483647.0), -2147483648.0, 2147483647.0));
                break;
        }
    }

    /// <summary>Device bytes to stereo float. Surround is folded down; mono is doubled.</summary>
    public void ToStereo(ReadOnlySpan<byte> src, int frames, Span<float> dst)
    {
        if (_kind == Kind.Float32 && Channels == 2)
        {
            MemoryMarshal.Cast<byte, float>(src.Slice(0, frames * 8)).CopyTo(dst);
            return;
        }

        int bps = _bytesPerSample;
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<byte> frame = src.Slice(f * BytesPerFrame, BytesPerFrame);
            float l = Read(frame);
            float r = Channels > 1 ? Read(frame.Slice(bps)) : l;
            if (Channels >= 6)
            {
                // 5.1 / 7.1: front left, front right, centre, LFE, then surrounds.
                float c = Read(frame.Slice(2 * bps)) * 0.707f;
                l += c + Read(frame.Slice(4 * bps)) * 0.5f;
                r += c + Read(frame.Slice(5 * bps)) * 0.5f;
            }
            dst[2 * f] = l;
            dst[2 * f + 1] = r;
        }
    }

    /// <summary>Stereo float to device bytes. Extra channels are left silent.</summary>
    public void FromStereo(ReadOnlySpan<float> src, int frames, Span<byte> dst)
    {
        if (_kind == Kind.Float32 && Channels == 2)
        {
            MemoryMarshal.AsBytes(src.Slice(0, frames * 2)).CopyTo(dst);
            return;
        }

        int bps = _bytesPerSample;
        dst.Slice(0, frames * BytesPerFrame).Clear();
        for (int f = 0; f < frames; f++)
        {
            Span<byte> frame = dst.Slice(f * BytesPerFrame, BytesPerFrame);
            float l = src[2 * f], r = src[2 * f + 1];
            if (Channels == 1)
            {
                Write(frame, (l + r) * 0.5f);
            }
            else
            {
                Write(frame, l);
                Write(frame.Slice(bps), r);
            }
        }
    }
}
