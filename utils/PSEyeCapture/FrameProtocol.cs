// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace PSEyeCapture;

internal readonly record struct FrameHeader(int Width, int Height, int Stride, int Length, long Timestamp)
{
    public static FrameHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != 32 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x31455950 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1) throw new InvalidDataException("Unsupported PS Eye frame header");
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[8..]), height = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        int stride = BinaryPrimitives.ReadInt32LittleEndian(data[16..]), length = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        long timestamp = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
        if (width != 640 || height != 480 || stride != width * 3 || length != stride * height || timestamp < 0)
            throw new InvalidDataException("Invalid PS Eye frame dimensions or timestamp");
        return new(width, height, stride, length, timestamp);
    }
}
