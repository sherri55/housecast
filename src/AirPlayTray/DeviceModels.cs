using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AirPlayTray;

/// <summary>
/// What each AirPlay device says it is, read from its network announcement inside WSL,
/// because OwnTone's API doesn't pass the model on.
/// </summary>
public static class DeviceModels
{
    /// <summary>
    /// Icon is a Segoe Fluent Icons glyph, the icon font built into Windows 11.
    /// </summary>
    public sealed record DeviceModel(string Icon, string Description);

    public const string SpeakerIcon = "\uE7F5";
    public const string TvIcon = "\uE7F4";
    public const string StreamingBoxIcon = "\uEC15";
    public const string HomeTheaterIcon = "\uE7F3";
    public const string ReceiverIcon = "\uE8D6";
    public const string ComputerIcon = "\uE977";
    public const string GroupIcon = "\uE80F";

    /// <summary>
    /// Returns each announced device's model, keyed by the name OwnTone also uses.
    /// </summary>
    public static async Task<Dictionary<string, DeviceModel>> ReadAsync(string distro)
    {
        using var browse = Process.Start(new ProcessStartInfo
        {
            FileName = "wsl.exe",
            ArgumentList = { "-d", distro, "--", "avahi-browse", "--resolve", "--parsable", "--terminate", "_airplay._tcp" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = await browse.StandardOutput.ReadToEndAsync();
        await browse.WaitForExitAsync();

        var models = new Dictionary<string, DeviceModel>();
        // Resolved lines: =;interface;protocol;name;type;domain;host;address;port;"key=value" "key=value"...
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split(';');
            if (fields.Length < 10 || fields[0] != "=")
                continue;

            var name = Unescape(fields[3]);
            var model = TxtValue(fields[9], "model");
            var maker = TxtValue(fields[9], "manufacturer");
            models[name] = Describe(model, maker);
        }
        return models;
    }

    private static DeviceModel Describe(string model, string maker)
    {
        // "Yamaha Corporation" reads better as "Yamaha"
        var brand = maker.Split(' ')[0];
        brand = brand.Length > 1 && brand.All(char.IsUpper) ? brand[0] + brand[1..].ToLowerInvariant() : brand;
        var named = model.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{brand} {model}".Trim();

        if (model.StartsWith("AppleTV", StringComparison.Ordinal))
            return new DeviceModel(StreamingBoxIcon, "Apple TV");
        if (model.StartsWith("AudioAccessory", StringComparison.Ordinal))
            return new DeviceModel(SpeakerIcon, "HomePod");
        if (brand is "Samsung" or "LG" || model.Contains("TV", StringComparison.Ordinal))
            return new DeviceModel(TvIcon, $"{brand} TV".Trim());
        if (model.StartsWith("HT-", StringComparison.Ordinal) || model.Contains("Bar", StringComparison.OrdinalIgnoreCase))
            return new DeviceModel(HomeTheaterIcon, named);
        if (model.StartsWith("RX-", StringComparison.Ordinal))
            return new DeviceModel(ReceiverIcon, named);
        return new DeviceModel(SpeakerIcon, named);
    }

    // Avahi writes spaces and punctuation in names as \DDD decimal escapes, e.g. \032 for a space
    private static string Unescape(string text) =>
        Regex.Replace(text, @"\\(\d{3})", m => ((char)int.Parse(m.Groups[1].Value)).ToString());

    private static string TxtValue(string txt, string key)
    {
        var match = Regex.Match(txt, $"\"{key}=([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }
}
