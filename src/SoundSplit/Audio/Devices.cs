using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace SoundSplit.Audio;

internal enum SpeakerKind { Speakers, Headphones, Display, Other }

internal sealed record OutputInfo(string Id, string Name, bool IsBluetooth, SpeakerKind Kind);

internal sealed record MicInfo(string Id, string Name);

/// <summary>Finds audio devices and switches the Windows default output.</summary>
internal static class Devices
{
    public static List<OutputInfo> Outputs(MMDeviceEnumerator enumerator)
    {
        var list = new List<OutputInfo>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    list.Add(Describe(device));
                }
                catch
                {
                    // A device that vanishes mid-enumeration is simply not listed.
                }
            }
        }
        return list;
    }

    public static OutputInfo Describe(MMDevice device)
    {
        bool bluetooth = false;
        var kind = SpeakerKind.Speakers;
        try
        {
            // There is no single "this is Bluetooth" flag. Classic drivers sit on the BTHENUM bus;
            // laptops that offload Bluetooth audio to the sound chip only mention it in the
            // hardware path or the driver's name. So look through every text property.
            var props = device.Properties;
            for (int i = 0; i < props.Count && !bluetooth; i++)
            {
                try
                {
                    if (props[i].Value is string text)
                    {
                        bluetooth = text.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        try
        {
            if (device.Properties.TryGetValue(PropertyKeys.PKEY_AudioEndpoint_FormFactor, out uint form))
            {
                kind = form switch
                {
                    3 or 5 => SpeakerKind.Headphones,   // headphones, headset
                    9 or 10 => SpeakerKind.Display,     // HDMI / DisplayPort audio
                    1 or 0 or 8 => SpeakerKind.Speakers,
                    _ => SpeakerKind.Other,
                };
            }
        }
        catch
        {
        }
        return new OutputInfo(device.ID, device.FriendlyName, bluetooth, kind);
    }

    public static List<MicInfo> Microphones(MMDeviceEnumerator enumerator)
    {
        var list = new List<MicInfo>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    list.Add(new MicInfo(device.ID, device.FriendlyName));
                }
                catch
                {
                }
            }
        }
        return list;
    }

    public static string? DefaultOutputId(MMDeviceEnumerator enumerator) => DefaultId(enumerator, DataFlow.Render);

    public static string? DefaultMicId(MMDeviceEnumerator enumerator) => DefaultId(enumerator, DataFlow.Capture);

    private static string? DefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Makes a device the Windows default output, exactly as picking it in the taskbar's sound
    /// menu does. Windows has no documented call for this; this is the same interface the
    /// Settings app itself uses, and has been stable since Windows 7.
    /// </summary>
    public static void SetDefaultOutput(string deviceId)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, 0));   // system sounds and apps
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, 1));   // music and video
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
    }
}
