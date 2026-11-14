using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoundSplit.Audio;

/// <summary>One clock for everything: seconds on the performance counter, which is also what Windows audio timestamps use.</summary>
internal static class AudioTime
{
    private static readonly double TickSeconds = 1.0 / Stopwatch.Frequency;

    public static double Now => Stopwatch.GetTimestamp() * TickSeconds;

    /// <summary>Converts a WASAPI timestamp (performance counter expressed in 100 ns units) to seconds.</summary>
    public static double FromWasapi(long hundredNanos) => hundredNanos * 1e-7;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>Asks Windows to schedule the calling thread like an audio thread, so it is not held up by other work.</summary>
    public static IntPtr EnterAudioThread()
    {
        try
        {
            uint index = 0;
            return AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void LeaveAudioThread(IntPtr handle)
    {
        try
        {
            if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle);
        }
        catch
        {
        }
    }
}
