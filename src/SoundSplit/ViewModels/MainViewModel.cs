using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using SoundSplit.Audio;
using SoundSplit.Core;
using SoundSplit.Services;

namespace SoundSplit.ViewModels;

internal enum BarKind { None, Info, Success, Caution, Error }

internal sealed class MainViewModel : Observable, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient? _notifications;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _syncTimer;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Stopwatch _syncClock = new();

    private ShareEngine? _engine;
    private string? _leaderId;
    private string? _originalDefault;
    private Dictionary<string, double> _ideal = new();
    private bool _estimated;
    private CancellationTokenSource? _syncCancel;
    private double _syncSeconds = 1;
    private bool _loading;

    private bool _busy, _syncing;
    private string _footer = "", _syncDetail = "", _syncStep = "", _barTitle = "", _barMessage = "";
    private double _syncProgress;
    private BarKind _bar;
    private MicInfo? _mic;
    private StatusTone _footerTone;

    /// <summary>Test hook: run for real but send silence to the speakers.</summary>
    public bool MuteOutput { get; set; }

    public MainViewModel()
    {
        ToggleShare = new RelayCommand(() => _ = ToggleShareAsync());
        Sync = new RelayCommand(() => _ = SyncAsync());
        DismissBar = new RelayCommand(() => Bar = BarKind.None);
        OpenSoundSettings = new RelayCommand(() => Open("ms-settings:sound"));
        OpenMicSettings = new RelayCommand(() => Open("ms-settings:privacy-microphone"));

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _statusTimer.Tick += (_, _) => UpdateTexts();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
        };
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _syncTimer.Tick += (_, _) => SyncProgress = Math.Min(100, _syncClock.Elapsed.TotalSeconds / _syncSeconds * 100);

        // If the app was killed while sharing, Windows may still be pointed at the speaker we chose.
        if (_settings.PendingRestoreDefault is { } pending)
        {
            TrySetDefault(pending);
            _settings.PendingRestoreDefault = null;
            _settings.Save();
        }

        Refresh();

        try
        {
            _notifications = _enumerator.CreateNotificationClient(true);
            _notifications.DeviceAdded += (_, _) => QueueRefresh();
            _notifications.DeviceRemoved += (_, _) => QueueRefresh();
            _notifications.DeviceStateChanged += (_, _) => QueueRefresh();
            _notifications.DefaultDeviceChanged += (_, _) => QueueRefresh();
        }
        catch (Exception ex)
        {
            Log.Write("Device notifications unavailable: " + ex.Message);
        }
    }

    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<MicInfo> Mics { get; } = new();

    public ICommand ToggleShare { get; }
    public ICommand Sync { get; }
    public ICommand DismissBar { get; }
    public ICommand OpenSoundSettings { get; }
    public ICommand OpenMicSettings { get; }

    public bool IsSharing => _engine != null;

    public bool IsSyncing
    {
        get => _syncing;
        private set
        {
            if (Set(ref _syncing, value)) RaiseState();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) RaiseState();
        }
    }

    private int SelectedCount => Devices.Count(d => d.IsSelected);

    public bool CanShare => !IsBusy && !IsSyncing && (IsSharing || SelectedCount >= 2);
    public bool CanSync => !IsBusy && (IsSyncing || (SelectedCount >= 2 && Mics.Count > 0));
    public bool HasNoDevices => Devices.Count == 0;
    public bool HasMics => Mics.Count > 0;
    public string ShareText => IsSharing ? "Stop sharing" : "Start sharing";
    public string ShareGlyph => IsSharing ? "" : "";
    public string SyncButtonText => IsSyncing ? "Cancel" : IsCalibrated ? "Sync again" : "Sync now";

    /// <summary>Every switched-on speaker has a microphone measurement from the same session.</summary>
    public bool IsCalibrated
    {
        get
        {
            var selected = Devices.Where(d => d.IsSelected).ToList();
            return selected.Count >= 2 && selected.All(d => _settings.Calibration.ContainsKey(d.Id));
        }
    }

    /// <summary>Draw attention to the Sync button: there are speakers to sync and they have not been measured.</summary>
    public bool SyncIsSuggested => !IsSyncing && SelectedCount >= 2 && !IsCalibrated && Mics.Count > 0;

    public string FooterText
    {
        get => _footer;
        private set => Set(ref _footer, value);
    }

    public StatusTone FooterTone
    {
        get => _footerTone;
        private set => Set(ref _footerTone, value);
    }

    public string SyncDetail
    {
        get => _syncDetail;
        private set => Set(ref _syncDetail, value);
    }

    public string SyncStep
    {
        get => _syncStep;
        private set => Set(ref _syncStep, value);
    }

    public double SyncProgress
    {
        get => _syncProgress;
        private set => Set(ref _syncProgress, value);
    }

    public BarKind Bar
    {
        get => _bar;
        private set
        {
            if (Set(ref _bar, value)) Raise(nameof(BarOffersMicSettings));
        }
    }

    public string BarTitle
    {
        get => _barTitle;
        private set => Set(ref _barTitle, value);
    }

    public string BarMessage
    {
        get => _barMessage;
        private set => Set(ref _barMessage, value);
    }

    private bool _barMic;
    public bool BarOffersMicSettings => _barMic && Bar != BarKind.None;

    public MicInfo? SelectedMic
    {
        get => _mic;
        set
        {
            if (!Set(ref _mic, value) || _loading || value == null) return;
            _settings.MicId = value.Id;
            _settings.Save();
        }
    }

    public bool RestoreDefault
    {
        get => _settings.RestoreDefault;
        set
        {
            if (_settings.RestoreDefault == value) return;
            _settings.RestoreDefault = value;
            _settings.Save();
            Raise();
        }
    }

    private void RaiseState()
    {
        Raise(nameof(IsSharing));
        Raise(nameof(CanShare));
        Raise(nameof(CanSync));
        Raise(nameof(ShareText));
        Raise(nameof(ShareGlyph));
        Raise(nameof(SyncButtonText));
        Raise(nameof(IsCalibrated));
        Raise(nameof(SyncIsSuggested));
        Raise(nameof(HasNoDevices));
        Raise(nameof(HasMics));
    }

    private void ShowBar(BarKind kind, string title, string message, bool offerMicSettings = false)
    {
        _barMic = offerMicSettings;
        BarTitle = title;
        BarMessage = message;
        _bar = BarKind.None;
        Bar = kind;
        Raise(nameof(BarOffersMicSettings));
    }

    private static void Open(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- devices

    private void QueueRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        _loading = true;
        try
        {
            List<OutputInfo> outputs = Devices_Outputs();
            string? def = Audio.Devices.DefaultOutputId(_enumerator);

            foreach (DeviceItem gone in Devices.Where(d => outputs.All(o => o.Id != d.Id)).ToList())
            {
                Devices.Remove(gone);
                gone.Dispose();
            }

            bool firstRun = _settings.Selected.Count == 0 && Devices.Count == 0;
            foreach (OutputInfo info in outputs)
            {
                if (Devices.Any(d => d.Id == info.Id)) continue;
                MMDevice? device = null;
                try
                {
                    device = _enumerator.GetDevice(info.Id);
                }
                catch
                {
                }
                var item = new DeviceItem(info, device, OnSelectionChanged, OnTrimChanged);
                // First run: with exactly two speakers there is only one thing the user can want.
                item.SetSelectedQuietly(firstRun ? outputs.Count == 2 : _settings.Selected.Contains(info.Id));
                if (_settings.TrimMs.TryGetValue(info.Id, out double trim)) item.SetTrimQuietly(trim);
                Devices.Add(item);
            }
            foreach (DeviceItem d in Devices) d.IsDefault = d.Id == def;

            List<MicInfo> mics = Audio.Devices.Microphones(_enumerator);
            if (!mics.Select(m => m.Id).SequenceEqual(Mics.Select(m => m.Id)))
            {
                Mics.Clear();
                foreach (MicInfo m in mics) Mics.Add(m);
            }
            string? wanted = _settings.MicId ?? Audio.Devices.DefaultMicId(_enumerator);
            SelectedMic = Mics.FirstOrDefault(m => m.Id == wanted)
                ?? Mics.FirstOrDefault(m => m.Id == Audio.Devices.DefaultMicId(_enumerator))
                ?? Mics.FirstOrDefault();

            if (_engine != null && _leaderId != null)
            {
                if (Devices.All(d => d.Id != _leaderId))
                    _ = StopSharingAsync("Sharing stopped", "The speaker Windows was playing to was disconnected.", BarKind.Caution);
                else if (def != _leaderId)
                    _ = StopSharingAsync("Sharing stopped", "The Windows output was changed, so the speakers are no longer being fed.", BarKind.Info, restoreDefault: false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Refresh failed: " + ex);
        }
        finally
        {
            _loading = false;
        }
        RaiseState();
        UpdateTexts();
    }

    private List<OutputInfo> Devices_Outputs() => Audio.Devices.Outputs(_enumerator);

    private void OnSelectionChanged(DeviceItem item)
    {
        if (_loading) return;
        _settings.Selected = Devices.Where(d => d.IsSelected).Select(d => d.Id).ToList();
        _settings.Save();
        RaiseState();
        UpdateTexts();
        if (IsSharing) _ = RestartSharingAsync();
    }

    private void OnTrimChanged(DeviceItem item)
    {
        if (_loading) return;
        _settings.TrimMs[item.Id] = item.TrimMs;
        _settings.Save();
        if (_engine != null && _ideal.TryGetValue(item.Id, out double ideal))
            _engine.SetDelay(item.Id, ideal + item.TrimMs / 1000);
    }

    private bool TrySetDefault(string id)
    {
        try
        {
            Audio.Devices.SetDefaultOutput(id);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Could not change the default output: " + ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- sharing

    /// <summary>Decides which speaker Windows plays to directly and how long to hold each of the others back.</summary>
    private (string Leader, Dictionary<string, double> Ideal, bool Estimated) MakePlan(List<DeviceItem> selected)
    {
        string? current = Audio.Devices.DefaultOutputId(_enumerator);

        if (selected.All(d => _settings.Calibration.ContainsKey(d.Id)))
        {
            var measured = selected.Select(d =>
            {
                CalibrationEntry c = _settings.Calibration[d.Id];
                return new SpeakerMeasurement(d.Id, c.Direct, c.Stream);
            }).ToList();
            SyncPlan plan = SyncPlanner.Plan(measured, ShareEngine.TypicalMinDelay, current);
            // Keep the true ideal (even when it is shorter than can be achieved) so the status
            // can say honestly how far off a speaker is.
            SpeakerMeasurement leader = measured.First(m => m.Id == plan.LeaderId);
            var ideal = measured.Where(m => m.Id != plan.LeaderId)
                .ToDictionary(m => m.Id, m => leader.DirectLatency - m.StreamLatency);
            return (plan.LeaderId, ideal, false);
        }

        // Not measured yet. Bluetooth is practically always the slow one, so let Windows play to
        // it directly and hold the others back by a typical Bluetooth delay.
        DeviceItem? def = selected.FirstOrDefault(d => d.Id == current);
        bool anyBluetooth = selected.Any(d => d.IsBluetooth);
        DeviceItem lead = def != null && (def.IsBluetooth || !anyBluetooth)
            ? def
            : selected.FirstOrDefault(d => d.IsBluetooth) ?? selected[0];
        var guess = selected.Where(d => d != lead)
            .ToDictionary(d => d.Id, d => lead.IsBluetooth && !d.IsBluetooth ? 0.170 : 0.0);
        return (lead.Id, guess, true);
    }

    private async Task ToggleShareAsync()
    {
        if (IsBusy || IsSyncing) return;
        if (IsSharing) await StopSharingAsync();
        else await StartSharingAsync();
    }

    private async Task StartSharingAsync()
    {
        var selected = Devices.Where(d => d.IsSelected).ToList();
        if (selected.Count < 2 || _engine != null) return;

        IsBusy = true;
        try
        {
            var (leader, ideal, estimated) = MakePlan(selected);
            string? current = Audio.Devices.DefaultOutputId(_enumerator);
            if (leader != current)
            {
                _originalDefault ??= current;
                if (!TrySetDefault(leader)) throw new InvalidOperationException("Windows would not switch its output to the chosen speaker.");
                _settings.PendingRestoreDefault = _originalDefault;
                _settings.Save();
            }

            // Short device periods only matter when a speaker needs less delay than normal periods allow.
            bool lowLatency = ideal.Values.Any(v => v < ShareEngine.TypicalMinDelay);
            var requests = selected.Where(d => d.Id != leader)
                .Select(d => new SinkRequest(d.Id, ideal[d.Id] + d.TrimMs / 1000)).ToList();
            bool mute = MuteOutput;
            ShareEngine engine = await Task.Run(() => new ShareEngine(leader, requests, lowLatency, mute));

            engine.LeaderFaulted += ex => _dispatcher.BeginInvoke(() =>
            {
                Log.Write("Leader faulted: " + ex.Message);
                if (_engine == engine) _ = StopSharingAsync("Sharing stopped", "The speaker Windows was playing to stopped responding.", BarKind.Caution);
            });
            engine.SinkFaulted += (id, ex) => Log.Write($"Output {id} faulted: {ex.Message}");

            _engine = engine;
            _leaderId = leader;
            _ideal = ideal;
            _estimated = estimated;
            _statusTimer.Start();
            Log.Write($"Sharing: leader {leader}, {requests.Count} follower(s), low latency {lowLatency}, estimated {estimated}");
            if (Bar is BarKind.Error or BarKind.Info) Bar = BarKind.None;
        }
        catch (Exception ex)
        {
            Log.Write("Start failed: " + ex);
            RestoreOriginalDefault();
            ShowBar(BarKind.Error, "Couldn't start sharing", Friendly(ex));
        }
        finally
        {
            IsBusy = false;
            RaiseState();
            UpdateTexts();
        }
    }

    private async Task StopSharingAsync(string? title = null, string? message = null, BarKind kind = BarKind.Info,
        bool restoreDefault = true, bool keepOriginal = false)
    {
        ShareEngine? engine = _engine;
        if (engine == null) return;
        _engine = null;
        _leaderId = null;
        _statusTimer.Stop();
        IsBusy = true;
        try
        {
            await Task.Run(engine.Dispose);
        }
        finally
        {
            if (!keepOriginal)
            {
                if (restoreDefault) RestoreOriginalDefault();
                else ForgetOriginalDefault();
            }
            IsBusy = false;
            if (title != null) ShowBar(kind, title, message ?? "");
            RaiseState();
            UpdateTexts();
        }
    }

    private async Task RestartSharingAsync()
    {
        if (IsBusy) return;
        await StopSharingAsync(keepOriginal: true);
        if (SelectedCount >= 2) await StartSharingAsync();
        else RestoreOriginalDefault();
        RaiseState();
        UpdateTexts();
    }

    private void RestoreOriginalDefault()
    {
        if (_originalDefault != null && _settings.RestoreDefault && Devices.Any(d => d.Id == _originalDefault))
            TrySetDefault(_originalDefault);
        ForgetOriginalDefault();
    }

    private void ForgetOriginalDefault()
    {
        _originalDefault = null;
        if (_settings.PendingRestoreDefault != null)
        {
            _settings.PendingRestoreDefault = null;
            _settings.Save();
        }
    }

    private static string Friendly(Exception ex)
    {
        string m = ex.Message;
        if (ex is UnauthorizedAccessException || m.Contains("0x80070005") || m.Contains("E_ACCESSDENIED"))
            return "Windows denied access to the device.";
        if (m.Contains("0x88890004") || m.Contains("DEVICE_INVALIDATED", StringComparison.OrdinalIgnoreCase))
            return "One of the speakers is no longer available.";
        if (m.Contains("0x8889000A") || m.Contains("DEVICE_IN_USE", StringComparison.OrdinalIgnoreCase))
            return "One of the speakers is being used exclusively by another app.";
        return m;
    }

    // ---------------------------------------------------------------- sync

    private async Task SyncAsync()
    {
        if (IsSyncing)
        {
            _syncCancel?.Cancel();
            return;
        }
        if (IsBusy) return;
        var selected = Devices.Where(d => d.IsSelected).ToList();
        MicInfo? mic = SelectedMic;
        if (selected.Count < 2 || mic == null) return;

        bool wasSharing = IsSharing;
        if (wasSharing) await StopSharingAsync(keepOriginal: true);

        // The microphone has to be able to hear each speaker, so quiet or muted ones are turned
        // up for the few seconds the test takes, then put back exactly as they were.
        var restore = new List<(DeviceItem Item, double Volume, bool Muted)>();
        // Volumes are left alone unless the microphone can't hear a speaker. Only then is that
        // speaker unmuted and turned up for a second attempt.
        bool MakeAudible(string id) => _dispatcher.Invoke(() =>
        {
            DeviceItem? d = Devices.FirstOrDefault(x => x.Id == id);
            if (d == null || (!d.IsMuted && d.Volume >= 40)) return false;
            restore.Add((d, d.Volume, d.IsMuted));
            if (d.Volume < 40) d.Volume = 40;
            d.IsMuted = false;
            _syncSeconds += Calibrator.SecondsPerSpeaker;
            SyncStep = $"Couldn't hear {d.ShortName}. Turning it up and listening again…";
            return true;
        });

        _syncCancel = new CancellationTokenSource();
        CancellationToken token = _syncCancel.Token;
        _syncSeconds = selected.Count * Calibrator.SecondsPerSpeaker + 0.6;
        SyncProgress = 0;
        SyncStep = "Getting ready…";
        _syncClock.Restart();
        _syncTimer.Start();
        IsSyncing = true;
        Bar = BarKind.None;

        IReadOnlyList<SpeakerReading>? readings = null;
        try
        {
            var ids = selected.Select(d => d.Id).ToList();
            readings = await Task.Run(() => Calibrator.Run(ids, mic.Id, p => _dispatcher.BeginInvoke(() =>
            {
                DeviceItem? d = Devices.FirstOrDefault(x => x.Id == p.DeviceId);
                SyncStep = $"Listening to {d?.ShortName ?? "speaker"} ({p.Index + 1} of {p.Count})…";
            }), token, makeAudible: MakeAudible), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (MicrophoneSilentException)
        {
            ShowBar(BarKind.Error, "The microphone is silent",
                "Windows may be blocking microphone access for desktop apps, or the microphone is muted.", offerMicSettings: true);
        }
        catch (Exception ex)
        {
            Log.Write("Sync failed: " + ex);
            bool denied = ex is UnauthorizedAccessException || ex.Message.Contains("0x80070005");
            ShowBar(BarKind.Error, "Couldn't use the microphone",
                denied ? "Windows is blocking microphone access for desktop apps." : Friendly(ex), offerMicSettings: denied);
        }
        finally
        {
            _syncTimer.Stop();
            foreach (var (item, volume, muted) in restore)
            {
                item.Volume = volume;
                item.IsMuted = muted;
            }
            IsSyncing = false;
            SyncStep = "";
            _syncCancel.Dispose();
            _syncCancel = null;
        }

        if (readings != null) ApplyReadings(selected, readings);
        if (wasSharing && SelectedCount >= 2) await StartSharingAsync();
        else if (wasSharing) RestoreOriginalDefault();
        RaiseState();
        UpdateTexts();
    }

    private void ApplyReadings(List<DeviceItem> selected, IReadOnlyList<SpeakerReading> readings)
    {
        foreach (SpeakerReading r in readings)
            Log.Write($"Measured {r.DeviceId}: heard {r.Heard} snr {r.Snr:F1} direct {r.DirectLatency * 1000:F1} ms stream {r.StreamLatency * 1000:F1} ms loopback {r.LoopbackSeen} {r.Problem}");

        var heard = readings.Where(r => r.Heard).ToList();
        var missed = readings.Where(r => !r.Heard).ToList();

        if (heard.Count >= 2)
        {
            // Measurements are only comparable within one session, so replace the whole set.
            _settings.Calibration = heard.ToDictionary(r => r.DeviceId,
                r => new CalibrationEntry { Direct = r.DirectLatency, Stream = r.StreamLatency, Snr = r.Snr });
            _settings.CalibratedAt = DateTime.Now;

            // A timing nudge is a correction to the old baseline. With a fresh measurement the
            // baseline has moved, so an old nudge would now push the speaker out of sync.
            foreach (SpeakerReading r in heard)
            {
                _settings.TrimMs.Remove(r.DeviceId);
                selected.FirstOrDefault(d => d.Id == r.DeviceId)?.SetTrimQuietly(0);
            }
            _settings.Save();
        }

        string Names(IEnumerable<SpeakerReading> rs) => string.Join(" and ",
            rs.Select(r => selected.FirstOrDefault(d => d.Id == r.DeviceId)?.ShortName ?? "a speaker"));

        if (missed.Count == 0)
        {
            ShowBar(BarKind.Success, "Speakers are in sync", DescribeCalibration(selected) ?? "");
        }
        else
        {
            ShowBar(BarKind.Caution, $"Couldn't hear {Names(missed)}",
                "Move it closer to this PC's microphone, make sure it is switched on and connected, then sync again. "
                + "Headphones can't be heard by the microphone; set those by ear with the timing slider.");
        }
    }

    /// <summary>One plain sentence describing what the last measurement found.</summary>
    private string? DescribeCalibration(List<DeviceItem> selected)
    {
        if (selected.Count < 2 || !selected.All(d => _settings.Calibration.ContainsKey(d.Id))) return null;
        var (leader, ideal, _) = MakePlan(selected);
        DeviceItem lead = selected.First(d => d.Id == leader);
        var others = selected.Where(d => d.Id != leader).ToList();
        if (others.Count == 1)
        {
            double ms = Math.Max(0, ideal[others[0].Id]) * 1000;
            return ms < 5
                ? $"{lead.ShortName} and {others[0].ShortName} play at practically the same moment."
                : $"{lead.ShortName} plays {ms:0} ms later than {others[0].ShortName}, so {others[0].ShortName} is held back to match.";
        }
        return $"{lead.ShortName} is the slowest speaker. The others are held back "
            + string.Join(", ", others.Select(o => $"{Math.Max(0, ideal[o.Id]) * 1000:0} ms")) + " to match it.";
    }

    // ---------------------------------------------------------------- text

    private void UpdateTexts()
    {
        var selected = Devices.Where(d => d.IsSelected).ToList();
        IReadOnlyList<SinkStatus>? status = _engine?.Status();
        bool allGood = true;

        foreach (DeviceItem d in Devices)
        {
            string kind = d.IsBluetooth ? "Bluetooth" : d.Info.Kind == SpeakerKind.Display ? "Display audio" : "";
            if (_engine == null || !d.IsSelected)
            {
                d.CanTrim = false;
                d.Tone = StatusTone.Neutral;
                d.Status = string.Join(" · ", new[] { d.IsDefault ? "Windows default" : "", kind }.Where(s => s.Length > 0));
                if (d.Status.Length == 0) d.Status = "Available";
                continue;
            }

            if (d.Id == _leaderId)
            {
                d.CanTrim = false;
                d.Tone = StatusTone.Good;
                d.Status = "Playing · Windows plays straight to this one";
                continue;
            }

            SinkStatus? s = status?.FirstOrDefault(x => x.DeviceId == d.Id);
            d.CanTrim = s != null;
            if (s == null)
            {
                d.Tone = StatusTone.Neutral;
                d.Status = "Starting…";
                allGood = false;
            }
            else if (s.Fault != null)
            {
                d.Tone = StatusTone.Bad;
                d.Status = "Stopped · the speaker isn't responding";
                allGood = false;
            }
            else if (!s.Locked || Math.Abs(s.ErrorSeconds) > 0.003)
            {
                d.Tone = StatusTone.Neutral;
                d.Status = "Lining up…";
                allGood = false;
            }
            else
            {
                double wanted = _ideal.GetValueOrDefault(d.Id) + d.TrimMs / 1000;
                double leftover = s.DelaySeconds - wanted;
                string held = $"held back {s.DelaySeconds * 1000:0} ms";
                if (_estimated)
                {
                    d.Tone = StatusTone.Caution;
                    d.Status = $"Playing · {held} (a guess — not measured yet)";
                    allGood = false;
                }
                else if (leftover > 0.004)
                {
                    d.Tone = StatusTone.Caution;
                    d.Status = $"Playing · about {leftover * 1000:0} ms late, as close as this pair gets";
                }
                else
                {
                    d.Tone = StatusTone.Good;
                    d.Status = $"In sync · {held}";
                }
            }
        }

        if (_engine != null)
        {
            FooterTone = allGood ? StatusTone.Good : StatusTone.Caution;
            FooterText = _estimated
                ? $"Sharing to {selected.Count} speakers · not synced yet"
                : $"Sharing to {selected.Count} speakers";
        }
        else
        {
            FooterTone = StatusTone.Neutral;
            FooterText = Devices.Count < 2 ? "Connect a second speaker or headphones"
                : selected.Count < 2 ? "Switch on at least two speakers"
                : $"{selected.Count} speakers ready";
        }

        if (!IsSyncing)
        {
            SyncDetail = Mics.Count == 0
                ? "No microphone was found, so speakers can't be measured automatically. Use the timing slider on each speaker instead."
                : DescribeCalibration(selected)
                ?? "Plays a few short chirps on each speaker and listens with the microphone to measure how late each one is. Takes about "
                    + $"{Math.Max(2, selected.Count) * Calibrator.SecondsPerSpeaker:0} seconds.";
        }
        Raise(nameof(IsCalibrated));
        Raise(nameof(SyncIsSuggested));
        Raise(nameof(SyncButtonText));
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _syncCancel?.Cancel();
        ShareEngine? engine = _engine;
        _engine = null;
        try
        {
            engine?.Dispose();
        }
        catch
        {
        }
        RestoreOriginalDefault();
        try
        {
            _notifications?.Dispose();
        }
        catch
        {
        }
        foreach (DeviceItem d in Devices) d.Dispose();
        try
        {
            _enumerator.Dispose();
        }
        catch
        {
        }
    }
}
