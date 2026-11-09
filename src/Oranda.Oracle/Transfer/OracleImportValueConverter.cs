using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary><see cref="IImportValueConverter"/> 의 Oracle 구현: 정적 <see cref="ImportValueConverter"/> 규칙을 그대로 쓴다(App 은 세션 DBMS 로 이것을 골라 미리보기에 쓴다).</summary>
[OrandaService(typeof(IImportValueConverter), Dbms = Dbms.Oracle)]
internal sealed class OracleImportValueConverter : IImportValueConverter
{
    public ColumnDataKind KindOf(string dataType) => ImportValueConverter.KindOf(dataType);

    public string ToLiteral(ColumnInfo column, object? cell) => ImportValueConverter.ToLiteral(column, cell);

    public bool TryConvert(ColumnInfo column, object? cell, out string literal, out string? reason) =>
        ImportValueConverter.TryConvert(column, cell, out literal, out reason);
}
