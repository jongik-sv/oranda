using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer;

/// <summary>
/// 내보내기·가져오기(EXP). 내보내기는 그리드가 아니라 서버에서 새 리더로 다시 스트리밍한다(TRD 4.4).
/// 가져오기의 INSERT 는 실행 엔진(정책 관문)을 거친다. 구현: Oranda.Oracle(TSK-06-07).
/// </summary>
public interface IDataTransferService
{
    Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                      ExportOptions options, Stream destination, IProgress<long>? progress,
                                      CancellationToken ct);

    Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress,
                                      CancellationToken ct);
}

public enum TransferFormat
{
    Csv,
    Tsv,
    Xlsx,
    Json,
    Insert,
}

public sealed record ExportOptions(TransferFormat Format)
{
    public bool IncludeHeader { get; init; } = true;

    /// <summary>INSERT 형식의 대상 테이블 이름.</summary>
    public string? InsertTableName { get; init; }

    /// <summary>xlsx 한 시트 최대 행(넘으면 시트 분할).</summary>
    public int MaxRowsPerSheet { get; init; } = 1_048_575;
}

/// <summary>원본 열 → 대상 열 매핑. 대상이 null 이면 그 열은 건너뛴다.</summary>
public sealed record ColumnMapping(string SourceColumn, string? TargetColumn);

public sealed record ImportOptions(TransferFormat Format, DbObjectRef Target, IReadOnlyList<ColumnMapping> Mappings)
{
    public bool HasHeader { get; init; } = true;

    public int BatchSize { get; init; } = 500;

    public bool CommitAtEnd { get; init; } = true;
}

public sealed record TransferSummary(long Rows, TimeSpan Elapsed)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
