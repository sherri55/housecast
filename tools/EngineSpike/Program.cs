// Development console for the native engine, for live checks against real speakers.
// usage:
//   EngineSpike [info] ["speaker name"]       list speakers, GET /info, transient pairing, encrypted GET /info
//   EngineSpike pin "speaker name" pinFile    PIN pairing: the TV shows a PIN; write it to pinFile.
//                                             Then reconnects with pair-verify and the saved keys.
//   EngineSpike tone "name[,name…]" [joiner]  beeps on several speakers in sync for 20 s; the joiner joins 8 s in
//   EngineSpike live "speaker name" [seconds] whatever Windows is playing, for 60 s by default
using AirPlay.Engine;
using AirPlay.Engine.Audio;
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Rtsp;
using Claunia.PropertyList;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

var mode = args.Length > 0 && args[0] is "info" or "pin" or "tone" or "live" ? args[0] : "info";
var rest = mode == args.FirstOrDefault() ? args[1..] : args;
var target = rest.Length > 0 ? rest[0] : "Bedroom 2";

Console.WriteLine("Looking for AirPlay speakers…");
var speakers = await SpeakerBrowser.BrowseAsync(TimeSpan.FromSeconds(8));
foreach (var s in speakers)
    Console.WriteLine($"  {s.Name,-22} {s.Address,-15} :{s.Port,-5} {s.Manufacturer} {s.Model,-24} AP2 audio={s.SupportsAirPlay2Audio} PTP={s.SupportsPtp}");

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

    await using var streamer = new AirPlayStreamer(clientId, "EngineSpike");
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
    var duration = TimeSpan.FromSeconds(rest.Length > 1 ? int.Parse(rest[1]) : 60);
    await using var streamer = new AirPlayStreamer(clientId, "EngineSpike");
    await streamer.AddSpeakerAsync(speaker, volumePercent: 25);

    // Loopback capture, converted to 44.1 kHz 16-bit stereo as the app does
    using var devices = new MMDeviceEnumerator();
    using var capture = new WasapiLoopbackCapture(devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
    var captured = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true, ReadFully = false };
    var converted = new SampleToWaveProvider16(new WdlResamplingSampleProvider(new MultiplexingSampleProvider([captured.ToSampleProvider()], 2), AirPlayStreamer.SampleRate));
    capture.DataAvailable += (_, e) => captured.AddSamples(e.Buffer, 0, e.BytesRecorded);
    capture.StartRecording();
    Console.WriteLine($"\nStreaming this PC's sound to {speaker.Name} for {duration.TotalSeconds:0} s");

    // Frames go out at the speaker's rate, paced by our clock. Capture arrives in bursts, so a
    // short wait is allowed for it; past that, Windows is silent and silence fills the gap.
    // If capture runs faster than our clock, the excess is dropped to keep the delay fixed.
    var frameDuration = TimeSpan.FromSeconds((double)AlacFramer.SamplesPerFrame / AirPlayStreamer.SampleRate);
    var waitForCapture = TimeSpan.FromMilliseconds(60);
    var mostBuffered = TimeSpan.FromMilliseconds(120);
    var frameBytes = new byte[AlacFramer.SamplesPerFrame * 4];
    var frame = new short[AlacFramer.SamplesPerFrame * 2];
    long framesSent = 0, silentFrames = 0, droppedFrames = 0;
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var nextReport = TimeSpan.FromSeconds(10);
    while (stopwatch.Elapsed < duration)
    {
        while (framesSent * frameDuration < stopwatch.Elapsed)
        {
            var behind = stopwatch.Elapsed - framesSent * frameDuration;
            if (captured.BufferedDuration > frameDuration)
            {
                var read = converted.Read(frameBytes, 0, frameBytes.Length);
                Array.Clear(frameBytes, read, frameBytes.Length - read);
                if (captured.BufferedDuration > mostBuffered)
                {
                    droppedFrames++;
                    continue;
                }
            }
            else if (behind > waitForCapture)
            {
                Array.Clear(frameBytes);
                silentFrames++;
            }
            else
                break;

            Buffer.BlockCopy(frameBytes, 0, frame, 0, frameBytes.Length);
            streamer.Send(frame);
            framesSent++;
        }
        if (stopwatch.Elapsed > nextReport)
        {
            Console.WriteLine($"  {stopwatch.Elapsed.TotalSeconds:0}s: {framesSent} frames, {silentFrames} silent, {droppedFrames} dropped; {streamer.Diagnostics}");
            nextReport += TimeSpan.FromSeconds(10);
        }
        await Task.Delay(2);
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
