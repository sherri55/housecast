using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AirPlay.Engine.Pairing;
using Claunia.PropertyList;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace AirPlay.Engine.Receiver;

/// <summary>
/// Who an announced receiver says it is: its device id, UUIDs and key, its two TXT records,
/// and its GET /info reply. The values are shairport-sync's (bonjour_strings.c, rtsp.c), which
/// iOS accepts as an AirPlay 2 speaker.
/// </summary>
internal sealed class ReceiverIdentity
{
    // AirPlay 2 audio receiver features, as shairport-sync announces them (shairport.c)
    public const ulong Features = 0x0001_C340_401C_4A00UL;
    // "Audio cable attached"
    public const int StatusFlags = 0x4;
    private const string SourceVersion = "366.0";
    private const string Model = "Housecast";

    // What we accept: realtime ALAC 44.1 kHz 16-bit stereo; buffered AAC-LC 44.1 kHz stereo only,
    // so the buffered stream is always one Windows can decode
    private const long AudioStreamFormats = 0x40000;
    private const long BufferStreamFormats = 0x400000;

    public ReceiverIdentity(string name, KeyStore keyStore)
    {
        Name = name;
        DeviceIdBytes = DeviceIdFor(name);
        DeviceId = string.Join(':', DeviceIdBytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        PrivateKey = keyStore.ReceiverKey(DeviceId);
        PublicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(PrivateKey, 0, PublicKey, 0);
        Pi = UuidFrom(MD5.HashData(Encoding.UTF8.GetBytes("pi:" + DeviceId)));
        Psi = UuidFrom(MD5.HashData(DeviceIdBytes));
    }

    public string Name { get; }
    public byte[] DeviceIdBytes { get; }

    /// <summary>"AA:BB:CC:DD:EE:FF", as senders see it.</summary>
    public string DeviceId { get; }

    public byte[] PrivateKey { get; }
    public byte[] PublicKey { get; }
    public string Pi { get; }
    public string Psi { get; }

    /// <summary>The _raop._tcp instance name: the device id's 12 hex digits, '@', the name.</summary>
    public string RaopName => $"{Convert.ToHexString(DeviceIdBytes)}@{Name}";

    private static string FeaturesText => $"0x{Features & 0xFFFF_FFFF:X},0x{Features >> 32:X}";

    // The features as 8 little-endian bytes in base64, without padding
    private static string FeaturesEx
    {
        get
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, Features);
            return Convert.ToBase64String(bytes).TrimEnd('=');
        }
    }

    private string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    public IReadOnlyList<KeyValuePair<string, string>> AirPlayTxt =>
    [
        new("acl", "0"),
        new("deviceid", DeviceId),
        new("features", FeaturesText),
        new("fex", FeaturesEx),
        new("flags", $"0x{StatusFlags:X}"),
        new("gid", Pi),
        new("igl", "0"),
        new("gcgl", "0"),
        new("model", Model),
        new("protovers", "1.1"),
        new("pi", Pi),
        new("psi", Psi),
        new("pk", PublicKeyHex),
        new("srcvers", SourceVersion),
    ];

    public IReadOnlyList<KeyValuePair<string, string>> RaopTxt =>
    [
        new("cn", "0,1"),
        new("da", "true"),
        new("et", "0,1"),
        new("ft", FeaturesText),
        new("sf", $"0x{StatusFlags:X}"),
        new("am", Model),
        new("pk", PublicKeyHex),
        new("tp", "UDP"),
        new("vn", "65537"),
        new("vs", SourceVersion),
    ];

    /// <summary>The GET /info reply.</summary>
    /// <param name="senderAddress">"ip:port" of the sender asking.</param>
    public NSDictionary Info(string senderAddress, double volumeDecibels)
    {
        // The TXT record again, as length-prefixed "key=value" strings
        var txt = new MemoryStream();
        foreach (var (key, value) in AirPlayTxt.Append(new("btaddr", "00:00:00:00:00:00")).Append(new("vv", "2")))
        {
            var entry = Encoding.UTF8.GetBytes($"{key}={value}");
            txt.WriteByte((byte)entry.Length);
            txt.Write(entry);
        }

        return new NSDictionary
        {
            { "playbackCapabilities", new NSDictionary
                {
                    { "supportsInterstitials", false },
                    { "supportsFPSSecureStop", false },
                    { "supportsUIForAudioOnlyContent", false },
                }
            },
            { "canRecordScreenStream", false },
            { "keepAliveSendStatsAsBody", false },
            { "protocolVersion", "1.1" },
            { "screenDemoMode", false },
            { "receiverHDRCapability", "4k60" },
            { "psi", Psi },
            { "featuresEx", FeaturesEx },
            { "features", new NSNumber(unchecked((long)Features)) },
            { "statusFlags", StatusFlags },
            { "deviceID", DeviceId },
            { "pi", Pi },
            { "name", Name },
            { "model", Model },
            { "pk", new NSData(PublicKey) },
            { "senderAddress", senderAddress },
            { "initialVolume", volumeDecibels },
            { "vv", 0 },
            { "volumeControlType", 3 },
            { "supportedFormats", new NSDictionary
                {
                    { "audioStream", AudioStreamFormats },
                    { "bufferStream", BufferStreamFormats },
                }
            },
            { "txtAirPlay", new NSData(txt.ToArray()) },
        };
    }

    // A stable id per name, as shairport-sync does for several instances on one machine:
    // FNV-1a of the name, marked as a locally administered unicast address
    private static byte[] DeviceIdFor(string name)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(name))
            hash = (hash ^ b) * 1099511628211UL;
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, hash);
        var id = bytes[2..];
        id[0] = (byte)((id[0] | 0x02) & 0xFE);
        return id;
    }

    // A version 4 style UUID from 16 hash bytes
    private static string UuidFrom(byte[] hash)
    {
        hash[6] = (byte)((hash[6] & 0x0F) | 0x40);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(hash);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
