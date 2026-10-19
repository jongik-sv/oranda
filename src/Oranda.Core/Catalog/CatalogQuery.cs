namespace Oranda.Core.Catalog;

/// <summary>카탈로그 파일 하나 = 쿼리 하나. 머리 주석 메타데이터와 실행할 SQL 본문.</summary>
/// <param name="Header">머리 주석에서 읽은 메타데이터</param>
/// <param name="Sql">머리 주석 블록을 뺀 본문(앞뒤 공백 제거)</param>
public sealed record CatalogQuery(CatalogHeader Header, string Sql);

/// <summary>
/// 카탈로그 파일 하나의 원문. <see cref="Path"/> 는 Catalog/ 아래 상대 경로이고 구분자는 '/' 다
/// (예: <c>session/list.sql</c>).
/// </summary>
public sealed record CatalogSource(string Path, string Text);
