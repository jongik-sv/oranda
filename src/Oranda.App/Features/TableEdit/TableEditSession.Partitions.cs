using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「파티션」 페이지 편집 상태(설계 18.4). 테이블 편집 모델(<see cref="TableDraft"/>)과 따로 원본·편집 파티션 모델을 쥐고, 차이·문장은 Core
/// <see cref="PartitionDiff"/>·<see cref="PartitionScriptBuilder"/> 가 만든다. 파티션 문장은 테이블 문장 뒤, 테이블 이름 바꾸기(와 그 앞 규칙 이름 바꾸기) 앞에 들어간다
/// — 파티션 문장은 옛 테이블 이름을 쓴다(설계 18.1 이름 바꾸기 = 맨 끝).
/// </summary>
public sealed partial class TableEditSession
{
    private PartitionTableDraft? _partitionOriginal;
    private PartitionTableDraft? _partitionDraft;
    private IReadOnlyList<string> _globalIndexes = [];

    /// <summary>원본 파티션 모델. null = 파티션 정보를 못 읽어 편집하지 않음(<see cref="PartitionTableDraft.FromTable"/>).</summary>
    public PartitionTableDraft? PartitionOriginal => _partitionOriginal;

    /// <summary>편집 파티션 모델. null = 편집하지 않음.</summary>
    public PartitionTableDraft? PartitionDraft => _partitionDraft;

    /// <summary>파티션 차이(문장으로 못 만드는 편집은 <see cref="PartitionChanges.Problems"/>).</summary>
    public PartitionChanges PartitionChanges { get; private set; } = PartitionChanges.Empty;

    /// <summary>이 테이블의 비파티션 전역 인덱스 이름(나누기·합치기 뒤 다시 만들 대상, 방언이 요구할 때).</summary>
    public IReadOnlyList<string> GlobalIndexes => _globalIndexes;

    /// <summary>파티션 편집이 바뀌었나(문장이 없는 편집 = 문제만 있는 편집도 고친 것으로 본다).</summary>
    public bool IsPartitionDirty => !PartitionChanges.IsEmpty || PartitionChanges.Problems.Count > 0;

    /// <summary>파티션을 고칠 수 있다(정보를 읽었고, 일반 테이블이고, 방언이 파티션 편집을 한다).</summary>
    public bool CanEditPartitions => _partitionOriginal is not null && !IsRestricted && !IsNewTable
        && Dialect.UnsupportedReasonKey(TableEditFeature.Partitioning, Context.Server) is null;

    private Action? _openPartitionsPage;

    /// <summary>
    /// 「파티션 테이블로 바꾸기…」가 부르는 동작: 객체 상세가 「파티션」 페이지를 끼우고 고른다. 일반 테이블에만 쓴다
    /// (파티션 테이블은 「파티션」 페이지가 늘 있다). null = 그 길이 없음(새 테이블 탭 등).
    /// </summary>
    public Action? OpenPartitionsPage
    {
        get => _openPartitionsPage;
        set
        {
            _openPartitionsPage = value;
            OnPropertyChanged(nameof(CanOpenPartitioning));
        }
    }

    /// <summary>「저장」 페이지의 「파티션 테이블로 바꾸기…」 단추를 보인다: 일반 테이블이고 이 서버에서 파티션 테이블로 바꿀 수 있다(못 바꾸면 막다른 페이지라 숨긴다).</summary>
    public bool CanOpenPartitioning => _openPartitionsPage is not null && CanChoosePartitionMethod;

    /// <summary>「파티션 테이블로 바꾸기…」: 「파티션」 페이지로 간다(방식·키 열은 그 페이지에서 고른다).</summary>
    public void OpenPartitioning()
    {
        if (CanOpenPartitioning)
        {
            _openPartitionsPage!();
        }
    }

    /// <summary>파티션을 못 고치는 까닭(고칠 수 있으면 빈 글).</summary>
    public string PartitionLockedReason => CanEditPartitions ? string.Empty
        : IsRestricted ? RestrictionText
        : _partitionOriginal is null ? TableEditText.T("파티션 정보를 읽지 못해 고칠 수 없습니다(시스템·참조 파티션이거나 옛 캐시)", "Partition details could not be read (system or reference partitioning, or an old cache)")
        : Dialect.UnsupportedReasonKey(TableEditFeature.Partitioning, Context.Server) is { } key ? Oranda.App.Features.Localization.UiText.Get(key, key)
        : string.Empty;

