using System.Diagnostics;
using System.Net.Sockets;
using AirPlay.Engine.Audio;
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Rtsp;

namespace AirPlay.Engine;

public enum PlayResult
{
    Playing,
    /// <summary>The speaker is showing a PIN; pass it to <see cref="AirPlayEngine.SubmitPinAsync"/>.</summary>
    NeedsPin,
    Failed,
}

/// <summary>
/// Everything the app needs: the speakers on the network, which of them play, their volume
/// and delay, PIN pairing, and the audio to send. All speakers play one stream in sync.
/// </summary>
public sealed class AirPlayEngine : IAsyncDisposable
{
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(10);
    // A speaker can miss an announcement now and then; only one gone this long leaves the list
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly string _clientName;
    private readonly KeyStore _keyStore;
    private readonly AirPlayStreamer _streamer;
    private readonly PcmPacer _pacer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pumpLoop;
    private readonly Task _discoveryLoop;
    private readonly object _lock = new();
    private readonly Dictionary<string, (AirPlaySpeaker Speaker, DateTime LastSeen)> _seen = [];
    private readonly Dictionary<string, Playing> _playing = [];
    private readonly HashSet<string> _starting = [];
    // Connections where a speaker was asked to show a PIN; the PIN must go back on the same one
    private readonly Dictionary<string, RtspClient> _pinRequests = [];
    private int _masterVolume = 100;

    private sealed record Playing(AirPlaySession Session, int Volume);

    /// <param name="clientName">Shown by some speakers as the source of the sound.</param>
    /// <exception cref="SocketException">UDP 319 or 320 is taken, usually by another AirPlay sender.</exception>
    public AirPlayEngine(string clientName, KeyStore keyStore)
    {
        _clientName = clientName;
        _keyStore = keyStore;
        _streamer = new AirPlayStreamer(Convert.ToUInt64(keyStore.ClientId, 16), clientName);
        _pacer = new PcmPacer(frame => _streamer.Send(frame));
        // On the thread pool: started from an app's UI thread, the loops would otherwise resume
        // there, and any pause in the UI (such as pairing a speaker) would delay the audio
        _pumpLoop = Task.Run(() => PumpAsync(_stop.Token));
        _discoveryLoop = Task.Run(() => DiscoverAsync(_stop.Token));
    }

