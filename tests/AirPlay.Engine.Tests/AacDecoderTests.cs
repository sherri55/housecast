using System.Runtime.InteropServices;
using AirPlay.Engine.Audio;
using NAudio.MediaFoundation;

namespace AirPlay.Engine.Tests;

public class AacDecoderTests
{
    [Fact]
    public void A_tone_encoded_by_windows_decodes_back_to_the_same_tone()
    {
        var tone = Tone(440, seconds: 2);
        var frames = Encode(tone);

        using var decoder = new AacDecoder();
        var pcm = frames.SelectMany(frame => decoder.Decode(frame)).ToArray();
        var samples = MemoryMarshal.Cast<byte, short>(pcm).ToArray();

        // Roughly all the audio comes back, less what the codec keeps in its pipeline
        Assert.InRange(samples.Length, tone.Length - 4 * 2048, tone.Length + 2048);
        // Skip the codec's start-up, then count the left channel's zero crossings over one second
        var left = samples.Where((_, i) => i % 2 == 0).Skip(4096).Take(44100).ToArray();
        var crossings = left.Zip(left.Skip(1)).Count(pair => pair.First < 0 && pair.Second >= 0);
        Assert.InRange(crossings, 438, 442);
    }

    [Fact]
    public void Decoding_carries_on_after_a_reset()
    {
        var frames = Encode(Tone(440, seconds: 1));
        using var decoder = new AacDecoder();

        var before = frames.Take(20).Sum(frame => decoder.Decode(frame).Length);
        decoder.Reset();
        var after = frames.Skip(20).Take(20).Sum(frame => decoder.Decode(frame).Length);

        Assert.InRange(after, before - 4096, before + 4096);
    }

    internal static short[] Tone(double frequency, int seconds)
    {
        var samples = new short[44100 * seconds * 2];
        for (var i = 0; i < samples.Length / 2; i++)
            samples[2 * i] = samples[2 * i + 1] = (short)(8000 * Math.Sin(2 * Math.PI * frequency * i / 44100));
        return samples;
    }

    // Raw AAC-LC frames from Windows' own AAC encoder, as an iPhone sends them
    internal static List<byte[]> Encode(short[] pcm)
    {
        MediaFoundationApi.Startup();
        var encoder = (IMFTransform)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("93AF0C51-2275-45d2-A35B-F2BA21CAED00"), throwOnError: true)!)!;

        var output = new MediaType { MajorType = MediaTypes.MFMediaType_Audio, SubType = AudioSubtypes.MFAudioFormat_AAC, SampleRate = 44100, ChannelCount = 2, BitsPerSample = 16 };
        output.SetUInt32(MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 16000);
        output.SetUInt32(MediaFoundationAttributes.MF_MT_AAC_PAYLOAD_TYPE, 0);
        encoder.SetOutputType(0, output.MediaFoundationObject, 0);
        var input = new MediaType { MajorType = MediaTypes.MFMediaType_Audio, SubType = AudioSubtypes.MFAudioFormat_PCM, SampleRate = 44100, ChannelCount = 2, BitsPerSample = 16 };
        input.SetUInt32(MediaFoundationAttributes.MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
        input.SetUInt32(MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 44100 * 4);
        encoder.SetInputType(0, input.MediaFoundationObject, 0);
        encoder.GetOutputStreamInfo(0, out var info);

        var frames = new List<byte[]>();
        var bytes = MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
        const int chunk = 1024 * 4;
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            var length = Math.Min(chunk, bytes.Length - offset);
            var sample = MediaFoundationApi.CreateSample();
            var buffer = MediaFoundationApi.CreateMemoryBuffer(length);
            buffer.Lock(out var pointer, out _, out _);
            Marshal.Copy(bytes, offset, pointer, length);
            buffer.Unlock();
            buffer.SetCurrentLength(length);
            sample.AddBuffer(buffer);
            sample.SetSampleTime(offset / 4 * 10_000_000L / 44100);
            sample.SetSampleDuration(length / 4 * 10_000_000L / 44100);
            encoder.ProcessInput(0, sample, 0);

            while (true)
            {
                var outSample = MediaFoundationApi.CreateSample();
                var outBuffer = MediaFoundationApi.CreateMemoryBuffer(Math.Max(info.cbSize, 8192));
                outSample.AddBuffer(outBuffer);
                var result = encoder.ProcessOutput(0, 1, [new MFT_OUTPUT_DATA_BUFFER { pSample = outSample }], out _);
                if (result != 0)
                    break;
                outBuffer.Lock(out var outPointer, out _, out var outLength);
                var frame = new byte[outLength];
                Marshal.Copy(outPointer, frame, 0, outLength);
                outBuffer.Unlock();
                frames.Add(frame);
            }
        }
        Marshal.ReleaseComObject(encoder);
        return frames;
    }
}
