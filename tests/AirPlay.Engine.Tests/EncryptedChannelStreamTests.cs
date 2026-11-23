using System.IO.Pipelines;
using System.Security.Cryptography;
using AirPlay.Engine.Pairing;

namespace AirPlay.Engine.Tests;

public class EncryptedChannelStreamTests
{
    [Fact]
    public async Task What_one_side_writes_the_other_side_reads()
    {
        var (sender, receiver) = Connect();
        var message = RandomNumberGenerator.GetBytes(3000);

        await sender.WriteAsync(message);
        var received = new byte[message.Length];
        await receiver.ReadExactlyAsync(received);

        Assert.Equal(message, received);
    }

    [Fact]
    public async Task Messages_are_split_into_1024_byte_blocks_with_a_length_and_tag_each()
    {
        var pipe = new Pipe();
        var key = RandomNumberGenerator.GetBytes(32);
        var sender = new EncryptedChannelStream(pipe.Writer.AsStream(), key, key);

        await sender.WriteAsync(new byte[1500]);
        await pipe.Writer.CompleteAsync();
        var wire = new MemoryStream();
        await pipe.Reader.AsStream().CopyToAsync(wire);
        var bytes = wire.ToArray();

        // (2 + 1024 + 16) + (2 + 476 + 16)
        Assert.Equal(1536, bytes.Length);
        Assert.Equal(new byte[] { 0x00, 0x04 }, bytes[..2]);
        Assert.Equal(new byte[] { 0xDC, 0x01 }, bytes[1042..1044]);
    }

    [Fact]
    public async Task A_tampered_block_is_rejected()
    {
        var pipe = new Pipe();
        var key = RandomNumberGenerator.GetBytes(32);
        var sender = new EncryptedChannelStream(pipe.Writer.AsStream(), key, key);
        await sender.WriteAsync("RTSP/1.0 200 OK\r\n"u8.ToArray());
        await pipe.Writer.CompleteAsync();
        var wire = new MemoryStream();
        await pipe.Reader.AsStream().CopyToAsync(wire);
        var bytes = wire.ToArray();
        bytes[5] ^= 0xFF;

        var receiver = new EncryptedChannelStream(new MemoryStream(bytes), key, key);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => receiver.ReadAsync(new byte[64]).AsTask());
    }

    [Fact]
    public void Control_channel_keys_differ_by_direction()
    {
        var (write, read) = HomeKitPairing.ControlChannelKeys(RandomNumberGenerator.GetBytes(64));

        Assert.Equal(32, write.Length);
        Assert.NotEqual(write, read);
    }

    // Two streams joined by in-memory pipes, keyed the way a sender and a receiver would be
    private static (EncryptedChannelStream Sender, EncryptedChannelStream Receiver) Connect()
    {
        var toReceiver = new Pipe();
        var toSender = new Pipe();
        var forward = RandomNumberGenerator.GetBytes(32);
        var backward = RandomNumberGenerator.GetBytes(32);
        var sender = new EncryptedChannelStream(new DuplexStream(toSender.Reader.AsStream(), toReceiver.Writer.AsStream()), forward, backward);
        var receiver = new EncryptedChannelStream(new DuplexStream(toReceiver.Reader.AsStream(), toSender.Writer.AsStream()), backward, forward);
        return (sender, receiver);
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override void Flush() => output.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
