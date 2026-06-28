using Banking.Infrastructure.Checks;
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
}
