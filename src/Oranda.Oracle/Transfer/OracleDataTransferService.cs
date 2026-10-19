using System.Diagnostics;
using System.Globalization;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>
/// 내보내기(서버 재스트리밍)·가져오기. 소유: TSK-06-07.
/// <para>
/// 내보내기: 질의를 실행 엔진으로 다시 실행해 페이지 단위로 읽으며 바로 파일에 쓴다(전체를 메모리에 올리지 않는다).
/// 가져오기: 행을 열 타입에 맞는 SQL 리터럴로 바꿔 배치 단위 <c>INSERT … SELECT … FROM DUAL UNION ALL …</c> 한 문장으로 보낸다.
/// 모든 문장은 <see cref="IExecutionService"/>(정책 관문)를 거치므로 읽기 전용 프로필·운영 확인이 그대로 적용된다.
/// 배치가 서버 오류로 실패하면 그 배치를 한 행씩 다시 보내 어느 행이 틀렸는지 찾는다. 틀린 행은 <see cref="TransferSummary.Warnings"/> 에
/// <c>「행 N: 이유」</c> 로 따로 보고하고 나머지는 계속 넣는다.
/// </para>
/// </summary>
[OrandaService(typeof(IDataTransferService))]
internal sealed class OracleDataTransferService : IDataTransferService
{
    /// <summary>가져오기 오류 행 경고 접두어. 뒤에 <c>행 번호: 이유</c> 가 온다.</summary>
    internal const string ErrorRowPrefix = "행 ";

    private const int MaxReportedErrors = 1000;

    private readonly IExecutionService _execution;
    private readonly IMetadataProvider _metadata;

    public OracleDataTransferService(IExecutionService execution, IMetadataProvider metadata)
    {
        _execution = execution;
        _metadata = metadata;
    }

    public async Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(destination);
        var sw = Stopwatch.StartNew();

