namespace AirPlay.Engine.Receiver;

/// <summary>
/// The answers to a sender's /fp-setup (FairPlay) messages. AirPlay 2 audio doesn't depend
/// on FairPlay, so, as in shairport-sync (rtsp.c, handle_fp_setup, MIT), fixed replies satisfy it.
/// </summary>
internal static class FairPlaySetup
{
    // One reply per mode (byte 14 of the first message), copied from shairport-sync
    private static readonly byte[][] FirstReplies =
    [
        Convert.FromHexString(
            "46504C59030102000000008202000F9F3F9E0A2521DBDF312AB2BFB29E8D232B6376A8C818701D22AE93D82737FEAF9D" +
            "B4FDF41C2DBA9D1F49CAAABF6591AC1F7BC6F7E0663D21AFE01565953EAB81F418CEED095ADB7C3D0E254909A79831D4" +
            "9C3982973434FACB42C63A1CD911A6FE941A8A6D4A743B46C3A7649E44C78955E49D8155009549C4E2F7A3F6D5BA"),
        Convert.FromHexString(
            "46504C5903010200000000820201CF32A25714B2524F8AA0AD7AF164E37BCF4424E200047EFC0AD67AFCD95DED1C2730" +
            "BB591B962ED63A9C4DED88BA8FC78DE64D91CCFD5C7B56DA88E31F5CCEAFC7431995A01665A54E1939D25B94DB64B9E4" +
            "5D8D063E1E6AF07E9656162B0EFA404275EA5A44D9591C7256B9FBE6513898B80227721988571650942AD946688A"),
        Convert.FromHexString(
            "46504C5903010200000000820202C169A352EEED35B18CDD9C58D64F16C1519A89EB5317BD0D4336CD68F638FF9D016A" +
            "5B52B7FA9216B2B65482C78444118121A2C7FED83DB7119E9182AAD7D18C7063E2A457555910AF9E0EFC76347D164043" +
            "807F581EE4FBE42CA9DEDC1B5EB2A3AA3D2ECD59E7EEE70B3629F22AFD161D877353DDB99ADC8E07006E56F850CE"),
        Convert.FromHexString(
            "46504C59030102000000008202039001E1727E0F57F9F5880DB104A6257A23F5CFFF1ABBE1E93045251AFB97EB9FC001" +
            "1EBE0F3A81DF5B691D76ACB2F7A5C708E3D328F56BB39DBDE5F29C8A17F481487E3AE863C678325422E6F78E166D18AA" +
            "7FD636258BCE28726F661F738893CE44311E4BE6C0535193E5EF72E8686233729C227D820C999445D89246C8C359"),
    ];

    private static readonly byte[] SecondReplyHeader = Convert.FromHexString("46504C590301040000000014");

    /// <returns>The reply, or null for a message that isn't FairPlay setup version 3.</returns>
    public static byte[]? Reply(byte[] request)
    {
        if (request.Length < 15 || request[4] != 3 || request[5] != 1)
            return null;
        return request[6] switch
        {
            1 when request[14] < FirstReplies.Length => FirstReplies[request[14]],
            // The second reply echoes the last 20 bytes of the request
            3 when request.Length >= 20 => [.. SecondReplyHeader, .. request[^20..]],
            _ => null,
        };
    }
}
