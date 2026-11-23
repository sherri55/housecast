using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using AirPlay.Engine;
using AirPlay.Engine.Pairing;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AirPlayTray;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private AirPlayEngine? _engine;
    private AudioStreamer? _streamer;
    private bool _restoring;
    private FlyoutViewModel? _viewModel;
    private Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second copy couldn't take the PTP ports the first one holds
        _singleInstance = new Mutex(true, "AirPlayTray.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        try
        {
            _engine = new AirPlayEngine(Environment.MachineName, new KeyStore(KeyStore.DefaultPath));
        }
        catch (SocketException)
        {
            System.Windows.MessageBox.Show(
                "Another program is using the network ports AirPlay needs (UDP 319 and 320), " +
                "usually another AirPlay sender. Close it and start AirPlay Tray again.",
                "AirPlay", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = DrawTrayIcon(WindowsSettings.TaskbarUsesLightTheme),
            Text = "AirPlay",
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
            _trayIcon.Visible = false;

        _viewModel?.RestoreLocalSpeakers();
        _streamer?.Dispose();
        // Tells each speaker the session is over, rather than leave it waiting for audio
        _engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // The AirPlay mark: a screen with a triangle rising into its bottom edge
    private static Icon DrawTrayIcon(bool lightTaskbar)
    {
        var color = lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(color, 2.5f) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(9, 22), new PointF(4, 22), new PointF(4, 5), new PointF(28, 5), new PointF(28, 22), new PointF(23, 22)]);
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, [new PointF(16, 17), new PointF(24, 28), new PointF(8, 28)]);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