    /// <summary>The AirPlay 2 speakers on the network, by name.</summary>
    public IReadOnlyList<AirPlaySpeaker> Speakers
    {
        get
        {
            lock (_lock)
                return _seen.Values.Select(s => s.Speaker).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    public bool IsPlaying(string speakerId)
    {
        lock (_lock)
            return _playing.ContainsKey(speakerId);
    }

    /// <summary>Scales every speaker's volume, 0–100.</summary>
    public int MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0, 100);
            foreach (var (_, playing) in PlayingNow())
                _ = SendVolumeAsync(playing.Session, playing.Volume);
        }
    }

    /// <summary>Adds captured audio: 44.1 kHz, 16-bit, stereo, little-endian.</summary>
    public void Write(ReadOnlySpan<byte> pcm) => _pacer.Write(pcm);

    /// <param name="volume">0–100, before <see cref="MasterVolume"/> scales it.</param>
    /// <param name="offsetMilliseconds">Plays this speaker later (positive) or earlier (negative) than the rest.</param>
    public async Task<PlayResult> PlayAsync(string speakerId, int volume, int offsetMilliseconds)
    {
        AirPlaySpeaker? speaker;
        lock (_lock)
        {
            if (_playing.ContainsKey(speakerId))
                return PlayResult.Playing;
            speaker = _seen.GetValueOrDefault(speakerId).Speaker;
            if (speaker is null || !_starting.Add(speakerId))
                return PlayResult.Failed;
        }

        var pairing = _keyStore.Find(speakerId);
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            // Pairing is seconds of big-number maths at worst; keep it off the caller's (UI) thread
            var session = await Task.Run(() => _streamer.AddSpeakerAsync(speaker, Scaled(volume), offsetMilliseconds, pairing, _keyStore.ClientId, timeout.Token));
            session.Lost += OnLost;
            lock (_lock)
                _playing[speakerId] = new Playing(session, volume);
            return PlayResult.Playing;
        }
        catch (PairingException) when (pairing is null)
        {
            return await RequestPinAsync(speaker);
        }
        catch (PairingException)
        {
            // The speaker no longer knows our keys, e.g. after a reset; pair afresh next time
            _keyStore.Forget(speakerId);
            return PlayResult.Failed;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AirPlayException or OperationCanceledException)
        {
            return PlayResult.Failed;
        }
        finally
        {
            lock (_lock)
                _starting.Remove(speakerId);
        }
    }

    public async Task StopAsync(string speakerId)
    {
        Playing? playing;
        lock (_lock)
        {
            if (_playing.Remove(speakerId, out playing))
                playing.Session.Lost -= OnLost;
        }
        if (playing is not null)
            await _streamer.RemoveSpeakerAsync(playing.Session);
    }

    /// <param name="volume">0–100, before <see cref="MasterVolume"/> scales it.</param>
    public async Task SetVolumeAsync(string speakerId, int volume)
    {
        Playing? playing;
        lock (_lock)
        {
            if (!_playing.TryGetValue(speakerId, out playing))
                return;
            playing = playing with { Volume = volume };
            _playing[speakerId] = playing;
        }
        await SendVolumeAsync(playing.Session, volume);
    }

    public void SetOffset(string speakerId, int offsetMilliseconds)
    {
        Playing? playing;
        lock (_lock)
            _playing.TryGetValue(speakerId, out playing);
        if (playing is not null)
            _streamer.SetOffset(playing.Session, offsetMilliseconds);
    }

    /// <summary>
    /// Pairs with the PIN a speaker is showing after <see cref="PlayAsync"/> said it needs one.
    /// Afterwards <see cref="PlayAsync"/> connects with the saved keys.
    /// </summary>
    /// <returns>False if the PIN was wrong or the speaker stopped waiting; play again for a new PIN.</returns>
    public async Task<bool> SubmitPinAsync(string speakerId, string pin)
    {
        RtspClient? rtsp;
        lock (_lock)
            _pinRequests.Remove(speakerId, out rtsp);
        if (rtsp is null)
            return false;

        await using (rtsp)
        {
            try
            {
                _keyStore.Save(speakerId, await Task.Run(() => HomeKitPairing.PairWithPinAsync(rtsp, pin, _keyStore.ClientId)));
                return true;
            }
            catch (Exception ex) when (ex is PairingException or IOException or SocketException)
            {
                return false;
            }
        }
    }

    private async Task<PlayResult> RequestPinAsync(AirPlaySpeaker speaker)
    {
        var rtsp = new RtspClient(Convert.ToUInt64(_keyStore.ClientId, 16), _clientName);
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            await rtsp.ConnectAsync(speaker.Address, speaker.Port, timeout.Token);
            await rtsp.SendAsync("GET", "/info", cancellationToken: timeout.Token);
            await HomeKitPairing.RequestPinAsync(rtsp, timeout.Token);
        }
        catch (Exception ex) when (ex is PairingException or IOException or SocketException or OperationCanceledException)
        {
            await rtsp.DisposeAsync();
            return PlayResult.Failed;
        }

        RtspClient? earlier;
        lock (_lock)
        {
            _pinRequests.Remove(speaker.Id, out earlier);
            _pinRequests[speaker.Id] = rtsp;
        }
        if (earlier is not null)
            await earlier.DisposeAsync();
        return PlayResult.NeedsPin;
    }

    // The speaker dropped us; it shows as not playing from now on
    private void OnLost(AirPlaySession session)
    {
        string? id;
        lock (_lock)
        {
            id = _playing.FirstOrDefault(p => p.Value.Session == session).Key;
            if (id is not null)
                _playing.Remove(id);
        }
        if (id is not null)
            _ = _streamer.RemoveSpeakerAsync(session);
    }

    private int Scaled(int volume) => volume * _masterVolume / 100;

    private async Task SendVolumeAsync(AirPlaySession session, int volume)
    {
        try
        {
            await session.SetVolumeAsync(Scaled(volume));
        }
        catch (Exception ex) when (ex is IOException or SocketException or AirPlayException or ObjectDisposedException)
        {
            // A speaker that went away; it shows as lost soon
        }
    }

    private List<KeyValuePair<string, Playing>> PlayingNow()
    {
        lock (_lock)
            return _playing.ToList();
    }

    // Frames go out at the speakers' rate by our clock, whatever pace the audio is written at
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            _pacer.Pump(clock.Elapsed);
            try
            {
                await Task.Delay(2, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var found = await SpeakerBrowser.BrowseAsync(ScanTime, cancellationToken);
                var now = DateTime.UtcNow;
                lock (_lock)
                {
                    // Only speakers this engine can play to: AirPlay 2 audio timed by PTP
                    foreach (var speaker in found.Where(s => s.SupportsAirPlay2Audio && s.SupportsPtp))
                        _seen[speaker.Id] = (speaker, now);
                    foreach (var gone in _seen.Where(s => now - s.Value.LastSeen > ForgetAfter && !_playing.ContainsKey(s.Key)).ToList())
                        _seen.Remove(gone.Key);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // Windows' DNS-SD refused, e.g. with no network; the next round tries again
            }

            try
            {
                await Task.Delay(ScanEvery, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await Task.WhenAll(_pumpLoop, _discoveryLoop);
        await _streamer.DisposeAsync();
        foreach (var rtsp in _pinRequests.Values)
            await rtsp.DisposeAsync();
        _stop.Dispose();
    }
}
