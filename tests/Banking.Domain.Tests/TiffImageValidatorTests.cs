using Banking.Infrastructure.Checks;
using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class TiffImageValidatorTests
{
    [Fact]
    public void AcceptsLittleEndianTiffHeader() => TiffImageValidator.Validate(
        "front.tiff", "image/tiff", [0x49, 0x49, 0x2A, 0x00, 0x00]);

    [Fact]
    public void AcceptsBigEndianTiffHeader() => TiffImageValidator.Validate(
        "back.tif", "application/octet-stream", [0x4D, 0x4D, 0x00, 0x2A, 0x00]);

    [Fact]
    public void RejectsNonTiffHeader() => Assert.Throws<ArgumentException>(() =>
        TiffImageValidator.Validate("front.tiff", "image/tiff", [0xFF, 0xD8, 0xFF, 0xE0]));

    [Fact]
    public void GeneratedCheckImagesAreCompleteBaselineTiffs()
    {
        var front = CheckTiffGenerator.Generate(CheckImageSide.Front,
            "103000648", "654321", "1001", 125.50m);
        var back = CheckTiffGenerator.Generate(CheckImageSide.Back,
            "103000648", "654321", "1001", 125.50m);

        TiffImageValidator.Validate("front.tiff", "image/tiff", front);
        TiffImageValidator.Validate("back.tiff", "image/tiff", back);
        Assert.NotEqual(front, back);
        Assert.True(front.Length < TiffImageValidator.MaxBytes);

        using var reader = new BinaryReader(new MemoryStream(front));
        reader.BaseStream.Position = 4;
        reader.BaseStream.Position = reader.ReadUInt32();
        Assert.Equal(13, reader.ReadUInt16());
        var tags = new Dictionary<ushort, uint>();
        for (var i = 0; i < 13; i++)
        {
            var tag = reader.ReadUInt16();
            var type = reader.ReadUInt16();
            _ = reader.ReadUInt32();
            tags[tag] = type == 3 ? reader.ReadUInt16() : reader.ReadUInt32();
            if (type == 3) _ = reader.ReadUInt16();
        }
        Assert.Equal(900u, tags[256]);
        Assert.Equal(400u, tags[257]);
        Assert.Equal((uint)front.Length, tags[273] + tags[279]);
    }
}
