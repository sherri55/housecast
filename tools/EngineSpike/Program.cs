// Development console for the native engine, for live checks against real speakers.
// usage:
//   EngineSpike [info] ["speaker name"]       list speakers, GET /info, transient pairing, encrypted GET /info
//   EngineSpike pin "speaker name" pinFile    PIN pairing: the TV shows a PIN; write it to pinFile.
//                                             Then reconnects with pair-verify and the saved keys.
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Pairing;
using AirPlay.Engine.Rtsp;
using Claunia.PropertyList;

var mode = args.Length > 0 && args[0] is "info" or "pin" ? args[0] : "info";
var rest = mode == args.FirstOrDefault() ? args[1..] : args;
var target = rest.Length > 0 ? rest[0] : "Bedroom 2";

Console.WriteLine("Looking for AirPlay speakers…");
var speakers = await SpeakerBrowser.BrowseAsync(TimeSpan.FromSeconds(8));
foreach (var s in speakers)
    Console.WriteLine($"  {s.Name,-22} {s.Address,-15} :{s.Port,-5} {s.Manufacturer} {s.Model,-24} AP2 audio={s.SupportsAirPlay2Audio} PTP={s.SupportsPtp}");

var speaker = speakers.FirstOrDefault(s => s.Name == target);
if (speaker is null)
{
    Console.WriteLine($"'{target}' wasn't found.");
    return;
}

var keyStore = new KeyStore(Path.Combine(AppContext.BaseDirectory, "spike-pairings.bin"));
var clientId = Convert.ToUInt64(keyStore.ClientId, 16);

if (mode == "info")
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
