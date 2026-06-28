using System.Globalization;
using Banking.Domain;

namespace Banking.Infrastructure.Checks;

/// <summary>Creates synthetic baseline grayscale TIFFs for exercising the check lab.</summary>
public static class CheckTiffGenerator
{
    private const int Width = 900;
    private const int Height = 400;

    public static byte[] Generate(CheckImageSide side, string routingNumber,
        string accountNumber, string checkNumber, decimal amount)
    {
        var pixels = Enumerable.Repeat((byte)255, Width * Height).ToArray();
        DrawRectangle(pixels, 8, 8, Width - 9, Height - 9, 0, 3);
        if (side == CheckImageSide.Front)
            DrawFront(pixels, routingNumber, accountNumber, checkNumber, amount);
        else
            DrawBack(pixels, routingNumber, accountNumber, checkNumber);

        var description = $"Banking Lab synthetic {side} check image; routing={routingNumber}; "
            + $"account={accountNumber}; check={checkNumber}; amount={amount.ToString("0.00", CultureInfo.InvariantCulture)}\0";
        return Encode(pixels, description);
    }

    private static void DrawFront(byte[] pixels, string routing, string account,
        string checkNumber, decimal amount)
    {
        DrawLine(pixels, 45, 75, 470, 75, 110, 2);
        DrawLine(pixels, 45, 120, 620, 120, 160, 2);
        DrawLine(pixels, 45, 165, 760, 165, 160, 2);
        DrawRectangle(pixels, 690, 55, 850, 120, 60, 2);
        DrawRectangle(pixels, 690, 205, 850, 285, 120, 2);
        DrawLine(pixels, 540, 305, 835, 305, 80, 2);
        for (var x = 45; x < 850; x += 16) DrawLine(pixels, x, 350, x + 6, 350, 0, 4);
        DrawDataBars(pixels, routing + account + checkNumber
            + decimal.ToInt64(amount * 100m).ToString(CultureInfo.InvariantCulture), 55, 328);
    }

    private static void DrawBack(byte[] pixels, string routing, string account, string checkNumber)
    {
        DrawRectangle(pixels, 70, 55, 520, 175, 100, 2);
        DrawLine(pixels, 95, 90, 475, 90, 150, 2);
        DrawLine(pixels, 95, 125, 430, 125, 150, 2);
        DrawRectangle(pixels, 610, 60, 825, 190, 140, 2);
        DrawLine(pixels, 70, 245, 825, 245, 170, 2);
        DrawLine(pixels, 70, 285, 825, 285, 170, 2);
        DrawDataBars(pixels, routing + account + checkNumber, 80, 330);
    }

    private static void DrawDataBars(byte[] pixels, string value, int left, int top)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        for (var i = 0; i < hash.Length; i++)
        {
            var height = 8 + hash[i] % 22;
            DrawLine(pixels, left + i * 12, top - height, left + i * 12, top, 0,
                hash[i] % 3 == 0 ? 3 : 2);
        }
    }

    private static void DrawRectangle(byte[] pixels, int left, int top, int right,
        int bottom, byte shade, int thickness)
    {
        DrawLine(pixels, left, top, right, top, shade, thickness);
        DrawLine(pixels, left, bottom, right, bottom, shade, thickness);
        DrawLine(pixels, left, top, left, bottom, shade, thickness);
        DrawLine(pixels, right, top, right, bottom, shade, thickness);
    }

    private static void DrawLine(byte[] pixels, int x1, int y1, int x2, int y2,
        byte shade, int thickness)
    {
        if (y1 == y2)
            for (var y = y1; y < y1 + thickness; y++)
                for (var x = x1; x <= x2; x++) Set(pixels, x, y, shade);
        else
            for (var x = x1; x < x1 + thickness; x++)
                for (var y = y1; y <= y2; y++) Set(pixels, x, y, shade);
    }

    private static void Set(byte[] pixels, int x, int y, byte shade)
    {
        if (x >= 0 && x < Width && y >= 0 && y < Height) pixels[y * Width + x] = shade;
    }

    private static byte[] Encode(byte[] pixels, string description)
    {
        var descriptionBytes = System.Text.Encoding.ASCII.GetBytes(description);
        const ushort entryCount = 13;
        var variableOffset = 8 + 2 + entryCount * 12 + 4;
        var descriptionOffset = variableOffset;
        variableOffset += descriptionBytes.Length;
        if ((variableOffset & 1) != 0) variableOffset++;
        var xResolutionOffset = variableOffset;
        var yResolutionOffset = xResolutionOffset + 8;
        var pixelOffset = yResolutionOffset + 8;

        using var stream = new MemoryStream(pixelOffset + pixels.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write(8u);
        writer.Write(entryCount);
        Entry(writer, 256, 4, 1, Width);
        Entry(writer, 257, 4, 1, Height);
        Entry(writer, 258, 3, 1, 8);
        Entry(writer, 259, 3, 1, 1);
        Entry(writer, 262, 3, 1, 1);
        Entry(writer, 270, 2, descriptionBytes.Length, descriptionOffset);
        Entry(writer, 273, 4, 1, pixelOffset);
        Entry(writer, 277, 3, 1, 1);
        Entry(writer, 278, 4, 1, Height);
        Entry(writer, 279, 4, 1, pixels.Length);
        Entry(writer, 282, 5, 1, xResolutionOffset);
        Entry(writer, 283, 5, 1, yResolutionOffset);
        Entry(writer, 296, 3, 1, 2);
        writer.Write(0u);
        writer.Write(descriptionBytes);
        if ((stream.Position & 1) != 0) writer.Write((byte)0);
        writer.Write(200u); writer.Write(1u);
        writer.Write(200u); writer.Write(1u);
        writer.Write(pixels);
        return stream.ToArray();
    }

    private static void Entry(BinaryWriter writer, ushort tag, ushort type, int count, int value)
    {
        writer.Write(tag); writer.Write(type); writer.Write((uint)count);
        if (type == 3 && count == 1)
        {
            writer.Write((ushort)value); writer.Write((ushort)0);
        }
        else writer.Write((uint)value);
    }
}
