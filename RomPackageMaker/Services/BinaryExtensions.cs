using System.Buffers.Binary;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// 针对 Android 镜像（小端序）的二进制读取扩展方法。
/// </summary>
internal static class BinaryExtensions
{
    public static ushort ReadUInt16LE(this BinaryReader reader)
    {
        Span<byte> buf = stackalloc byte[2];
        if (reader.Read(buf) != 2) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

    public static short ReadInt16LE(this BinaryReader reader)
    {
        Span<byte> buf = stackalloc byte[2];
        if (reader.Read(buf) != 2) throw new EndOfStreamException();
        return BinaryPrimitives.ReadInt16LittleEndian(buf);
    }

    public static uint ReadUInt32LE(this BinaryReader reader)
    {
        Span<byte> buf = stackalloc byte[4];
        if (reader.Read(buf) != 4) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    public static int ReadInt32LE(this BinaryReader reader)
    {
        Span<byte> buf = stackalloc byte[4];
        if (reader.Read(buf) != 4) throw new EndOfStreamException();
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    public static ulong ReadUInt64LE(this BinaryReader reader)
    {
        Span<byte> buf = stackalloc byte[8];
        if (reader.Read(buf) != 8) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    public static long ReadInt64LE(this BinaryReader reader)
    {
        Span<byte> buf = stackalloc byte[8];
        if (reader.Read(buf) != 8) throw new EndOfStreamException();
        return BinaryPrimitives.ReadInt64LittleEndian(buf);
    }

    public static void WriteUInt16LE(this BinaryWriter writer, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        writer.Write(buf);
    }

    public static void WriteUInt32LE(this BinaryWriter writer, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        writer.Write(buf);
    }

    public static void WriteUInt64LE(this BinaryWriter writer, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        writer.Write(buf);
    }

    public static string ReadCString(this BinaryReader reader, int maxLength)
    {
        var bytes = reader.ReadBytes(maxLength);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    public static byte[] ReadExact(this Stream stream, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buffer;
    }

    public static void CopyExact(this Stream source, Stream destination, long count, int bufferSize = 1 << 20)
    {
        var buffer = new byte[bufferSize];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n = source.Read(buffer, 0, toRead);
            if (n <= 0) throw new EndOfStreamException();
            destination.Write(buffer, 0, n);
            remaining -= n;
        }
    }
}
