using System.Runtime.InteropServices;
using NAudio.MediaFoundation;

namespace AirPlay.Engine.Audio;

/// <summary>
/// Decodes raw AAC-LC 44.1 kHz stereo frames, as an AirPlay 2 buffered stream carries them,
/// to 16-bit PCM, with the AAC decoder built into Windows (Media Foundation).
/// </summary>
public sealed class AacDecoder : IDisposable
{
    private static readonly Guid AacDecoderClassId = new("32d186a7-218f-4c75-8876-dd77273a8999");
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);

    // AudioSpecificConfig: object type 2 (AAC-LC), sample rate index 4 (44.1 kHz), 2 channels
    private static readonly byte[] AudioSpecificConfig = [0x12, 0x10];

    private readonly IMFTransform _decoder;
    private readonly int _outputSize;

    public AacDecoder()
    {
        MediaFoundationApi.Startup();
        _decoder = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(AacDecoderClassId, throwOnError: true)!)!;

        var input = new MediaType
        {
            MajorType = MediaTypes.MFMediaType_Audio,
            SubType = AudioSubtypes.MFAudioFormat_AAC,
            SampleRate = 44100,
            ChannelCount = 2,
        };
        input.SetUInt32(MediaFoundationAttributes.MF_MT_AAC_PAYLOAD_TYPE, 0);
        // HEAACWAVEINFO's 12 bytes after its WAVEFORMATEX (all zero means raw AAC frames), then the config
        var userData = new byte[12 + AudioSpecificConfig.Length];
        AudioSpecificConfig.CopyTo(userData, 12);
        input.MediaFoundationObject.SetBlob(MediaFoundationAttributes.MF_MT_USER_DATA, userData, userData.Length);
        _decoder.SetInputType(0, input.MediaFoundationObject, 0);
        _decoder.SetOutputType(0, Pcm16OutputType(), 0);

        _decoder.GetOutputStreamInfo(0, out var info);
        _outputSize = Math.Max(info.cbSize, 2048 * 4);
        _decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
    }

    /// <summary>
    /// Decodes one frame. Returns interleaved 16-bit stereo PCM, normally 1024 frames' worth;
    /// may be empty while the decoder fills its pipeline.
    /// </summary>
    public byte[] Decode(ReadOnlySpan<byte> aacFrame)
    {
        var input = MediaFoundationApi.CreateSample();
        var buffer = MediaFoundationApi.CreateMemoryBuffer(aacFrame.Length);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            Marshal.Copy(aacFrame.ToArray(), 0, pointer, aacFrame.Length);
            buffer.Unlock();
            buffer.SetCurrentLength(aacFrame.Length);
            input.AddBuffer(buffer);
            _decoder.ProcessInput(0, input, 0);
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
            Marshal.ReleaseComObject(input);
        }

        using var pcm = new MemoryStream();
        while (ReadOutput(pcm))
        {
        }
        return pcm.ToArray();
    }

    /// <summary>Drops what the decoder holds, e.g. after a seek.</summary>
    public void Reset() => _decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero);

    // Appends one output sample to pcm; false once the decoder needs more input
    private bool ReadOutput(MemoryStream pcm)
    {
        var sample = MediaFoundationApi.CreateSample();
        var buffer = MediaFoundationApi.CreateMemoryBuffer(_outputSize);
        try
        {
            sample.AddBuffer(buffer);
            var output = new[] { new MFT_OUTPUT_DATA_BUFFER { pSample = sample } };
            var result = _decoder.ProcessOutput(0, 1, output, out _);
            if (result == NeedMoreInput)
                return false;
            if (result == StreamChange)
            {
                _decoder.SetOutputType(0, Pcm16OutputType(), 0);
                return true;
            }
            Marshal.ThrowExceptionForHR(result);

            buffer.Lock(out var pointer, out _, out var length);
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            buffer.Unlock();
            pcm.Write(bytes);
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
            Marshal.ReleaseComObject(sample);
        }
    }

    // The decoder offers 16-bit integer and float PCM; pick its 16-bit type, which carries
    // every attribute the decoder wants set
    private IMFMediaType Pcm16OutputType()
    {
        for (var index = 0; ; index++)
        {
            _decoder.GetOutputAvailableType(0, index, out var available);
            var type = new MediaType(available);
            if (type.SubType == AudioSubtypes.MFAudioFormat_PCM && type.BitsPerSample == 16)
                return available;
            Marshal.ReleaseComObject(available);
        }
    }

    public void Dispose()
    {
        _decoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
        Marshal.ReleaseComObject(_decoder);
    }
}
