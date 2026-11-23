namespace AirPlay.Engine.Rtsp;

public sealed record RtspResponse(int StatusCode, string Reason, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
