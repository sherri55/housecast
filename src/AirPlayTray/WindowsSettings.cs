using System.Text.Json;
using System.Windows.Media;
using Microsoft.Win32;

namespace AirPlayTray;

/// <summary>
/// Per-user settings kept in the registry: the Windows theme and accent colour the app follows,
/// starting with Windows, and the app's own settings: remembered speakers, their volume and
/// delay, and groups.
/// </summary>
public static class WindowsSettings
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AirPlayTray";
    private const string AppKey = @"Software\AirPlayTray";

    /// <summary>
    /// The Windows accent colour in the shade Windows 11 itself uses on a light or dark
    /// background, or null when the palette isn't there.
    /// </summary>
    public static Color? AccentColor(bool lightBackground)
    {
        // AccentPalette is eight RGBA colours from lightest to darkest. On dark backgrounds
        // Windows uses the second-lightest, on light backgrounds the first dark shade.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
        if (key?.GetValue("AccentPalette") is not byte[] { Length: >= 32 } palette)
            return null;

        var at = (lightBackground ? 4 : 1) * 4;
        return Color.FromRgb(palette[at], palette[at + 1], palette[at + 2]);
    }

    // Apps and the taskbar have separate settings, so the flyout and the tray icon can differ
    public static bool AppsUseLightTheme => ReadPersonalize("AppsUseLightTheme");

    public static bool TaskbarUsesLightTheme => ReadPersonalize("SystemUsesLightTheme");

    /// <summary>
    /// Ids of the speakers the user last ticked, played again when nothing is playing.
    /// </summary>
    public static string[] SelectedSpeakerIds
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey);
            return key?.GetValue(nameof(SelectedSpeakerIds)) as string[] ?? [];
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue(nameof(SelectedSpeakerIds), value, RegistryValueKind.MultiString);
        }
    }

    public sealed record SpeakerSetting(int Volume, int OffsetMs);

    private static readonly SpeakerSetting DefaultSpeakerSetting = new(50, 0);

    public static SpeakerSetting SpeakerSettingFor(string speakerId) =>
        ReadSpeakerSettings().GetValueOrDefault(speakerId, DefaultSpeakerSetting);

    public static void SaveSpeakerSetting(string speakerId, SpeakerSetting setting)
    {
        var settings = ReadSpeakerSettings();
        settings[speakerId] = setting;
        using var key = Registry.CurrentUser.CreateSubKey(AppKey);
        key.SetValue("SpeakerSettings", JsonSerializer.Serialize(settings), RegistryValueKind.String);
    }

    // Each speaker's volume and delay, stored as JSON keyed by speaker id
    private static Dictionary<string, SpeakerSetting> ReadSpeakerSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AppKey);
        return key?.GetValue("SpeakerSettings") is string json
            ? JsonSerializer.Deserialize<Dictionary<string, SpeakerSetting>>(json) ?? []
            : [];
    }

    /// <summary>Scales every speaker's volume, 0–100.</summary>
    public static int MasterVolume
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey);
            return key?.GetValue(nameof(MasterVolume)) as int? ?? 100;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue(nameof(MasterVolume), value, RegistryValueKind.DWord);
        }
    }

    public sealed record SpeakerGroup(string Name, string[] SpeakerIds);

    /// <summary>
    /// Named sets of speakers that play together, stored as JSON.
    /// </summary>
    public static SpeakerGroup[] Groups
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppKey);
            return key?.GetValue(nameof(Groups)) is string json
                ? JsonSerializer.Deserialize<SpeakerGroup[]>(json) ?? []
                : [];
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppKey);
            key.SetValue(nameof(Groups), JsonSerializer.Serialize(value), RegistryValueKind.String);
        }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    private static bool ReadPersonalize(string name)
    {
        // Windows treats a missing value as light
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is not int value || value != 0;
    }
}
