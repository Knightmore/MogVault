using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MogVault.Store;

public static partial class VaultFile
{
    private ref struct SpanWriter(Span<byte> dest)
    {
        private Span<byte> rest = dest;

        public readonly int Remaining => rest.Length;

        public void Write<T>(in T value) where T : unmanaged
        {
            MemoryMarshal.Write(rest, in value);
            rest = rest[Unsafe.SizeOf<T>()..];
        }

        public void WriteSectionHeader(ushort tag, int length)
        {
            Write(tag);
            Write((uint)length);
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(rest);
            rest = rest[bytes.Length..];
        }

        public void WriteUtf8(string s) => rest = rest[Encoding.UTF8.GetBytes(s, rest)..];
    }

    private ref struct SpanReader(ReadOnlySpan<byte> src)
    {
        private ReadOnlySpan<byte> rest = src;

        public readonly int Remaining => rest.Length;

        public T Read<T>() where T : unmanaged
        {
            var size = Unsafe.SizeOf<T>();
            if (rest.Length < size)
                throw new InvalidDataException("Unexpected end of file.");
            var value = MemoryMarshal.Read<T>(rest);
            rest = rest[size..];
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (rest.Length < count)
                throw new InvalidDataException("Unexpected end of file.");
            var bytes = rest[..count];
            rest = rest[count..];
            return bytes;
        }

        public string ReadUtf8(int count) => Encoding.UTF8.GetString(ReadBytes(count));
    }
}
