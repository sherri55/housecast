using System.Globalization;

namespace AirPlay.Engine.Discovery;

/// <summary>
/// Finds AirPlay receivers through their mDNS announcements.
/// </summary>
public static class SpeakerBrowser
{
    public const string ServiceType = "_airplay._tcp.local";

    public static async Task<IReadOnlyList<AirPlaySpeaker>> BrowseAsync(TimeSpan scanTime, CancellationToken cancellationToken = default)
    {
        var instanceNames = await WindowsDnsSd.BrowseAsync(ServiceType, scanTime, cancellationToken);
        var instances = await Task.WhenAll(instanceNames.Select(name =>
            WindowsDnsSd.ResolveAsync(name, TimeSpan.FromSeconds(3), cancellationToken)));

        return instances
            .Where(i => i?.Address is not null)
            .Select(i => new AirPlaySpeaker(
                Id: i!.Txt.GetValueOrDefault("deviceid", i.InstanceName),
                // "Bedroom 2._airplay._tcp.local" is shown as "Bedroom 2"
                Name: i.InstanceName.Split("._airplay.")[0],
                Address: i.Address!,
                Port: i.Port,
                Model: i.Txt.GetValueOrDefault("model", ""),
                Manufacturer: i.Txt.GetValueOrDefault("manufacturer", ""),
                Features: ParseFeatures(i.Txt.GetValueOrDefault("features", ""))))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads the <c>features</c> TXT value: one hex number for the low 32 bits,
    /// optionally a comma and a second for the high 32 bits, e.g. "0x4A7FDFD5,0x3C175FDE".
    /// </summary>
    public static ulong ParseFeatures(string text)
    {
        var parts = text.Split(',');
        var low = ParseHex(parts[0]);
        var high = parts.Length > 1 ? ParseHex(parts[1]) : 0;
        return low | (high << 32);
    }

    private static ulong ParseHex(string part)
    {
        var digits = part.Trim();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];
        return ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
