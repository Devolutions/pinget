using System.Buffers.Binary;
using System.IO.Compression;

namespace Devolutions.Pinget.Core;

internal static class NativeMsZip
{
    internal static byte[] DecompressManaged(byte[] input)
    {
        if (input.Length < 24)
            throw new InvalidDataException("Truncated MSZIP framing header.");
        var totalSize = BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(8, 8));
        var blockSize = BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(16, 8));
        if (totalSize > 64 * 1024 * 1024 || blockSize is 0 or > 32768)
            throw new InvalidDataException("Invalid MSZIP size or block size (maximum output is 64 MiB).");
        var output = new byte[checked((int)totalSize)];
        var position = 24;
        var written = 0;
        while (position < input.Length)
        {
            if (input.Length - position < 4)
                throw new InvalidDataException("Truncated MSZIP block length.");
            var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(position, 4));
            position += 4;
            if (compressedLength < 3 || compressedLength > input.Length - position
                || input[position] != (byte)'C' || input[position + 1] != (byte)'K')
                throw new InvalidDataException("Invalid or truncated MSZIP block.");

            var historyLength = Math.Min(written, 32768);
            // A nonfinal stored block supplies the dictionary to the raw inflater.
            var deflateBytes = new byte[checked(5 + historyLength + (int)compressedLength - 2)];
            BinaryPrimitives.WriteUInt16LittleEndian(deflateBytes.AsSpan(1), (ushort)historyLength);
            BinaryPrimitives.WriteUInt16LittleEndian(deflateBytes.AsSpan(3), (ushort)~historyLength);
            output.AsSpan(written - historyLength, historyLength).CopyTo(deflateBytes.AsSpan(5));
            input.AsSpan(position + 2, (int)compressedLength - 2).CopyTo(deflateBytes.AsSpan(5 + historyLength));
            using var compressed = new CompleteDeflateInput(deflateBytes);
            using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
            var block = new byte[historyLength + (int)blockSize];
            var read = 0;
            while (read < block.Length)
            {
                var count = inflater.Read(block.AsSpan(read));
                if (count == 0)
                    break;
                read += count;
            }
            if (inflater.ReadByte() != -1 || read <= historyLength || read - historyLength > output.Length - written)
                throw new InvalidDataException("MSZIP block exceeds its declared output size.");
            var produced = read - historyLength;
            block.AsSpan(historyLength, produced).CopyTo(output.AsSpan(written));
            written += produced;
            position += (int)compressedLength;
        }
        if (written != output.Length)
            throw new InvalidDataException("MSZIP output does not match its declared size.");
        return output;
    }

    private sealed class CompleteDeflateInput(byte[] bytes) : Stream
    {
        private readonly MemoryStream input = new(bytes, false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position
        {
            get => input.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Position == Length && buffer.Length > 0)
                throw new InvalidDataException("Truncated MSZIP deflate stream.");
            return input.Read(buffer);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                input.Dispose();
            base.Dispose(disposing);
        }
    }
}
