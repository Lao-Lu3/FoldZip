using System.Text;

namespace FoldZip.Core;

/// <summary>
/// 压缩包内文件名解码：优先按 UTF-8 标志；无标志时先按严格 UTF-8 尝试，
/// 失败则回退 GB18030（兼容 Windows 上大量旧中文压缩包），最后 Latin1。
/// </summary>
public static class NameEncoding
{
    static NameEncoding()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* 已注册或不可用时忽略 */ }
    }

    public static string Decode(byte[] raw, bool utf8Flag)
    {
        if (utf8Flag) return Encoding.UTF8.GetString(raw);
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding("GB18030").GetString(raw); }
            catch { return Encoding.Latin1.GetString(raw); }
        }
    }

    public static byte[] Encode(string name) => Encoding.UTF8.GetBytes(name);
}

/// <summary>ZIP 使用的时间：MS-DOS 日期/时间（2 秒精度，1980 起）。</summary>
public static class DosDateTime
{
    public static DateTime FromDos(ushort date, ushort time)
    {
        try
        {
            int year = 1980 + ((date >> 9) & 0x7F);
            int month = Math.Max(1, (date >> 5) & 0xF);
            int day = Math.Max(1, date & 0x1F);
            return new DateTime(year, month, day,
                (time >> 11) & 0x1F, (time >> 5) & 0x3F, (time & 0x1F) * 2,
                DateTimeKind.Local);
        }
        catch
        {
            return new DateTime(1980, 1, 1);
        }
    }

    public static (ushort Time, ushort Date) ToDos(DateTime dt)
    {
        if (dt.Year < 1980) dt = new DateTime(1980, 1, 1);
        ushort date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
        ushort time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
        return (time, date);
    }
}
