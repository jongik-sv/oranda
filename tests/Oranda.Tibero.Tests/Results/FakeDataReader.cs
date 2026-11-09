using System.Collections;
using System.Data;
using System.Data.Common;

namespace Oranda.Tibero.Tests.Results;

/// <summary>
/// 시험용 <see cref="DbDataReader"/>: 메모리 행을 앞으로만 읽어 준다. 결과 집합·값 읽기 시험이 드라이버·서버 없이 돌게 한다.
/// <see cref="OnRead"/> 로 n 번째 읽기에서 지연·예외를 흉내 낸다.
/// </summary>
internal class FakeDataReader(string[] names, object?[][] rows, DataTable? schema = null) : DbDataReader
{
    private int _row = -1;

    /// <summary>Read 가 불린 횟수(1부터)를 받아 실행된다. 던지면 그 Read 가 던진다.</summary>
    public Action<int>? OnRead { get; set; }

    public int ReadCalls { get; private set; }

    public bool Disposed { get; private set; }

    public override int FieldCount => names.Length;

    public override bool HasRows => rows.Length > 0;

    public override bool IsClosed => Disposed;

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ReadCalls++;
        OnRead?.Invoke(ReadCalls);
        if (_row + 1 >= rows.Length)
        {
            _row = rows.Length;
            return false;
        }

        _row++;
        return true;
    }

    public override bool NextResult() => false;

    public override DataTable? GetSchemaTable() => schema;

    public override string GetName(int ordinal) => names[ordinal];

    public override int GetOrdinal(string name) => Array.IndexOf(names, name);

    public override object GetValue(int ordinal) => rows[_row][ordinal] ?? DBNull.Value;

    public override bool IsDBNull(int ordinal) => rows[_row][ordinal] is null or DBNull;

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++)
        {
            values[i] = GetValue(i);
        }

        return n;
    }

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override Type GetFieldType(int ordinal) => rows.Length > 0 && rows[0][ordinal] is { } v ? v.GetType() : typeof(string);

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
