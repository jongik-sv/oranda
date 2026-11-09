using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using Tibero.DataAccess.Types;

namespace Tibero.DataAccess.Client
{
    public enum TiberoDbType
    {
        BFile = 101, Blob = 102, Byte = 103, Char = 104, Clob = 105, Date = 106, Decimal = 107, Double = 108,
        Long = 109, LongRaw = 110, Int16 = 111, Int32 = 112, Int64 = 113, IntervalDS = 114, IntervalYM = 115,
        NClob = 116, NChar = 117, NVarchar2 = 119, Raw = 120, RefCursor = 121, Single = 122, TimeStamp = 123,
        TimeStampLTZ = 124, TimeStampTZ = 125, Varchar2 = 126, XmlType = 127, Array = 128, Object = 129,
        Ref = 130, BinaryDouble = 132, BinaryFloat = 133, Boolean = 134,
    }

    public enum TiberoCollectionType { None = 0, PLSQLAssociativeArray = 1 }

    public sealed class TiberoException : DbException
    {
        internal TiberoException(int number, string message) : base(message) { Number = number; }
        public int Number { get; }
    }

    public static class FakeServer
    {
        // 시험마다(비동기 흐름마다) 따로 — 다른 레인의 시험이 병렬로 돌아도 서로의 처리기를 쓰지 않는다.
        private static readonly AsyncLocal<Func<DbCommand, object?>?> s_handler = new();
        public static Func<DbCommand, object?>? Handler
        {
            get => s_handler.Value;
            set => s_handler.Value = value;
        }
        public static Exception ServerError(int number, string message) => new TiberoException(number, message);
        public static Exception ClientError(int code, string message) => new tbcomm.err.TbOdpException(code, message);
        public static void Reset() { Handler = null; }
        internal static object? Run(DbCommand c) => Handler?.Invoke(c);
    }

    public sealed class TiberoClientFactory : DbProviderFactory
    {
        public static readonly TiberoClientFactory Instance = new TiberoClientFactory();
        private TiberoClientFactory() { }
        public override DbConnection CreateConnection() => new TiberoConnection();
        public override DbCommand CreateCommand() => new TiberoCommand();
        public override DbParameter CreateParameter() => new TiberoParameter();
        public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new TiberoConnectionStringBuilder();
    }

    public sealed class TiberoConnectionStringBuilder : DbConnectionStringBuilder { }

    public sealed class TiberoTransaction : DbTransaction
    {
        private readonly TiberoConnection _c;
        internal TiberoTransaction(TiberoConnection c, IsolationLevel l) { _c = c; IsolationLevel = l; }
        public override IsolationLevel IsolationLevel { get; }
        protected override DbConnection? DbConnection => _c;
        public override void Commit() { }
        public override void Rollback() { }
    }

