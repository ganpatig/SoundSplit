using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SoundSplit.ViewModels;

namespace SoundSplit;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    internal bool IsDark { get; private set; }

    internal MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        vm.PropertyChanged += OnViewModelChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        ApplyStatusColours();
        UpdateButtons();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsSharing) or nameof(MainViewModel.SyncIsSuggested) or nameof(MainViewModel.IsSyncing))
            UpdateButtons();
    }

    /// <summary>
    /// One accent-coloured button at a time, pointing at the next sensible step: "Sync now" when
    /// the speakers have not been measured, otherwise "Start sharing".
    /// </summary>
    private void UpdateButtons()
    {
        var accent = TryFindResource("AccentButtonStyle") as Style;
        var normal = TryFindResource(typeof(System.Windows.Controls.Button)) as Style;
        bool syncFirst = _vm.SyncIsSuggested && !_vm.IsSharing;
        SyncButton.Style = syncFirst ? accent ?? normal : normal;
        ShareButton.Style = !_vm.IsSharing && !syncFirst ? accent ?? normal : normal;
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(ApplyStatusColours);
    }

    /// <summary>Windows 11 uses different success / caution / critical colours on dark backgrounds.</summary>
    private void ApplyStatusColours()
    {
        ThemeMode mode = Application.Current.ThemeMode;
        bool dark = mode == ThemeMode.Dark || (mode != ThemeMode.Light && IsDarkTheme());
        IsDark = dark;
        void Put(string key, string light, string darkColour) =>
            Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColour : light));

        Put("GoodBrush", "#0F7B0F", "#6CCB5F");
        Put("CautionBrush", "#9D5D00", "#FCE100");
        Put("BadBrush", "#C42B1C", "#FF99A4");
        Put("GoodFill", "#DFF6DD", "#393D1B");
        Put("CautionFill", "#FFF4CE", "#433519");
        Put("BadFill", "#FDE7E9", "#442726");
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        base.OnClosed(e);
    }
}
