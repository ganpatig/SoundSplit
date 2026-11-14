using System;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoundSplit.Audio;
using SoundSplit.Services;
using SoundSplit.ViewModels;

namespace SoundSplit;

public partial class App : Application
{
    private Mutex? _single;
    private MainViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Audio threads must never wait for a blocking garbage collection.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        if (Diagnostics.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        int shot = Array.IndexOf(e.Args, "--shot");
        string? shotPath = shot >= 0 && shot + 1 < e.Args.Length ? e.Args[shot + 1] : null;

        if (shotPath == null)
        {
            _single = new Mutex(true, "SoundSplit.SingleInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("SoundSplit is already running.", "SoundSplit", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("Fatal: " + args.ExceptionObject);

        if (e.Args.Contains("--dark")) ThemeMode = ThemeMode.Dark;
        if (e.Args.Contains("--light")) ThemeMode = ThemeMode.Light;

        _vm = new MainViewModel { MuteOutput = e.Args.Contains("--mute") };
        var window = new MainWindow(_vm);
        MainWindow = window;
        window.Closed += (_, _) =>
        {
            _vm.Dispose();
            Shutdown();
        };
        window.Show();

        if (shotPath != null) _ = CaptureAsync(window, shotPath, e.Args.Contains("--share"));
    }

    /// <summary>Test hook: save a picture of the window and exit (optionally after starting to share).</summary>
    private async Task CaptureAsync(MainWindow window, string path, bool share)
    {
        try
        {
            await Task.Delay(900);
            if (share)
            {
                _vm!.ToggleShare.Execute(null);
                await Task.Delay(4500);
            }

            var root = (FrameworkElement)window.Content;
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            int w = (int)Math.Ceiling(root.ActualWidth * scale), h = (int)Math.Ceiling(root.ActualHeight * scale);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // Mica is drawn by the compositor, not by WPF, so stand in for it with the theme's base colour.
                bool dark = ThemeMode == ThemeMode.Dark || (ThemeMode == ThemeMode.System && window.IsDark);
                var back = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
                var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                dc.DrawRectangle(back, null, rect);
                dc.DrawRectangle(new VisualBrush(root), null, rect);
            }
            var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(path)) encoder.Save(file);
        }
        catch (Exception ex)
        {
            Log.Write("Capture failed: " + ex);
        }
        window.Close();
    }
}
