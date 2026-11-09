using System;
using System.Globalization;
using System.IO;

namespace Tibero.DataAccess.Types
{
    public interface INullable { bool IsNull { get; } }

    public struct TiberoString : INullable
    {
        private readonly string? _v; private readonly bool _ok;
        public TiberoString(string value) { _v = value; _ok = true; }
        public static readonly TiberoString Null = default;
        public bool IsNull => !_ok;
        public string Value => _ok ? _v! : throw new InvalidOperationException("null");
        public override string ToString() => _ok ? _v! : "null";
    }

    public struct TiberoDecimal : INullable
    {
        private readonly decimal _v; private readonly bool _ok;
        public TiberoDecimal(decimal value) { _v = value; _ok = true; }
        public static readonly TiberoDecimal Null = default;
        public bool IsNull => !_ok;
        public decimal Value => _ok ? _v : throw new InvalidOperationException("null");
        public override string ToString() => _ok ? _v.ToString(CultureInfo.InvariantCulture) : "null";
    }

    public struct TiberoDate : INullable
    {
        private readonly DateTime _v; private readonly bool _ok;
        public TiberoDate(DateTime value) { _v = value; _ok = true; }
        public static readonly TiberoDate Null = default;
        public bool IsNull => !_ok;
        public DateTime Value => _ok ? _v : throw new InvalidOperationException("null");
    }

    public struct TiberoTimeStamp : INullable
    {
        private readonly DateTime _v; private readonly int _ns; private readonly bool _ok;
        public TiberoTimeStamp(DateTime value) : this(value, 0) { }
        public TiberoTimeStamp(DateTime value, int nanosecond) { _v = value; _ns = nanosecond; _ok = true; }
        public TiberoTimeStamp(int year, int month, int day, int hour, int minute, int second, int nanosecond)
            : this(new DateTime(year, month, day, hour, minute, second), nanosecond) { }
        // 실제 드라이버에도 있는 밀리초 겹치기 — 어댑터가 나노초 생성자를 정확히 골라야 한다(넓히기로 이것을 고르면 안 됨).
        public TiberoTimeStamp(int year, int month, int day, int hour, int minute, int second, double millisecond)
            : this(new DateTime(year, month, day, hour, minute, second), (int)(millisecond * 1_000_000)) { }
        public static readonly TiberoTimeStamp Null = default;
        public bool IsNull => !_ok;
        public DateTime Value => _ok ? _v : throw new InvalidOperationException("null");
        public int Nanosecond => _ok ? _ns : throw new InvalidOperationException("null");
    }

    public struct TiberoTimeStampLTZ : INullable
    {
        private readonly DateTime _v; private readonly int _ns; private readonly bool _ok;
        public TiberoTimeStampLTZ(DateTime value, int nanosecond) { _v = value; _ns = nanosecond; _ok = true; }
        public TiberoTimeStampLTZ(DateTime value) : this(value, 0) { }
        public static readonly TiberoTimeStampLTZ Null = default;
        public bool IsNull => !_ok;
        public DateTime Value => _ok ? _v : throw new InvalidOperationException("null");
        public int Nanosecond => _ok ? _ns : throw new InvalidOperationException("null");
    }

    public struct TiberoTimeStampTZ : INullable
    {
        private readonly DateTime _v; private readonly string? _tz; private readonly bool _ok;
        public TiberoTimeStampTZ(DateTime data, string timeZone) { _v = data; _tz = timeZone; _ok = true; }
        public static readonly TiberoTimeStampTZ Null = default;
        public bool IsNull => !_ok;
        public DateTime Value => _ok ? _v : throw new InvalidOperationException("null");
        public string TimeZone => _ok ? _tz! : throw new InvalidOperationException("null");
    }

    public struct TiberoBinary : INullable
    {
        private readonly byte[]? _v;
        public TiberoBinary(byte[] value) { _v = value; }
        public static readonly TiberoBinary Null = default;
        public bool IsNull => _v == null;
        public byte[] Value => _v ?? throw new InvalidOperationException("null");
    }

    public class TiberoClob : Stream, INullable
    {
        private readonly string? _text; private long _pos;
        public TiberoClob(string? text, bool isNClob = false) { _text = text; IsNClob = isNClob; }
        public static new readonly TiberoClob Null = new TiberoClob(null);
        public bool IsNClob { get; }
        public bool IsNull => _text == null;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => (_text?.Length ?? 0) * 2L;
        public override long Position { get => _pos; set => _pos = value; }
        public int Read(char[] buffer, int offset, int count)
        {
            string t = _text ?? string.Empty;
            int start = (int)(_pos / 2);
            int n = Math.Max(0, Math.Min(count, t.Length - start));
            t.CopyTo(start, buffer, offset, n);
            _pos += 2L * n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => -1;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : Length + offset;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public object Clone() => new TiberoClob(_text, IsNClob);
    }

    public class TiberoBlob : Stream, INullable
    {
        private readonly byte[]? _data; private long _pos;
        public TiberoBlob(byte[]? data) { _data = data; }
        public static new readonly TiberoBlob Null = new TiberoBlob(null);
        public bool IsNull => _data == null;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _data?.Length ?? 0;
        public override long Position { get => _pos; set => _pos = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            byte[] d = _data ?? Array.Empty<byte>();
            int n = (int)Math.Max(0, Math.Min(count, d.Length - _pos));
            Array.Copy(d, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : Length + offset;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public object Clone() => new TiberoBlob(_data);
    }

    public class TiberoRefCursor : INullable
    {
        private readonly Tibero.DataAccess.Client.TiberoDataReader? _r;
        public TiberoRefCursor(System.Data.DataTable? table) { if (table != null) _r = new Tibero.DataAccess.Client.TiberoDataReader(table); }
        public bool IsNull => _r == null;
        public Tibero.DataAccess.Client.TiberoDataReader GetDataReader() => _r ?? throw new InvalidOperationException("null");
    }
}

namespace tbcomm.err
{
    public class TbOdpException : Exception
    {
        public TbOdpException(int errorCode, string message) : base(message) { ErrorCode = errorCode; }
        public int ErrorCode { get; }
    }
}
