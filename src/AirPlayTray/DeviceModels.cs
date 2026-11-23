namespace AirPlayTray;

/// <summary>
/// What each AirPlay device says it is, from the model and maker in its network announcement.
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
    /// The icon and description for a device, from the model and maker it announces.
    /// </summary>
    public static DeviceModel Describe(string model, string maker)
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
}
