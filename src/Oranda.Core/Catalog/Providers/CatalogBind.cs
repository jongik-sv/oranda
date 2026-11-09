using System.Diagnostics.CodeAnalysis;

namespace Oranda.Core.Catalog.Providers;

/// <summary>카탈로그 질의 바인드 한 개. DBMS 공급자가 자기 매개변수 형으로 바꾼다.</summary>
public sealed record CatalogBind(string Name, object? Value, CatalogBindKind Kind);

/// <summary>바인드 값의 형(공급자가 DBMS 형으로 대응).</summary>
[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "바인드 형 이름(Int32·Int64·Double)은 P0 설계 계약이다.")]
public enum CatalogBindKind
{
    Text,
    Int32,
    Int64,
    Double,
    Date,
}
