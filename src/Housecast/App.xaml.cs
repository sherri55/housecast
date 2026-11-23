using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using AirPlay.Engine;
using AirPlay.Engine.Pairing;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Housecast;

public partial class App : System.Windows.Application
{
    private const string RestartArgument = "--restart";
    private const string QuitArgument = "--quit";
    private const string SingleInstanceName = "Housecast.SingleInstance";
    private const string QuitEventName = "Housecast.Quit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _quitRequested;
    private AirPlayEngine? _engine;
    private AudioStreamer? _streamer;
    private bool _restoring;
    private FlyoutViewModel? _viewModel;
    private Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The uninstaller runs a copy with --quit so the running one lets go of its files
        if (e.Args.Contains(QuitArgument))
        {
            QuitRunningCopy();
            Shutdown();
            return;
        }

        // A second copy couldn't take the PTP ports the first one holds. A copy started by
        // Restart waits for the old one to quit instead.
        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirst);
        if (!isFirst && e.Args.Contains(RestartArgument))
            isFirst = WaitForPreviousCopy(_singleInstance);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _quitRequested = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        ThreadPool.RegisterWaitForSingleObject(_quitRequested, (_, _) => Dispatcher.Invoke(() => Shutdown()), null, Timeout.Infinite, true);

        try
        {
            _engine = new AirPlayEngine(Environment.MachineName, new KeyStore(WindowsSettings.PairingsPath));
        }
        catch (SocketException)
        {
            System.Windows.MessageBox.Show(
                "Another program is using the network ports Housecast needs (UDP 319 and 320), " +
                "usually another AirPlay sender. Close it and start Housecast again.",
                "Housecast", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        _streamer = new AudioStreamer(_engine);
        _streamer.Start();

        _viewModel = new FlyoutViewModel(_engine);

        // Plays the remembered speakers once they are found, and again if they all dropped us
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        watchdog.Tick += async (_, _) => await RestoreSelectionAsync();
        watchdog.Start();
        var flyout = new FlyoutWindow(_viewModel);

        var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = WindowsSettings.StartsWithWindows,
        };
        startWithWindows.CheckedChanged += (_, _) => WindowsSettings.StartsWithWindows = startWithWindows.Checked;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Restart", null, (_, _) =>
        {
            Process.Start(Environment.ProcessPath!, RestartArgument);
            Shutdown();
        });
        // Only an installed copy has Inno Setup's uninstaller beside it
        var uninstaller = Path.Combine(AppContext.BaseDirectory, "unins000.exe");
        if (File.Exists(uninstaller))
        {
            // The uninstaller asks to confirm, then closes this copy itself
            menu.Items.Add("Uninstall Housecast", null, (_, _) =>
                Process.Start(new ProcessStartInfo(uninstaller) { UseShellExecute = true }));
        }
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = DrawTrayIcon(WindowsSettings.TaskbarUsesLightTheme),
            Text = "Housecast",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                flyout.Toggle();
        };

        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            // Theme switches arrive as General; accent colour changes as Color or VisualStyle
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
                return;
            Dispatcher.Invoke(() =>
            {
                flyout.ApplyTheme();
                _trayIcon.Icon = DrawTrayIcon(WindowsSettings.TaskbarUsesLightTheme);
            });
        };
    }

    // When nothing is playing but the user had speakers ticked, plays the ones that are on the
    // network. Unticking everything in the flyout empties the remembered list, so that sticks.
    private async Task RestoreSelectionAsync()
    {
        var remembered = WindowsSettings.SelectedSpeakerIds;
        var speakers = _engine!.Speakers;
        if (_restoring || remembered.Length == 0 || speakers.Any(s => _engine.IsPlaying(s.Id)))
            return;

        _restoring = true;
        try
        {
            await Task.WhenAll(speakers.Where(s => remembered.Contains(s.Id)).Select(s =>
            {
                var setting = WindowsSettings.SpeakerSettingFor(s.Id);
                return _engine.PlayAsync(s.Id, setting.Volume, setting.OffsetMs);
            }));
        }
        finally
        {
            _restoring = false;
        }
    }

    // The old copy disposes the mutex without releasing it, so it comes back abandoned: ours all the same
    private static bool WaitForPreviousCopy(Mutex singleInstance)
    {
        try
        {
            return singleInstance.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    // Quits the running copy as its tray menu would, so it restores the PC's mute and tells its
    // speakers, then waits for it to be gone
    private static void QuitRunningCopy()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quitRequested))
            return;
        using (quitRequested)
            quitRequested.Set();
        using var singleInstance = new Mutex(false, SingleInstanceName);
        WaitForPreviousCopy(singleInstance);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
            _trayIcon.Visible = false;

        _viewModel?.RestoreLocalSpeakers();
        _streamer?.Dispose();
        // Tells each speaker the session is over, rather than leave it waiting for audio
        _engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _trayIcon?.Dispose();
        _quitRequested?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // The Housecast mark: a house with sound waves rising inside it
    private static Icon DrawTrayIcon(bool lightTaskbar)
    {
        var color = lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 2.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, [new PointF(5, 15), new PointF(16, 5), new PointF(27, 15)]);
            g.DrawLines(pen, [new PointF(8, 12.5f), new PointF(8, 27), new PointF(24, 27), new PointF(24, 12.5f)]);
            using var wavePen = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(wavePen, 11.5f, 19.5f, 9, 9, 225, 90);
            g.DrawArc(wavePen, 8, 16, 16, 16, 225, 90);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 14.2f, 22.2f, 3.6f, 3.6f);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
