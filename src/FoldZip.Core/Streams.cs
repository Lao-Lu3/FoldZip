namespace FoldZip.Core;

/// <summary>基础流的一个只读切片（限定 [start, start+length)），不支持 Seek 语义之外的操作。</summary>
public sealed class PartialStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public PartialStream(Stream baseStream, long start, long length)
    {
        if (!baseStream.CanRead) throw new ArgumentException("底层流必须可读", nameof(baseStream));
        if (start < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(start));
        _base = baseStream;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remain = _length - _position;
        if (remain <= 0) return 0;
        if (count > remain) count = (int)remain;
        if (count <= 0) return 0;
        if (_base.Position != _start + _position)
            _base.Seek(_start + _position, SeekOrigin.Begin);
        int read = _base.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}

/// <summary>读取侧包装：边读边计算 CRC-32 与累计字节数。</summary>
public sealed class CrcReadStream : Stream
{
    private readonly Stream _inner;
    private uint _crc = Crc32.Initial;
    private long _bytesRead;

    public CrcReadStream(Stream inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public uint FinalCrc => Crc32.Final(_crc);
    public long BytesRead => _bytesRead;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _crc = Crc32.Update(_crc, buffer, read);
            _bytesRead += read;
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}

/// <summary>写入侧包装：边写边计算 CRC-32 与累计字节数。</summary>
public sealed class CrcWriteStream : Stream
{
    private readonly Stream _inner;
    private uint _crc = Crc32.Initial;
    private long _bytesWritten;

    public CrcWriteStream(Stream inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public uint FinalCrc => Crc32.Final(_crc);
    public long BytesWritten => _bytesWritten;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count > 0)
        {
            _inner.Write(buffer, offset, count);
            _crc = Crc32.Update(_crc, buffer, count);
            _bytesWritten += count;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => _inner.Flush();
}