        var result = await _execution.ExecuteAsync(s, query, binds ?? new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Editor, SkipHistory = true }, ct).ConfigureAwait(false);
        if (result.Status != ExecutionStatus.Succeeded)
        {
            throw Failure(result, "내보낼 질의를 실행하지 못했다");
        }

        if (result.ResultSet is not { } rs)
        {
            throw new InvalidOperationException("질의가 결과 집합을 만들지 않았다(SELECT 문만 내보낼 수 있다).");
        }

        await using (rs.ConfigureAwait(false))
        {
            using var writer = TransferWriters.Create(options, destination);
            writer.Begin(rs.Columns);
            long rows = 0;
            var row = new object?[rs.Columns.Count];
            for (var pageIndex = 0; ; pageIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var page = await rs.GetPageAsync(pageIndex, ct).ConfigureAwait(false);
                for (var r = 0; r < page.RowCount; r++)
                {
                    for (var c = 0; c < row.Length; c++)
                    {
                        row[c] = page.Columns[c][r];
                    }

                    writer.WriteRow(row);
                    rows++;
                }

                progress?.Report(rows);
                if (page.RowCount == 0 || page.IsLast)
                {
                    break;
                }
            }

            writer.Finish();
            await destination.FlushAsync(ct).ConfigureAwait(false);
            return new TransferSummary(rows, sw.Elapsed) { Warnings = writer.Warnings.ToList() };
        }
    }

    public async Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        if (options.Format is not (TransferFormat.Csv or TransferFormat.Tsv or TransferFormat.Xlsx))
        {
            throw new NotSupportedException("가져오기는 CSV·TSV·xlsx 만 지원한다.");
        }

        if (options.BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "배치 크기는 1 이상이어야 한다.");
        }

        var sw = Stopwatch.StartNew();
        var table = await _metadata.GetTableAsync(s, options.Target, ct).ConfigureAwait(false);
        var targets = ResolveTargets(table, options.Mappings);
        var qualified = CellValueFormatter.QuoteIdentifier(options.Target.Owner) + "." + CellValueFormatter.QuoteIdentifier(options.Target.Name);
        var columnList = string.Join(", ", targets.Select(t => CellValueFormatter.QuoteIdentifier(t.Column.Name)));

        await using var local = await AsSeekableAsync(source, options.Format, ct).ConfigureAwait(false);
        var errors = new List<string>();
        var errorCount = 0;
        long inserted = 0;
        var batch = new List<PendingRow>(options.BatchSize);
        int[]? sourceIndex = null;
        long rowNo = 0;
        var committedAny = false;

        void RecordError(long row, string message)
        {
            errorCount++;
            if (errors.Count < MaxReportedErrors)
            {
                errors.Add($"{ErrorRowPrefix}{row.ToString(CultureInfo.InvariantCulture)}: {message}");
            }
        }

        async Task FlushAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            inserted += await InsertBatchAsync(s, qualified, columnList, batch, RecordError, ct).ConfigureAwait(false);
            progress?.Report(inserted);
            batch.Clear();
            if (!options.CommitAtEnd)
            {
                await s.CommitAsync(ct).ConfigureAwait(false);
                committedAny = true;
            }
        }

        try
        {
            foreach (var cells in TransferFileReader.ReadRows(options.Format, local.Stream))
            {
                rowNo++;
                ct.ThrowIfCancellationRequested();
                if (sourceIndex is null)
                {
                    if (options.HasHeader)
                    {
                        sourceIndex = ResolveSourceColumns(targets, HeaderNames(cells));
                        continue;
                    }

                    sourceIndex = ResolveSourceColumns(targets, Enumerable.Range(1, cells.Length).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList());
                }

                if (cells.Length == 0 || cells.All(c => c is null or ""))
                {
                    continue;
                }

                try
                {
                    var literals = new string[targets.Count];
                    for (var i = 0; i < literals.Length; i++)
                    {
                        var idx = sourceIndex[i];
                        var value = idx < cells.Length ? cells[idx] : null;
                        try
                        {
                            literals[i] = ImportValueConverter.ToLiteral(targets[i].Column, value);
                        }
                        catch (FormatException ex)
                        {
                            throw new FormatException($"열 {targets[i].Column.Name}: {ex.Message}", ex);
                        }
                    }

                    batch.Add(new PendingRow(rowNo, literals));
                }
                catch (FormatException ex)
                {
                    RecordError(rowNo, ex.Message);
                    continue;
                }

                if (batch.Count >= options.BatchSize)
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }

            await FlushAsync().ConfigureAwait(false);
            if (options.CommitAtEnd)
            {
                await s.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // 마지막에 한 번 커밋하기로 했으면 중간 실패는 아무것도 남기지 않는다.
            if (options.CommitAtEnd && !committedAny && s.HasPendingTransaction)
            {
                try
                {
                    await s.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // 롤백 실패는 원래 오류를 가리지 않는다.
                }
            }

            throw;
        }

        if (errorCount > errors.Count)
        {
            errors.Add($"오류 행이 더 있다(총 {errorCount.ToString(CultureInfo.InvariantCulture)}건, 앞 {MaxReportedErrors.ToString(CultureInfo.InvariantCulture)}건만 보고).");
        }

        return new TransferSummary(inserted, sw.Elapsed) { Warnings = errors };
    }

    private async Task<long> InsertBatchAsync(ISession s, string table, string columns, List<PendingRow> batch, Action<long, string> recordError, CancellationToken ct)
    {
        var result = await RunInsertAsync(s, BatchSql(table, columns, batch), ct).ConfigureAwait(false);
        if (result.Status == ExecutionStatus.Succeeded)
        {
            return result.RowsAffected ?? batch.Count;
        }

        if (result.Status != ExecutionStatus.Failed)
        {
            throw Failure(result, "가져오기 INSERT 가 실행되지 않았다");
        }

        if (batch.Count == 1)
        {
            recordError(batch[0].RowNumber, ErrorText(result));
            return 0;
        }

        // 배치가 통째로 실패했다(문장 단위로 되돌려져 있다). 한 행씩 다시 보내 틀린 행을 찾는다.
        long ok = 0;
        foreach (var row in batch)
        {
            ct.ThrowIfCancellationRequested();
            var single = await RunInsertAsync(s, BatchSql(table, columns, [row]), ct).ConfigureAwait(false);
            if (single.Status == ExecutionStatus.Succeeded)
            {
                ok += single.RowsAffected ?? 1;
            }
            else if (single.Status == ExecutionStatus.Failed)
            {
                recordError(row.RowNumber, ErrorText(single));
            }
            else
            {
                throw Failure(single, "가져오기 INSERT 가 실행되지 않았다");
            }
        }

        return ok;
    }

    private Task<ExecutionResult> RunInsertAsync(ISession s, string sql, CancellationToken ct)
    {
        var stmt = new SqlStatement(sql, default, StatementKind.Dml) { Verb = "INSERT" };
        return _execution.ExecuteAsync(s, stmt, new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Editor, SkipHistory = true }, ct);
    }

    internal static string BatchSql(string table, string columns, IReadOnlyList<PendingRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("INSERT INTO ").Append(table).Append(" (").Append(columns).Append(')');
        if (rows.Count == 1)
        {
            sb.Append(" VALUES (").AppendJoin(", ", rows[0].Literals).Append(')');
            return sb.ToString();
        }

        for (var i = 0; i < rows.Count; i++)
        {
            sb.Append(i == 0 ? " SELECT " : " UNION ALL SELECT ").AppendJoin(", ", rows[i].Literals).Append(" FROM DUAL");
        }

        return sb.ToString();
    }

    private static string ErrorText(ExecutionResult r) => r.Error is { } e ? $"{e.Code}: {e.Message}".Trim() : "알 수 없는 오류";

    private static InvalidOperationException Failure(ExecutionResult r, string prefix)
    {
        var detail = r.Status switch
        {
            ExecutionStatus.Denied => "정책 관문이 막았다" + (r.Policy is { } p ? $"({p.Rule})" : string.Empty),
            ExecutionStatus.Cancelled => "취소됐다",
            _ => ErrorText(r),
        };
        return new InvalidOperationException($"{prefix}: {detail}");
    }

    private static List<Target> ResolveTargets(TableInfo table, IReadOnlyList<ColumnMapping> mappings)
    {
        var byName = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var targets = new List<Target>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappings)
        {
            if (string.IsNullOrWhiteSpace(m.TargetColumn))
            {
                continue;
            }

            if (!byName.TryGetValue(m.TargetColumn, out var col))
            {
                throw new ArgumentException($"대상 테이블에 열 {m.TargetColumn} 이(가) 없다.", nameof(mappings));
            }

            if (col.IsVirtual)
            {
                throw new ArgumentException($"가상 열 {col.Name} 에는 넣을 수 없다.", nameof(mappings));
            }

            if (!used.Add(col.Name))
            {
                throw new ArgumentException($"대상 열 {col.Name} 이(가) 두 번 매핑됐다.", nameof(mappings));
            }

            targets.Add(new Target(m.SourceColumn, col));
        }

        if (targets.Count == 0)
        {
            throw new ArgumentException("매핑된 열이 없다.", nameof(mappings));
        }

        return targets;
    }

    private static List<string> HeaderNames(object?[] cells) =>
        cells.Select((c, i) => c is string { Length: > 0 } s ? s.Trim() : "COL" + (i + 1).ToString(CultureInfo.InvariantCulture)).ToList();

    private static int[] ResolveSourceColumns(List<Target> targets, List<string> names)
    {
        var result = new int[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            var idx = names.FindIndex(n => string.Equals(n, targets[i].Source.Trim(), StringComparison.OrdinalIgnoreCase));
            result[i] = idx >= 0 ? idx : throw new ArgumentException($"원본에 열 {targets[i].Source} 이(가) 없다.");
        }

        return result;
    }

    private static async Task<SeekableSource> AsSeekableAsync(Stream source, TransferFormat format, CancellationToken ct)
    {
        // xlsx(zip) 는 되감기가 필요하다. 안 되는 스트림은 임시 파일로 받아 쓴다(메모리에 올리지 않는다).
        if (format != TransferFormat.Xlsx || source.CanSeek)
        {
            return new SeekableSource(source, null);
        }

        var path = Path.Combine(Path.GetTempPath(), "oranda-import-" + Guid.NewGuid().ToString("N") + ".tmp");
        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await source.CopyToAsync(fs, ct).ConfigureAwait(false);
        fs.Position = 0;
        return new SeekableSource(fs, fs);
    }

    internal sealed record PendingRow(long RowNumber, string[] Literals);

    private sealed record Target(string Source, ColumnInfo Column);

    private sealed class SeekableSource(Stream stream, IAsyncDisposable? owned) : IAsyncDisposable
    {
        public Stream Stream { get; } = stream;

        public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
