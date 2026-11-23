using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AirPlay.Engine.Audio;
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Ptp;
using AirPlay.Engine.Receiver;
using AirPlay.Engine.Rtsp;

namespace AirPlay.Engine;

/// <summary>A group of speakers the engine announces as one AirPlay speaker.</summary>
public sealed record AirPlayGroup(string Name, IReadOnlyList<GroupMember> Members);

/// <param name="Volume">0–100; a sender's own volume scales it.</param>
/// <param name="OffsetMilliseconds">Plays this speaker later (positive) or earlier (negative) than the rest.</param>
public sealed record GroupMember(string SpeakerId, int Volume, int OffsetMilliseconds);

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

    // Speakers get relayed audio this long before it plays. A sender asks for audio to be heard
    // the moment its anchor arrives, so audio due sooner than about 0.75 s after a play, resume
    // or seek can't be relayed in time and is skipped; the rest plays in sync with the sender
    private static readonly TimeSpan RelayLead = TimeSpan.FromSeconds(1);
    // Relayed audio goes out this long before it plays, after the decoded audio has had time
    // to arrive; speakers need at least 250 ms
    private static readonly TimeSpan RelaySendLead = TimeSpan.FromMilliseconds(750);

    private readonly string _clientName;
    private readonly KeyStore _keyStore;
    private readonly PtpMaster _ptp;
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
    private readonly Dictionary<string, Relay> _relays = [];
    private readonly SemaphoreSlim _publishing = new(1, 1);

    private sealed record Playing(AirPlaySession Session, int Volume, int OffsetMilliseconds);

    // A published group: its receiver, its own stream, and, while a sender plays to it, the
    // members playing that sender and the ones to give back to the PC's stream afterwards
    private sealed class Relay(AirPlayGroup group, AirPlayReceiver receiver, AirPlayStreamer streamer)
    {
        public AirPlayGroup Group { get; set; } = group;
        public AirPlayReceiver Receiver { get; } = receiver;
        public AirPlayStreamer Streamer { get; } = streamer;
        public Dictionary<string, (AirPlaySession Session, int Volume)> Members { get; } = [];
        public Dictionary<string, (int Volume, int OffsetMilliseconds)> GiveBack { get; } = [];
        public string? Sender { get; set; }
        public int SenderVolume { get; set; } = 100;
        // Takeover and release run one at a time
        public SemaphoreSlim Busy { get; } = new(1, 1);
    }

    /// <param name="clientName">Shown by some speakers as the source of the sound.</param>
    /// <exception cref="SocketException">UDP 319 or 320 is taken, usually by another AirPlay sender.</exception>
    public AirPlayEngine(string clientName, KeyStore keyStore)
    {
        _clientName = clientName;
        _keyStore = keyStore;
        _ptp = new PtpMaster();
        _ptp.Start();
        _streamer = new AirPlayStreamer(_ptp, Convert.ToUInt64(keyStore.ClientId, 16), clientName);
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

    /// <summary>True while the speaker plays this PC's sound, or a sender's through one of its groups.</summary>
    public bool IsPlaying(string speakerId)
    {
        lock (_lock)
            return _playing.ContainsKey(speakerId) || HeldBy(speakerId) is not null;
    }

    /// <summary>
    /// A sender (an iPhone, say) started or stopped playing to a published group: the group's
    /// name, and the sender's name or null once it stopped.
    /// </summary>
    public event Action<string, string?>? GroupPlaybackChanged;

    /// <summary>For diagnosis: what senders playing to the groups do.</summary>
    public event Action<string>? ReceiverLogged;

    /// <summary>
    /// Announces each group on the network as an AirPlay speaker of its own, and stops
    /// announcing groups no longer listed. While a sender plays to a group, its speakers play
    /// that sender's audio instead of this PC's, then go back.
    /// </summary>
    public async Task PublishGroupsAsync(IReadOnlyList<AirPlayGroup> groups)
    {
        // Calls in quick succession (several saves) apply in order, never interleaved
        await _publishing.WaitAsync();
        try
        {
            await PublishAsync(groups);
        }
        finally
        {
            _publishing.Release();
        }
    }

    private async Task PublishAsync(IReadOnlyList<AirPlayGroup> groups)
    {
        var stale = new List<Relay>();
        var added = new List<AirPlayGroup>();
        lock (_lock)
        {
            foreach (var (name, relay) in _relays.ToList())
            {
                var wanted = groups.FirstOrDefault(g => g.Name == name);
                if (wanted is not null && wanted.Members.Select(m => m.SpeakerId).SequenceEqual(relay.Group.Members.Select(m => m.SpeakerId)))
                {
                    relay.Group = wanted;
                    continue;
                }
                _relays.Remove(name);
                stale.Add(relay);
            }
            added.AddRange(groups.Where(g => !_relays.ContainsKey(g.Name)));
        }

        foreach (var relay in stale)
            await CloseRelayAsync(relay);
        foreach (var group in added)
        {
            AirPlayReceiver receiver;
            try
            {
                receiver = await AirPlayReceiver.StartAsync(group.Name, _keyStore, _ptp.Follower, RelayLead);
            }
            catch (InvalidOperationException)
            {
                // Windows' DNS-SD refused, e.g. with no network; the next publish tries again
                continue;
            }
            var relay = new Relay(group, receiver, new AirPlayStreamer(_ptp, Convert.ToUInt64(_keyStore.ClientId, 16), _clientName));
            receiver.Logged += message => ReceiverLogged?.Invoke(message);
            receiver.PlaybackStarted += sender => _ = TakeOverAsync(relay, sender);
            receiver.PlaybackStopped += () => _ = GiveBackAsync(relay);
            receiver.VolumeChanged += decibels => SetSenderVolume(relay, decibels);
            receiver.AudioDue += audio => relay.Streamer.SendTimed(MemoryMarshal.Cast<byte, short>(audio.Pcm), audio.PlayAtNanoseconds);
            lock (_lock)
                _relays[group.Name] = relay;
        }
    }

    /// <summary>Scales every speaker's volume, 0–100.</summary>
    public int MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0, 100);
            foreach (var (_, playing) in PlayingNow())
                _ = SendVolumeAsync(playing.Session, Scaled(playing.Volume));
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
            // Playing a sender through a group: it plays this PC's sound once the sender stops
            if (HeldBy(speakerId) is { } relay)
            {
                relay.GiveBack[speakerId] = (volume, offsetMilliseconds);
                return PlayResult.Playing;
            }
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
                _playing[speakerId] = new Playing(session, volume, offsetMilliseconds);
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
            HeldBy(speakerId)?.GiveBack.Remove(speakerId);
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
        await SendVolumeAsync(playing.Session, Scaled(volume));
    }

    public void SetOffset(string speakerId, int offsetMilliseconds)
    {
        Playing? playing;
        lock (_lock)
        {
            if (_playing.TryGetValue(speakerId, out playing))
                _playing[speakerId] = playing with { OffsetMilliseconds = offsetMilliseconds };
        }
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

    // The group playing a sender's audio on this speaker, if any; call under _lock
    private Relay? HeldBy(string speakerId) => _relays.Values.FirstOrDefault(r => r.Members.ContainsKey(speakerId));

    // A sender started playing to a group: its speakers leave the PC's stream for the group's
    private async Task TakeOverAsync(Relay relay, string sender)
    {
        await relay.Busy.WaitAsync();
        try
        {
            relay.Sender = sender;
            var joining = new List<(GroupMember Member, AirPlaySpeaker Speaker)>();
            var leaving = new List<AirPlaySession>();
            lock (_lock)
            {
                foreach (var member in relay.Group.Members)
                {
                    if (relay.Members.ContainsKey(member.SpeakerId) || HeldBy(member.SpeakerId) is not null
                        || _seen.GetValueOrDefault(member.SpeakerId).Speaker is not { } speaker)
                        continue;
                    if (_playing.Remove(member.SpeakerId, out var playing))
                    {
                        playing.Session.Lost -= OnLost;
                        leaving.Add(playing.Session);
                        relay.GiveBack[member.SpeakerId] = (playing.Volume, playing.OffsetMilliseconds);
                    }
                    joining.Add((member, speaker));
                }
            }

            // Off the PC's stream first, so each speaker follows one stream at a time
            foreach (var session in leaving)
                await _streamer.RemoveSpeakerAsync(session);
            await Task.WhenAll(joining.Select(j => JoinRelayAsync(relay, j.Member, j.Speaker)));
        }
        finally
        {
            relay.Busy.Release();
        }
        GroupPlaybackChanged?.Invoke(relay.Group.Name, sender);
    }

    private async Task JoinRelayAsync(Relay relay, GroupMember member, AirPlaySpeaker speaker)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            var volume = member.Volume * relay.SenderVolume / 100;
            var session = await Task.Run(() => relay.Streamer.AddSpeakerAsync(speaker, volume, member.OffsetMilliseconds,
                _keyStore.Find(member.SpeakerId), _keyStore.ClientId, timeout.Token));
            session.Lost += lost => _ = LeaveRelayAsync(relay, member.SpeakerId, lost);
            lock (_lock)
                relay.Members[member.SpeakerId] = (session, member.Volume);
        }
        catch (Exception ex) when (ex is PairingException or IOException or SocketException or AirPlayException or OperationCanceledException)
        {
            // This speaker misses out; the rest of the group plays
        }
    }

    private async Task LeaveRelayAsync(Relay relay, string speakerId, AirPlaySession session)
    {
        lock (_lock)
            relay.Members.Remove(speakerId);
        await relay.Streamer.RemoveSpeakerAsync(session);
    }

    // The sender stopped: the group's speakers go back to what they played before
    private async Task GiveBackAsync(Relay relay)
    {
        await relay.Busy.WaitAsync();
        try
        {
            List<AirPlaySession> sessions;
            List<KeyValuePair<string, (int Volume, int OffsetMilliseconds)>> giveBack;
            lock (_lock)
            {
                sessions = relay.Members.Values.Select(m => m.Session).ToList();
                relay.Members.Clear();
                giveBack = relay.GiveBack.ToList();
                relay.GiveBack.Clear();
                relay.Sender = null;
            }
            foreach (var session in sessions)
                await relay.Streamer.RemoveSpeakerAsync(session);
            relay.Streamer.StopTimed();
            foreach (var (speakerId, (volume, offset)) in giveBack)
                await PlayAsync(speakerId, volume, offset);
        }
        finally
        {
            relay.Busy.Release();
        }
        GroupPlaybackChanged?.Invoke(relay.Group.Name, null);
    }

    // The sender's volume scales each member's own, as the master volume does for the PC's sound
    private void SetSenderVolume(Relay relay, double decibels)
    {
        List<(AirPlaySession Session, int Volume)> members;
        lock (_lock)
        {
            relay.SenderVolume = decibels <= -30 ? 0 : (int)Math.Round((decibels + 30) / 0.3);
            members = relay.Members.Values.ToList();
        }
        foreach (var (session, volume) in members)
            _ = SendVolumeAsync(session, volume * relay.SenderVolume / 100);
    }

    private async Task CloseRelayAsync(Relay relay)
    {
        await relay.Receiver.DisposeAsync();
        await GiveBackAsync(relay);
        await relay.Streamer.DisposeAsync();
    }

    private int Scaled(int volume) => volume * _masterVolume / 100;

    private async Task SendVolumeAsync(AirPlaySession session, int percent)
    {
        try
        {
            await session.SetVolumeAsync(percent);
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
            List<Relay> relays;
            lock (_lock)
                relays = _relays.Values.ToList();
            foreach (var relay in relays)
                relay.Streamer.PumpTimed(RelaySendLead);
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
        List<Relay> relays;
        lock (_lock)
        {
            relays = _relays.Values.ToList();
            _relays.Clear();
        }
        foreach (var relay in relays)
            await CloseRelayAsync(relay);
        await _streamer.DisposeAsync();
        await _ptp.DisposeAsync();
        foreach (var rtsp in _pinRequests.Values)
            await rtsp.DisposeAsync();
        _stop.Dispose();
    }
}
