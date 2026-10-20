namespace Oranda.Core.Transfer;

/// <summary>테이블 정의서 출력 형식(TSK-13-06).</summary>
public enum TableSpecFormat
{
    Xlsx,
    Markdown,
    Html,
}

/// <summary>정의서의 열 한 줄. <see cref="References"/> 는 외래키가 참조하는 <c>표.열</c>(없으면 null).</summary>
public sealed record TableSpecColumn(int No, string Name, string DataType, bool IsNullable, bool IsPrimaryKey, string? References, string? DefaultValue, string? Comment);

/// <summary>정의서의 인덱스 한 줄.</summary>
public sealed record TableSpecIndex(string Name, bool IsUnique, IReadOnlyList<string> Columns);

/// <summary>정의서의 표 하나(열 + 인덱스).</summary>
public sealed record TableSpecTable(string Owner, string Name, string? Comment, IReadOnlyList<TableSpecColumn> Columns, IReadOnlyList<TableSpecIndex> Indexes);

/// <summary>정의서 전체. 목차는 <see cref="Tables"/> 순서로 만든다.</summary>
public sealed record TableSpecDocument(string Title, DateTimeOffset GeneratedAt, IReadOnlyList<TableSpecTable> Tables);

/// <summary>정의서 문서 모델을 파일 내용으로 쓴다. 외부 라이브러리 없이 xlsx(목차 시트 + 표마다 시트)·Markdown·HTML 을 만든다. 구현: Oranda.Oracle(TSK-13-06).</summary>
public interface ITableSpecWriter
{
    /// <summary>형식의 파일 확장자(점 포함, 예 <c>.xlsx</c>).</summary>
    string Extension(TableSpecFormat format);

    Task WriteAsync(TableSpecDocument document, TableSpecFormat format, Stream destination, CancellationToken ct);
}
