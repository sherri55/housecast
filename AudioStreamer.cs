using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AirPlayTray;

/// <summary>
/// Captures whatever Windows is playing and writes it into OwnTone's pipe inside WSL.
/// The wsl.exe process that carries the audio also keeps the WSL system awake,
/// which keeps OwnTone running.
/// </summary>
public sealed class AudioStreamer : IDisposable
{
    // OwnTone reads its pipe as raw 44.1 kHz, 16-bit, stereo PCM
    private static readonly WaveFormat PipeFormat = new(44100, 16, 2);

    // Loopback is polled every half buffer, so a short buffer gets the audio out sooner
    private const int CaptureBufferMilliseconds = 20;

    // Loopback delivers nothing while Windows is silent, but OwnTone needs a steady stream,
    // so after this long without audio the gap is filled with silence
    private static readonly long IdleTicks = Stopwatch.Frequency * 60 / 1000;
    private const int SilenceCheckMilliseconds = 20;

    private readonly string _distro;
    private readonly string _pipePath;
    private readonly object _writeLock = new();

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _captured;
    private IWaveProvider? _converted;
    private byte[] _convertBuffer = [];
    private readonly byte[] _silence = new byte[PipeFormat.AverageBytesPerSecond * SilenceCheckMilliseconds / 1000];
    private Timer? _silenceTimer;
    private Process? _wsl;
    private long _lastAudioTicks = Stopwatch.GetTimestamp();
    private long _silenceBytesSent;

    public AudioStreamer(string distro, string pipePath)
    {
        _distro = distro;
        _pipePath = pipePath;
    }

    public void Start()
    {
        // A copy of the app that was killed leaves its writer running, and two writers
        // interleave their audio in the pipe, so clear any leftovers first
        using (var cleanup = Process.Start(new ProcessStartInfo
        {
            FileName = "wsl.exe",
            ArgumentList = { "-d", _distro, "-u", "root", "--", "pkill", "-f", $"cat > {_pipePath}" },
            UseShellExecute = false,
            CreateNoWindow = true,
        }))
        {
            cleanup?.WaitForExit(5000);
        }

        using var enumerator = new MMDeviceEnumerator();
        _capture = new LoopbackCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        _captured = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            // Return only what was captured; padding here would put tiny gaps in the sound
            ReadFully = false,
        };

        var stereo = new MultiplexingSampleProvider([_captured.ToSampleProvider()], 2);
        var resampled = new WdlResamplingSampleProvider(stereo, PipeFormat.SampleRate);
        _converted = new SampleToWaveProvider16(resampled);
        _convertBuffer = new byte[PipeFormat.AverageBytesPerSecond * CaptureBufferMilliseconds / 1000];

        _capture.DataAvailable += OnCaptured;
        _capture.StartRecording();
        _silenceTimer = new Timer(_ => SendSilenceWhileIdle(), null, 0, SilenceCheckMilliseconds);
    }

    // Writes captured audio straight through, so nothing waits on a timer
    private void OnCaptured(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
            return;

        lock (_writeLock)
        {
            _captured!.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _lastAudioTicks = Stopwatch.GetTimestamp();
            _silenceBytesSent = 0;

            int count;
            while ((count = _converted!.Read(_convertBuffer, 0, _convertBuffer.Length)) > 0)
                Write(_convertBuffer, count);
        }
    }

    private void SendSilenceWhileIdle()
    {
        lock (_writeLock)
        {
            var idleTicks = Stopwatch.GetTimestamp() - _lastAudioTicks;
            if (idleTicks < IdleTicks)
                return;

            // Silence covers the whole quiet spell, counted from the last audio
            var due = idleTicks * PipeFormat.AverageBytesPerSecond / Stopwatch.Frequency;
            due -= due % PipeFormat.BlockAlign;
            while (_silenceBytesSent < due)
            {
                var count = (int)Math.Min(_silence.Length, due - _silenceBytesSent);
                Write(_silence, count);
                _silenceBytesSent += count;
            }
        }
    }

    private void Write(byte[] buffer, int count)
    {
        try
        {
            EnsureWslProcess().Write(buffer, 0, count);
        }
        catch (IOException)
        {
            // The wsl.exe process ended; the next write starts a new one
        }
    }

    private Stream EnsureWslProcess()
    {
        if (_wsl is { HasExited: false })
            return _wsl.StandardInput.BaseStream;

        _wsl?.Dispose();
        _wsl = Process.Start(new ProcessStartInfo
        {
            FileName = "wsl.exe",
            ArgumentList = { "-d", _distro, "-u", "root", "--", "sh", "-c", $"cat > {_pipePath}" },
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        return _wsl.StandardInput.BaseStream;
    }

    public void Dispose()
    {
        _silenceTimer?.Dispose();
        _capture?.StopRecording();
        _capture?.Dispose();

        lock (_writeLock)
        {
            if (_wsl is { HasExited: false })
            {
                _wsl.StandardInput.Close();
                if (!_wsl.WaitForExit(2000))
                    _wsl.Kill();
            }
            _wsl?.Dispose();
        }
    }

    /// <summary>
    /// NAudio's WasapiLoopbackCapture with a shorter buffer than its fixed 100 ms.
    /// </summary>
    private sealed class LoopbackCapture(MMDevice device) : WasapiCapture(device, false, CaptureBufferMilliseconds)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}