    public sealed class TiberoConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;
        private string _cs = string.Empty;
        public TiberoConnection() { }
        public TiberoConnection(string connectionString) { _cs = connectionString; }
#pragma warning disable CS8765
        public override string ConnectionString { get => _cs; set => _cs = value ?? string.Empty; }
#pragma warning restore CS8765
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => "6";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Open() { _state = ConnectionState.Open; }
        public override void Close() { _state = ConnectionState.Closed; }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new TiberoTransaction(this, isolationLevel);
        protected override DbCommand CreateDbCommand() => new TiberoCommand { Connection = this };
    }

    public sealed class TiberoParameter : DbParameter
    {
        private string _name = string.Empty;
        private string _src = string.Empty;
        public TiberoDbType TiberoDbType { get; set; }
        public TiberoCollectionType CollectionType { get; set; }
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
#pragma warning disable CS8765
        public override string ParameterName { get => _name; set => _name = value ?? string.Empty; }
        public override string SourceColumn { get => _src; set => _src = value ?? string.Empty; }
#pragma warning restore CS8765
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    public sealed class TiberoParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _l = new List<DbParameter>();
        public override int Count => _l.Count;
        public override object SyncRoot => ((ICollection)_l).SyncRoot;
        public override int Add(object value) { _l.Add((DbParameter)value); return _l.Count - 1; }
        public override void AddRange(Array values) { foreach (object o in values) Add(o); }
        public override void Clear() => _l.Clear();
        public override bool Contains(object value) => _l.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_l).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _l.GetEnumerator();
        public override int IndexOf(object value) => _l.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _l.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _l.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _l.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _l.RemoveAt(index);
        public override void RemoveAt(string parameterName) { int i = IndexOf(parameterName); if (i >= 0) _l.RemoveAt(i); }
        protected override DbParameter GetParameter(int index) => _l[index];
        protected override DbParameter GetParameter(string parameterName) => _l[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _l[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => _l[IndexOf(parameterName)] = value;
    }

    public sealed class TiberoCommand : DbCommand
    {
        private readonly TiberoParameterCollection _ps = new TiberoParameterCollection();
        private string _text = string.Empty;
        public bool BindByName { get; set; }
        public int InitialLOBFetchSize { get; set; }
        public int ArrayBindCount { get; set; }
#pragma warning disable CS8765
        public override string CommandText { get => _text; set => _text = value ?? string.Empty; }
#pragma warning restore CS8765
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _ps;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new TiberoParameter();
        public override int ExecuteNonQuery() => FakeServer.Run(this) is int i ? i : -1;
        public override object? ExecuteScalar() => FakeServer.Run(this);
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            new TiberoDataReader(FakeServer.Run(this) as DataTable ?? new DataTable());
    }

    public sealed class TiberoDataReader : DbDataReader
    {
        private readonly DataTable _t;
        private int _row = -1;
        private bool _closed;
        public TiberoDataReader(DataTable table) { _t = table; }
        private object Cell(int i) => _t.Rows[_row][i];
        public override int FieldCount => _t.Columns.Count;
        public override int Depth => 0;
        public override bool HasRows => _t.Rows.Count > 0;
        public override bool IsClosed => _closed;
        public override int RecordsAffected => -1;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override bool Read() => ++_row < _t.Rows.Count;
        public override bool NextResult() => false;
        public override void Close() { _closed = true; }
        public override string GetName(int ordinal) => _t.Columns[ordinal].ColumnName;
        public override int GetOrdinal(string name) => _t.Columns[name]?.Ordinal ?? throw new IndexOutOfRangeException(name);
        public override string GetDataTypeName(int ordinal) => _t.Columns[ordinal].DataType.Name;
        public override Type GetFieldType(int ordinal) => _t.Columns[ordinal].DataType;
        public override object GetValue(int ordinal) => Cell(ordinal);
        public override int GetValues(object[] values)
        {
            int n = Math.Min(values.Length, FieldCount);
            for (int i = 0; i < n; i++) values[i] = Cell(i);
            return n;
        }
        public override bool IsDBNull(int ordinal) => Cell(ordinal) is DBNull;
        public override bool GetBoolean(int ordinal) => (bool)Cell(ordinal);
        public override byte GetByte(int ordinal) => (byte)Cell(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => (char)Cell(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => (DateTime)Cell(ordinal);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(Cell(ordinal));
        public override double GetDouble(int ordinal) => Convert.ToDouble(Cell(ordinal));
        public override float GetFloat(int ordinal) => Convert.ToSingle(Cell(ordinal));
        public override Guid GetGuid(int ordinal) => (Guid)Cell(ordinal);
        public override short GetInt16(int ordinal) => Convert.ToInt16(Cell(ordinal));
        public override int GetInt32(int ordinal) => Convert.ToInt32(Cell(ordinal));
        public override long GetInt64(int ordinal) => Convert.ToInt64(Cell(ordinal));
        public override string GetString(int ordinal) => (string)Cell(ordinal);
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();

        public TiberoBinary GetTiberoBinary(int i) => Cell(i) is byte[] b ? new TiberoBinary(b) : TiberoBinary.Null;
        public TiberoClob GetTiberoClob(int i) => Cell(i) is string s ? new TiberoClob(s) : Cell(i) is TiberoClob c ? c : TiberoClob.Null;
        public TiberoBlob GetTiberoBlob(int i) => Cell(i) is byte[] b ? new TiberoBlob(b) : Cell(i) is TiberoBlob l ? l : TiberoBlob.Null;
        public TiberoTimeStampLTZ GetTiberoTimeStampLTZ(int i) =>
            Cell(i) is DateTime d ? new TiberoTimeStampLTZ(d, 0) : Cell(i) is TiberoTimeStampLTZ t ? t : TiberoTimeStampLTZ.Null;
    }
}
