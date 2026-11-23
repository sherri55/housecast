// Development console for the native engine, for live checks against real speakers.
// usage:
//   EngineSpike [info] ["speaker name"]       list speakers, GET /info, transient pairing, encrypted GET /info
//   EngineSpike pin "speaker name" pinFile    PIN pairing: the TV shows a PIN; write it to pinFile.
//                                             Then reconnects with pair-verify and the saved keys.
//   EngineSpike tone "name[,name…]" [joiner]  beeps on several speakers in sync for 20 s; the joiner joins 8 s in
//   EngineSpike live "speaker name" [seconds] whatever Windows is playing, for 60 s by default
//   EngineSpike receive "name" [minutes] [wav] announces an AirPlay receiver, logs what senders do and writes
//                                             their audio to a WAV as it falls due; 10 min by default
//   EngineSpike relay "group" ["a,b"] [min]  announces a group of speakers (Bedroom 1 and 2 by default); what an
//                                             iPhone plays to it plays on them, in sync with the phone
using AirPlay.Engine;
using AirPlay.Engine.Audio;
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Ptp;
using AirPlay.Engine.Receiver;
using AirPlay.Engine.Rtsp;
using Claunia.PropertyList;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

var mode = args.Length > 0 && args[0] is "info" or "pin" or "tone" or "live" or "receive" or "relay" ? args[0] : "info";
var rest = mode == args.FirstOrDefault() ? args[1..] : args;
var target = rest.Length > 0 ? rest[0] : "Bedroom 2";

if (mode == "receive")
{
    var receiverKeys = new KeyStore(Path.Combine(AppContext.BaseDirectory, "spike-pairings.bin"));
    var wavPath = rest.Length > 2 ? rest[2] : Path.Combine(AppContext.BaseDirectory, "received.wav");
    await using var clock = new PtpMaster();
    clock.Start();
    using (var wav = new WaveFileWriter(wavPath, new WaveFormat(44100, 16, 2)))
    {
        var frames = 0;
        await using var receiver = await AirPlayReceiver.StartAsync(target, receiverKeys, clock.Follower, TimeSpan.Zero);
        receiver.Logged += message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        receiver.AudioDue += audio =>
        {
            wav.Write(audio.Pcm);
            if (++frames % 431 == 0)
            {
                // Keeps the header current, so the file plays even if this is stopped
                wav.Flush();
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}   {wav.TotalTime:mm\\:ss} written; this frame was due {(audio.PlayAtNanoseconds - EngineClock.NowNanoseconds) / 1_000_000} ms from now");
            }
        };
        var minutes = rest.Length > 1 ? int.Parse(rest[1]) : 10;
        Console.WriteLine($"'{target}' is announced for {minutes} min; pick it in an iPhone's AirPlay list. Audio goes to {wavPath}");
        await Task.Delay(TimeSpan.FromMinutes(minutes));
    }
    return;
}

