using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AirPlayTray;

public partial class App : System.Windows.Application
{
    private const string Distro = "airplay";
    private const string PipePath = "/srv/music/pc-audio";

    private Mutex? _singleInstance;
    private OwnToneClient? _client;
    private AudioStreamer? _streamer;
    private FlyoutViewModel? _viewModel;
    private Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two copies would both write into OwnTone's pipe and interleave the audio
        _singleInstance = new Mutex(true, "AirPlayTray.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _client = new OwnToneClient();
        _streamer = new AudioStreamer(Distro, PipePath);
        _streamer.Start();

        _viewModel = new FlyoutViewModel(_client, Distro);

        // OwnTone doesn't always start the pipe by itself, e.g. when it boots while audio is
        // already flowing, and a pause in the stream can leave it with no speakers selected
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        watchdog.Tick += async (_, _) =>
        {
            try
            {
                if (!await _client.IsPlayingAsync())
                    await _client.PlayPipeAsync(PipePath);

                await RestoreSelectionAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // OwnTone is still starting; try again on the next tick
            }
        };
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

    // When nothing is selected but the user had speakers ticked, selects the ones OwnTone has
    // found. Unticking everything in the flyout empties the remembered list, so that sticks.
    private async Task RestoreSelectionAsync()
    {
        var remembered = WindowsSettings.SelectedSpeakerIds;
        if (remembered.Length == 0)
            return;

        var outputs = await _client!.GetOutputsAsync();
        if (outputs.Any(o => o.Selected))
            return;

        var found = outputs.Where(o => remembered.Contains(o.Id)).Select(o => o.Id).ToArray();
        if (found.Length > 0)
            await _client.SelectOnlyAsync(found);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
            _trayIcon.Visible = false;

        // Stop the speakers rather than leave them playing silence until WSL idles out
        try
        {
            _client?.SelectOnlyAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _viewModel?.RestoreLocalSpeakers();
        _streamer?.Dispose();
        _client?.Dispose();
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
