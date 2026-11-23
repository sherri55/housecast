using System.Net;

namespace AirPlay.Engine.Discovery;

/// <summary>
/// An AirPlay receiver as announced on the network.
/// </summary>
/// <param name="Id">The announced <c>deviceid</c>, a MAC-style address that stays the same across restarts.</param>
/// <param name="Features">The <c>features</c> bit field; see <see cref="SpeakerBrowser.ParseFeatures"/>.</param>
public sealed record AirPlaySpeaker(
    string Id,
    string Name,
    IPAddress Address,
    int Port,
    string Model,
    string Manufacturer,
    ulong Features)
{
    // Feature bits as OwnTone reads them (outputs/airplay.c)
    private const int AudioBit = 9;
    private const int PtpBit = 41;
    private const int PairingAndEncryptionBit = 48;

    /// <summary>
    /// Takes AirPlay 2 audio: plays sound and uses HomeKit-style pairing and encryption.
    /// </summary>
    public bool SupportsAirPlay2Audio => HasFeature(AudioBit) && HasFeature(PairingAndEncryptionBit);

    /// <summary>
    /// Keeps time with PTP, which is how several speakers stay in sync.
    /// </summary>
    public bool SupportsPtp => HasFeature(PtpBit);

    private bool HasFeature(int bit) => (Features & (1UL << bit)) != 0;
}
