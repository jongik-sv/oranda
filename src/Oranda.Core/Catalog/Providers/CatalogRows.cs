namespace Oranda.Core.Catalog.Providers;

/// <summary>카탈로그 질의 결과: 열 이름과 행(<c>GetValue</c> 값, NULL 은 null).</summary>
public sealed record CatalogRows(IReadOnlyList<string> Names, IReadOnlyList<object?[]> Rows);