    /// <summary>
    /// 파티션 원본을 넣는다(편집을 켤 때·실행 뒤 다시 읽을 때). 편집 모델은 원본 그대로 다시 시작한다.
    /// <paramref name="table"/> 의 인덱스 중 <see cref="PartitioningInfo.PartitionedIndexes"/> 에 없는 것 = 전역 인덱스.
    /// </summary>
    public void SetPartitions(TableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _partitionOriginal = PartitionTableDraft.FromTable(table);
        _partitionDraft = _partitionOriginal;
        var partitioned = new HashSet<string>(table.Partitioning?.PartitionedIndexes ?? [], StringComparer.Ordinal);
        _globalIndexes = [.. table.Indexes.Select(i => i.Name).Where(n => !partitioned.Contains(n))];
        Recompute();
    }

    /// <summary>파티션 편집 모델을 바꾼다(차이·문장·줄을 다시 만듦). 고칠 수 없으면 아무것도 하지 않는다.</summary>
    public void UpdatePartitions(Func<PartitionTableDraft, PartitionTableDraft> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!CanEditPartitions || _partitionDraft is null)
        {
            return;
        }

        var next = change(_partitionDraft);
        if (ReferenceEquals(next, _partitionDraft))
        {
            return;
        }

        _partitionDraft = next;
        Recompute();
    }

    /// <summary>파티션 편집을 원본으로 되돌린다.</summary>
    public void RevertPartitions()
    {
        if (_partitionDraft is not null && !ReferenceEquals(_partitionDraft, _partitionOriginal))
        {
            _partitionDraft = _partitionOriginal;
            Recompute();
        }
    }

    /// <summary>파티션 차이·문장을 다시 만든다(<see cref="Recompute"/> 안에서). 실패하면 이유를 돌려준다.</summary>
    private (IReadOnlyList<TableEditStatement> Statements, string? Error) BuildPartitionStatements()
    {
        PartitionChanges = PartitionChanges.Empty;
        if (_partitionOriginal is null || _partitionDraft is null || ReferenceEquals(_partitionOriginal, _partitionDraft))
        {
            return ([], null);
        }

        try
        {
            PartitionChanges = _engine.DiffPartitions(_partitionOriginal, _partitionDraft, Dialect, Context.Server);
            if (PartitionChanges.IsEmpty)
            {
                return ([], null);
            }

            return (_engine.BuildPartitions(Original.Owner, Original.Name, _partitionOriginal, _partitionDraft, Dialect, Context.Server, LiveGlobalIndexes()), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>
    /// 파티션 문장이 다시 만들 전역 인덱스(Tibero 나누기·합치기 뒤): 파티션 문장은 인덱스 문장 뒤에 오므로 편집 모델 기준 — 지운 인덱스는 빼고,
    /// 이름을 바꾼 인덱스는 새 이름, 이 편집에서 새로 만든(비파티션) 인덱스는 더한다.
    /// </summary>
    private IReadOnlyList<string> LiveGlobalIndexes()
    {
        var originalGlobals = new HashSet<string>(_globalIndexes, StringComparer.Ordinal);
        return [.. Draft.Indexes
            .Where(i => !i.IsDropped && (IsNewRow(i.RowKey) || Original.Index(i.RowKey) is { } o && originalGlobals.Contains(o.Name)))
            .Select(i => i.Name)];
    }

    /// <summary>
    /// 테이블 문장과 파티션 문장을 합친다: 테이블 이름 바꾸기가 있으면 그 앞(규칙 이름 바꾸기까지 앞)에, 없으면 끝에 파티션 문장을 둔다.
    /// </summary>
    private static TableEditScript MergePartitionStatements(TableEditScript table, IReadOnlyList<TableEditStatement> partitions)
    {
        if (partitions.Count == 0)
        {
            return table;
        }

        var list = table.Statements.ToList();
        var at = list.Count;
        var rename = list.FindIndex(s => s.RowKey == TableDraft.TableRowKey && s.Page == TableEditPage.Description
                                         && s.Statement.Text.Contains(" RENAME TO ", StringComparison.Ordinal));
        if (rename >= 0)
        {
            // 빌더는 규칙 이름 바꾸기(RENAME CONSTRAINT·ALTER INDEX … RENAME TO)를 테이블 RENAME TO 바로 앞에 잇달아 둔다(그 앞은 주석 문장).
            at = rename;
            while (at > 0 && list[at - 1].Effect.Kind == TableEditEffectKind.Rename && list[at - 1].Page is TableEditPage.Constraints or TableEditPage.Indexes)
            {
                at--;
            }
        }

        list.InsertRange(at, partitions);
        return new TableEditScript(list);
    }

    /// <summary>
    /// 파티션 편집의 실행 전 검사 줄(설계 18.4): 덜 쓴 새 줄(이름·경계 빔) ✖ — 깨진 문장이 나가지 않게, 파티션 키 열 지우기·형식 바꾸기 ✖,
    /// 지우기·비우기·합치기의 행 수 ⚠(<see cref="PartitionWarnings"/>). 실행·묶음에 넣기 앞 검사(<see cref="TableEditRunner.CheckAsync"/>)가 더한다.
    /// </summary>
    public IReadOnlyList<CheckFinding> PartitionFindings()
    {
        var list = new List<CheckFinding>();
        string Subject(string name) => name.Length > 0 ? name : TableEditText.T("새 파티션", "new partition");
        foreach (var row in PartitionRows.Where(r => r.HasError))
        {
            list.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, row.Key) { Subject = Subject(row.Name), Detail = row.Error });
        }

        // Core 가 문장을 만들지 않은 편집(경계 바꾸기·방언 미지원 …)은 ✖: 다른 문장만 반영되고 파티션 편집이 말없이 버려지지 않게(설계 4장).
        foreach (var problem in PartitionChanges.Problems.Where(p => PartitionRows.All(r => r.Key != p.RowKey || !r.HasError)))
        {
            var name = _partitionDraft?.Partitions.FirstOrDefault(p => p.RowKey == problem.RowKey)?.Name
                       ?? _partitionOriginal?.Partitions.FirstOrDefault(p => p.RowKey == problem.RowKey)?.Name ?? string.Empty;
            list.Add(new CheckFinding(CheckSeverity.Block, CheckCode.Unsupported, problem.RowKey) { Subject = Subject(name), Detail = problem.ReasonKey });
        }

        // 일반 → 파티션(설계 18.4): 키가 비었거나 지금 편집 모델에 없는 열(이름을 바꿨거나 뺀 열), 범위·목록인데 파티션 줄이 없으면 깨진 문장이 된다.
        if (_partitionOriginal is { Scheme: null } && _partitionDraft?.Scheme is { } converting)
        {
            var live = Draft.Columns.Where(c => !c.IsDropped).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            if (converting.KeyColumns.Count == 0)
            {
                list.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, TableDraft.TableRowKey)
                {
                    Subject = Draft.Name,
                    Detail = TableEditText.T("파티션 키 열을 고르세요", "choose the partition key columns"),
                });
            }

            foreach (var key in converting.KeyColumns.Where(k => !live.Contains(k)))
            {
                list.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, TableDraft.TableRowKey)
                {
                    Subject = key,
                    Detail = TableEditText.T("파티션 키 열이 이 테이블에 없습니다(이름을 바꿨거나 지운 열)", "the partition key column is not in the table (renamed or dropped)"),
                });
            }

            if (converting.Method is PartitionMethod.Range or PartitionMethod.List && !_partitionDraft.Partitions.Any(p => !p.IsDropped))
            {
                list.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, TableDraft.TableRowKey)
                {
                    Subject = Draft.Name,
                    Detail = TableEditText.T("파티션을 하나 이상 더하세요", "add at least one partition"),
                });
            }
        }

        // 키 열: 기존 파티션 테이블의 키, 또는 일반 → 파티션으로 바꾸며 고른 키.
        var keys = (_partitionDraft?.Scheme ?? _partitionOriginal?.Scheme)?.KeyColumns ?? [];
        foreach (var key in keys)
        {
            if (Original.Columns.FirstOrDefault(c => c.Name == key) is not { } column || Changes.Columns.FirstOrDefault(d => d.RowKey == column.RowKey) is not { } diff)
            {
                continue;
            }

            if (diff.State == DraftRowState.Dropped)
            {
                list.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, column.RowKey)
                {
                    Subject = key,
                    Detail = TableEditText.T("파티션 키 열은 지울 수 없습니다", "a partition key column cannot be dropped"),
                });
            }
            else if (diff.Changes.HasFlag(ColumnChange.Type))
            {
                list.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, column.RowKey)
                {
                    Subject = key,
                    Detail = TableEditText.T("파티션 키 열의 형식은 바꿀 수 없습니다", "the type of a partition key column cannot change"),
                });
            }
        }

        foreach (var warning in PartitionWarnings)
        {
            list.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, TableDraft.TableRowKey) { Detail = warning });
        }

        return list;
    }

    /// <summary>파티션 페이지 줄을 맞춘다(파티션 화면 partial 이 채운다).</summary>
    partial void SyncPartitionRows();
}
