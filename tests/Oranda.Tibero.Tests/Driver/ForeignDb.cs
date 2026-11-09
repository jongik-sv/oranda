using System.Data;
using System.Data.Common;

namespace Oranda.Tibero.Tests.Driver;

/// <summary>드라이버 형식이 아닌 명령(다른 공급자 흉내). 인자 검사 시험용.</summary>
internal sealed class ForeignCommand : DbCommand
{
#pragma warning disable CS8765
    public override string CommandText { get; set; } = string.Empty;
#pragma warning restore CS8765
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() => 0;
    public override object? ExecuteScalar() => null;
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new ForeignParameter();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
}

/// <summary>드라이버 형식이 아닌 매개변수.</summary>
internal sealed class ForeignParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
#pragma warning disable CS8765
    public override string ParameterName { get; set; } = string.Empty;
    public override string SourceColumn { get; set; } = string.Empty;
#pragma warning restore CS8765
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override void ResetDbType() { }
}
