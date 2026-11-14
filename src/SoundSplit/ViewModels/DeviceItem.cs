using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using SoundSplit.Audio;

namespace SoundSplit.ViewModels;

internal abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>How a speaker's status line is coloured.</summary>
internal enum StatusTone { Neutral, Good, Caution, Bad }

/// <summary>One speaker in the list.</summary>
internal sealed class DeviceItem : Observable, IDisposable
{
    private readonly MMDevice? _device;
    private readonly AudioEndpointVolume? _endpoint;
    private readonly Dispatcher _dispatcher;
    private readonly Action<DeviceItem> _selectionChanged;
    private readonly Action<DeviceItem> _trimChanged;
    private bool _selected, _isDefault, _muted, _canTrim, _fromDevice;
    private string _status = "";
    private StatusTone _tone;
    private double _volume, _trimMs;

    public DeviceItem(OutputInfo info, MMDevice? device, Action<DeviceItem> selectionChanged, Action<DeviceItem> trimChanged)
    {
        Info = info;
        _device = device;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _selectionChanged = selectionChanged;
        _trimChanged = trimChanged;
        ToggleMute = new RelayCommand(() => IsMuted = !IsMuted);
        ResetTrim = new RelayCommand(() => TrimMs = 0);

        try
        {
            _endpoint = device?.AudioEndpointVolume;
            if (_endpoint != null)
            {
                _volume = Math.Round(_endpoint.MasterVolumeLevelScalar * 100);
                _muted = _endpoint.Mute;
                _endpoint.OnVolumeNotification += OnDeviceVolume;
            }
        }
        catch
        {
            _endpoint = null;
        }
    }

    public OutputInfo Info { get; }
    public string Id => Info.Id;
    public string Name => Info.Name;

    /// <summary>The name without Windows' "Speakers (...)" wrapper, for use in sentences.</summary>
    public string ShortName
    {
        get
        {
            string n = Info.Name;
            int open = n.IndexOf('(');
            if (open > 0 && n.EndsWith(')'))
            {
                string inner = n.Substring(open + 1, n.Length - open - 2).Trim();
                int extra = inner.IndexOf(" (", StringComparison.Ordinal);
                if (extra > 0) inner = inner.Substring(0, extra);
                if (inner.Length > 0) return inner;
            }
            return n;
        }
    }

    /// <summary>Segoe Fluent Icons glyph for this kind of device.</summary>
    public string Glyph => Info.Kind switch
    {
        SpeakerKind.Headphones => "",
        SpeakerKind.Display => "",
        _ => "",
    };

    public bool IsBluetooth => Info.IsBluetooth;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) _selectionChanged(this);
        }
    }

    /// <summary>Sets the switch without telling the owner (used when loading).</summary>
    public void SetSelectedQuietly(bool value)
    {
        _selected = value;
        Raise(nameof(IsSelected));
    }

    public bool IsDefault
    {
        get => _isDefault;
        set => Set(ref _isDefault, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public StatusTone Tone
    {
        get => _tone;
        set => Set(ref _tone, value);
    }

    /// <summary>Volume 0-100. This is the device's own Windows volume, the same one the taskbar shows.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _volume, value)) return;
            Raise(nameof(VolumeGlyph));
            Raise(nameof(VolumeText));
            if (_fromDevice || _endpoint == null) return;
            try
            {
                _endpoint.MasterVolumeLevelScalar = (float)(value / 100);
                if (value > 0 && _muted) IsMuted = false;
            }
            catch
            {
            }
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            Raise(nameof(VolumeGlyph));
            if (_fromDevice || _endpoint == null) return;
            try
            {
                _endpoint.Mute = value;
            }
            catch
            {
            }
        }
    }

    public string VolumeText => ((int)_volume).ToString();

    public string VolumeGlyph => _muted || _volume == 0 ? "" : _volume < 34 ? "" : _volume < 67 ? "" : "";

    /// <summary>The user's own timing correction: positive plays this speaker later.</summary>
    public double TrimMs
    {
        get => _trimMs;
        set
        {
            value = Math.Clamp(Math.Round(value), -300, 300);
            if (!Set(ref _trimMs, value)) return;
            Raise(nameof(TrimText));
            _trimChanged(this);
        }
    }

    public void SetTrimQuietly(double value)
    {
        _trimMs = Math.Clamp(Math.Round(value), -300, 300);
        Raise(nameof(TrimMs));
        Raise(nameof(TrimText));
    }

    public string TrimText => _trimMs == 0 ? "0 ms" : _trimMs > 0 ? $"+{_trimMs:0} ms" : $"−{-_trimMs:0} ms";

    /// <summary>Timing can only be adjusted on a speaker the app is feeding.</summary>
    public bool CanTrim
    {
        get => _canTrim;
        set => Set(ref _canTrim, value);
    }

    public ICommand ToggleMute { get; }
    public ICommand ResetTrim { get; }

    private void OnDeviceVolume(AudioVolumeNotificationData data)
    {
        float volume = data.MasterVolume;
        bool muted = data.Muted;
        _dispatcher.BeginInvoke(() =>
        {
            _fromDevice = true;
            try
            {
                Volume = volume * 100;
                IsMuted = muted;
            }
            finally
            {
                _fromDevice = false;
            }
        });
    }

    public void Dispose()
    {
        try
        {
            if (_endpoint != null) _endpoint.OnVolumeNotification -= OnDeviceVolume;
        }
        catch
        {
        }
        try
        {
            _device?.Dispose();
        }
        catch
        {
        }
    }
}
