using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AirPlay.Engine.Pairing;

/// <summary>
/// The encrypted framing AirPlay 2 uses on a channel after pairing. Each block is a 2-byte
/// little-endian length (also the authenticated data), up to 1024 bytes of ChaCha20-Poly1305
/// ciphertext and a 16-byte tag. Each direction counts blocks for its nonce.
/// Ported from encrypt/decrypt in OwnTone's pair_ap/pair_homekit.c (MIT).
/// </summary>
public sealed class EncryptedChannelStream : Stream
{
    private const int MaxBlockLength = 0x400;
    private const int TagLength = 16;

    private readonly Stream _inner;
    private readonly ChaCha20Poly1305 _writeCipher;
    private readonly ChaCha20Poly1305 _readCipher;
    private ulong _writeCounter;
    private ulong _readCounter;
    private byte[] _plain = [];
    private int _plainOffset;

    public EncryptedChannelStream(Stream inner, byte[] writeKey, byte[] readKey)
    {
        _inner = inner;
        _writeCipher = new ChaCha20Poly1305(writeKey);
        _readCipher = new ChaCha20Poly1305(readKey);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        for (var offset = 0; offset < buffer.Length; offset += MaxBlockLength)
        {
            var plain = buffer.Slice(offset, Math.Min(MaxBlockLength, buffer.Length - offset));
            var block = new byte[2 + plain.Length + TagLength];
            BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)plain.Length);
            _writeCipher.Encrypt(
                Nonce(_writeCounter++),
                plain.Span,
                block.AsSpan(2, plain.Length),
                block.AsSpan(2 + plain.Length, TagLength),
                block.AsSpan(0, 2));
            await _inner.WriteAsync(block, cancellationToken);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_plainOffset == _plain.Length)
        {
            var header = new byte[2];
            if (await _inner.ReadAtLeastAsync(header, 2, throwOnEndOfStream: false, cancellationToken) < 2)
                return 0;

            var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
            var sealedBlock = new byte[length + TagLength];
            await _inner.ReadExactlyAsync(sealedBlock, cancellationToken);

            _plain = new byte[length];
            _plainOffset = 0;
            _readCipher.Decrypt(
                Nonce(_readCounter++),
                sealedBlock.AsSpan(0, length),
                sealedBlock.AsSpan(length, TagLength),
                _plain,
                header);
        }

        var count = Math.Min(buffer.Length, _plain.Length - _plainOffset);
        _plain.AsMemory(_plainOffset, count).CopyTo(buffer);
        _plainOffset += count;
        return count;
    }

    // 4 zero bytes, then the 64-bit block counter, little-endian
    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writeCipher.Dispose();
            _readCipher.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
