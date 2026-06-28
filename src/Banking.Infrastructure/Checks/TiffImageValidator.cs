namespace Banking.Infrastructure.Checks;

public static class TiffImageValidator
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static void Validate(string fileName, string contentType, byte[] content)
    {
        if (content.Length == 0) throw new ArgumentException($"{fileName} is empty.");
        if (content.Length > MaxBytes) throw new ArgumentException($"{fileName} exceeds the 2 MB lab limit.");
        if (!fileName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{fileName} must be a .tif or .tiff file.");
        var looksLikeTiff = content.Length >= 4 &&
            ((content[0] == 0x49 && content[1] == 0x49 && content[2] == 0x2A && content[3] == 0x00)
             || (content[0] == 0x4D && content[1] == 0x4D && content[2] == 0x00 && content[3] == 0x2A));
        if (!looksLikeTiff) throw new ArgumentException($"{fileName} does not have a TIFF header.");
        if (!contentType.Equals("image/tiff", StringComparison.OrdinalIgnoreCase)
            && !contentType.Equals("image/tif", StringComparison.OrdinalIgnoreCase)
            && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{fileName} must be uploaded as a TIFF image.");
    }
}
