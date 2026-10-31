namespace FoldZip.Core;

/// <summary>CRC-32 (IEEE 802.3, polynomial 0xEDB88320)，ZIP 与 GZip 通用。</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public const uint Initial = 0xFFFFFFFFu;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Update(uint crc, byte[] buffer, int count)
        => Update(crc, buffer.AsSpan(0, count));

    public static uint Final(uint crc) => ~crc;

    public static uint Compute(ReadOnlySpan<byte> data)
        => Final(Update(Initial, data));

    public static uint Compute(Stream stream)
    {
        uint crc = Initial;
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            crc = Update(crc, buffer, read);
        return Final(crc);
    }
}
