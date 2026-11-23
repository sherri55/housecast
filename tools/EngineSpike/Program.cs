// Development console for the native engine: lists AirPlay speakers, then asks one for /info.
// usage: EngineSpike ["speaker name"]
using AirPlay.Engine.Discovery;
using AirPlay.Engine.Rtsp;
using Claunia.PropertyList;

var target = args.Length > 0 ? args[0] : "Bedroom 2";

Console.WriteLine("Looking for AirPlay speakers…");
var speakers = await SpeakerBrowser.BrowseAsync(TimeSpan.FromSeconds(4));
foreach (var s in speakers)
    Console.WriteLine($"  {s.Name,-22} {s.Address,-15} :{s.Port,-5} {s.Manufacturer} {s.Model,-24} AP2 audio={s.SupportsAirPlay2Audio} PTP={s.SupportsPtp}");

var speaker = speakers.FirstOrDefault(s => s.Name == target);
if (speaker is null)
{
    Console.WriteLine($"'{target}' wasn't found.");
    return;
}

Console.WriteLine($"\nGET /info from {speaker.Name}");
await using var rtsp = new RtspClient((ulong)Random.Shared.NextInt64(), "EngineSpike");
await rtsp.ConnectAsync(speaker.Address, speaker.Port);
var reply = await rtsp.SendAsync("GET", "/info");
Console.WriteLine($"  {reply.StatusCode} {reply.Reason}, {reply.Body.Length} bytes, Content-Type {reply.Headers.GetValueOrDefault("Content-Type")}");

if (reply.Body.Length > 0 && PropertyListParser.Parse(reply.Body) is NSDictionary info)
{
    foreach (var key in new[] { "name", "model", "manufacturer", "statusFlags", "features", "deviceID", "sourceVersion", "pi", "pk" })
        if (info.TryGetValue(key, out var value))
            Console.WriteLine($"  {key,-14} {Describe(value)}");
    Console.WriteLine($"  all keys: {string.Join(", ", info.Keys.Order())}");
}

static string Describe(NSObject value) => value switch
{
    NSData data => Convert.ToHexString(data.Bytes),
    NSNumber number => $"{number} (0x{number.ToLong():X})",
    _ => value.ToString() ?? "",
};