if (mode == "relay")
{
    await using var engine = new AirPlayEngine("EngineSpike", new KeyStore(Path.Combine(AppContext.BaseDirectory, "spike-pairings.bin")));
    engine.GroupPlaybackChanged += (group, sender) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {group}: {(sender is null ? "stopped; speakers given back" : $"playing from {sender}")}");
    engine.ReceiverLogged += message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    var memberNames = rest.Length > 1 ? rest[1].Split(',') : ["Bedroom 1", "Bedroom 2"];
    // The engine scans every 10 s; wait up to a minute for the members
    List<AirPlaySpeaker?> members = [];
    for (var wait = 0; wait < 60; wait++)
    {
        members = memberNames.Select(name => engine.Speakers.FirstOrDefault(s => s.Name == name)).ToList();
        if (!members.Contains(null))
            break;
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
    if (members.Contains(null))
    {
        Console.WriteLine($"Not all of {string.Join(", ", memberNames)} were found; saw {string.Join(", ", engine.Speakers.Select(s => s.Name))}");
        return;
    }
    await engine.PublishGroupsAsync([new AirPlayGroup(target, members.Select(s => new GroupMember(s!.Id, 40, 0)).ToList())]);
    var relayMinutes = rest.Length > 2 ? int.Parse(rest[2]) : 15;
    Console.WriteLine($"'{target}' ({string.Join(", ", memberNames)}) is announced for {relayMinutes} min; play to it from an iPhone");
    await Task.Delay(TimeSpan.FromMinutes(relayMinutes));
    return;
}

Console.WriteLine("Looking for AirPlay speakers…");
var speakers = await SpeakerBrowser.BrowseAsync(TimeSpan.FromSeconds(8));
foreach (var s in speakers)
    Console.WriteLine($"  {s.Name,-22} {s.Id,-18} {s.Address,-15} :{s.Port,-5} {s.Manufacturer} {s.Model,-24} AP2 audio={s.SupportsAirPlay2Audio} PTP={s.SupportsPtp}");

var speaker = speakers.FirstOrDefault(s => s.Name == target.Split(',')[0]);
if (speaker is null)
{
    Console.WriteLine($"'{target}' wasn't found.");
    return;
}

var keyStore = new KeyStore(Path.Combine(AppContext.BaseDirectory, "spike-pairings.bin"));
var clientId = Convert.ToUInt64(keyStore.ClientId, 16);

if (mode == "tone")
{
    // A short 440 Hz beep every half second for 20 s, on every named speaker at once: beeps
    // that don't line up are heard as echoes. A speaker named after them joins 8 s in.
    var named = target.Split(',').Select(name => speakers.FirstOrDefault(s => s.Name == name)).ToArray();
    var joiner = rest.Length > 1 ? speakers.FirstOrDefault(s => s.Name == rest[1]) : null;
    if (named.Contains(null) || (rest.Length > 1 && joiner is null))
    {
        Console.WriteLine("A speaker wasn't found.");
        return;
    }

    await using var ptp = new PtpMaster();
    ptp.Start();
    await using var streamer = new AirPlayStreamer(ptp, clientId, "EngineSpike");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    await Task.WhenAll(named.Select(s => streamer.AddSpeakerAsync(s!, volumePercent: 25)));
    Console.WriteLine($"\nSessions set up with {target} in {stopwatch.ElapsedMilliseconds} ms; beeping for 20 s");

    var frame = new short[AlacFramer.SamplesPerFrame * 2];
    long samplesSent = 0;
    Task? joining = null;
    stopwatch.Restart();
    var nextReport = TimeSpan.FromSeconds(4);
    while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
    {
        var due = (long)(stopwatch.Elapsed.TotalSeconds * AirPlayStreamer.SampleRate);
        while (samplesSent < due)
        {
            for (var i = 0; i < AlacFramer.SamplesPerFrame; i++)
            {
                var sample = samplesSent + i;
                var beeping = sample % (AirPlayStreamer.SampleRate / 2) < AirPlayStreamer.SampleRate / 20;
                var value = beeping ? (short)(6000 * Math.Sin(2 * Math.PI * 440 * sample / AirPlayStreamer.SampleRate)) : (short)0;
                frame[2 * i] = value;
                frame[2 * i + 1] = value;
            }
            streamer.Send(frame);
            samplesSent += AlacFramer.SamplesPerFrame;
        }
        if (joiner is not null && joining is null && stopwatch.Elapsed > TimeSpan.FromSeconds(8))
        {
            Console.WriteLine($"  {stopwatch.Elapsed.TotalSeconds:0}s: {joiner.Name} joins");
            joining = streamer.AddSpeakerAsync(joiner, volumePercent: 25);
        }
        if (stopwatch.Elapsed > nextReport)
        {
            Console.WriteLine($"  {stopwatch.Elapsed.TotalSeconds:0}s: {streamer.Diagnostics}");
            nextReport += TimeSpan.FromSeconds(4);
        }
        await Task.Delay(2);
    }
    if (joining is not null)
        await joining;
    // Speakers play about 2 s behind; let the last beeps finish before tearing down
    await Task.Delay(TimeSpan.FromSeconds(3));
    Console.WriteLine($"  sent {samplesSent / AlacFramer.SamplesPerFrame} packets; {streamer.Diagnostics}");
}
else if (mode == "live")
{
    // Through the facade the app uses: its own discovery, play, and pacing
    var duration = TimeSpan.FromSeconds(rest.Length > 1 ? int.Parse(rest[1]) : 60);
    await using var engine = new AirPlayEngine("EngineSpike", keyStore);
    while (engine.Speakers.All(s => s.Id != speaker.Id))
        await Task.Delay(250);
    var result = await engine.PlayAsync(speaker.Id, volume: 25, offsetMilliseconds: 0);
    Console.WriteLine($"\n{speaker.Name}: {result}");
    if (result != PlayResult.Playing)
        return;

    // Loopback capture, converted to 44.1 kHz 16-bit stereo as the app does
    using var devices = new MMDeviceEnumerator();
    using var capture = new WasapiLoopbackCapture(devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
    var captured = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true, ReadFully = false };
    var converted = new SampleToWaveProvider16(new WdlResamplingSampleProvider(new MultiplexingSampleProvider([captured.ToSampleProvider()], 2), AirPlayStreamer.SampleRate));
    var bytes = new byte[4096];
    capture.DataAvailable += (_, e) =>
    {
        captured.AddSamples(e.Buffer, 0, e.BytesRecorded);
        int read;
        while ((read = converted.Read(bytes, 0, bytes.Length)) > 0)
            engine.Write(bytes.AsSpan(0, read));
    };
    capture.StartRecording();
    Console.WriteLine($"Streaming this PC's sound to {speaker.Name} for {duration.TotalSeconds:0} s");

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    while (stopwatch.Elapsed < duration)
    {
        await Task.Delay(TimeSpan.FromSeconds(10) < duration - stopwatch.Elapsed ? TimeSpan.FromSeconds(10) : duration - stopwatch.Elapsed);
        Console.WriteLine($"  {stopwatch.Elapsed.TotalSeconds:0}s: playing {engine.IsPlaying(speaker.Id)}");
    }
    capture.StopRecording();
    await Task.Delay(TimeSpan.FromSeconds(2.5));
}
else if (mode == "info")
{
    await using var rtsp = await ConnectAsync();
    await PrintInfoAsync(rtsp, "GET /info");

    Console.WriteLine($"\nTransient pairing with {speaker.Name}");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var sharedSecret = await HomeKitPairing.PairTransientAsync(rtsp);
    Console.WriteLine($"  paired in {stopwatch.ElapsedMilliseconds} ms, shared secret {sharedSecret.Length} bytes");
    await PrintInfoAsync(rtsp, "encrypted GET /info");
}
else
{
    var pinFile = rest[1];
    File.Delete(pinFile);

    await using (var rtsp = await ConnectAsync())
    {
        await rtsp.SendAsync("GET", "/info");
        await HomeKitPairing.RequestPinAsync(rtsp);
        Console.WriteLine($"\n{speaker.Name} should be showing a PIN. Waiting for it in {pinFile}…");
        while (!File.Exists(pinFile) || new FileInfo(pinFile).Length == 0)
            await Task.Delay(500);
        var pin = File.ReadAllText(pinFile).Trim();

        var keys = await HomeKitPairing.PairWithPinAsync(rtsp, pin, keyStore.ClientId);
        keyStore.Save(speaker.Id, keys);
        Console.WriteLine($"  paired with PIN; receiver id {keys.ReceiverId}, keys saved");
    }

    Console.WriteLine($"\nReconnecting to {speaker.Name} with pair-verify and the saved keys");
    await using var again = await ConnectAsync();
    await again.SendAsync("GET", "/info");
    var secret = await HomeKitPairing.VerifyAsync(again, keyStore.Find(speaker.Id)!, keyStore.ClientId);
    Console.WriteLine($"  verified, shared secret {secret.Length} bytes");
    await PrintInfoAsync(again, "encrypted GET /info");
}

async Task<RtspClient> ConnectAsync()
{
    var rtsp = new RtspClient(clientId, "EngineSpike");
    await rtsp.ConnectAsync(speaker.Address, speaker.Port);
    return rtsp;
}

static async Task PrintInfoAsync(RtspClient rtsp, string label)
{
    var reply = await rtsp.SendAsync("GET", "/info");
    Console.WriteLine($"  {label}: {reply.StatusCode} {reply.Reason}, {reply.Body.Length} bytes");
    if (reply.Body.Length > 0 && PropertyListParser.Parse(reply.Body) is NSDictionary info)
        Console.WriteLine($"  name {info["name"]}, model {info["model"]}, statusFlags 0x{((NSNumber)info["statusFlags"]).ToLong():X}");
}
