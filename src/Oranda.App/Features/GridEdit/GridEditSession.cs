using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Features.GridEdit;

/// <summary>편집 중인 행의 상태(그리드 행 머리 표시용).</summary>
public enum GridEditRowState
{
    Unchanged,
    Modified,
    Inserted,
    Deleted,
    Failed,
}

/// <summary>
/// 결과 그리드 1개의 편집 모드(GRID-04·05, PRD 6.5.1) — TSK-06-04.
/// 원본 값은 그리드에서 읽기만 하고(<see cref="ReadCell"/>), 바뀐 값은 이 객체가 들고 있다가 반영 때 <see cref="IRowEditor"/> 로 보낸다.
/// 읽기 함수는 질의 결과의 원래 값을 돌려줘야 한다(<see cref="GridEditBinding.OriginalValues"/>: 그리드에 보이는 바꾼 값이 아니다) —
/// UPDATE·DELETE 의 WHERE 키, 낙관적 잠금의 원래 값, 「원래 값과 같으면 변경 지움」이 이 값을 쓴다(PRD 6.5.1-3·6).
/// 새 행은 음수 행 번호(-1, -2 …)를 쓴다. 편집 가능 여부는 <see cref="Capability"/>(읽기 전용 프로필·키 보존까지 반영).
/// </summary>
public sealed class GridEditSession
{
    private readonly IRowEditor _editor;
    private readonly IMetadataProvider _metadata;
    private readonly Func<long, int, object?> _read;
    private readonly Func<long> _loadedRows;
    private readonly Dictionary<long, Dictionary<int, object?>> _updates = [];
    private readonly Dictionary<long, Dictionary<int, object?>> _inserts = [];
    private readonly HashSet<long> _deletes = [];
    private readonly Dictionary<long, string> _failures = [];
    private readonly Dictionary<long, Dictionary<int, object?>> _copiedKeys = [];
    private readonly Dictionary<long, object?[]> _originals = [];
    private long _nextInsert = -1;

    private GridEditSession(IRowEditor editor, IMetadataProvider metadata, ISession session, SqlStatement query,
                            IReadOnlyList<ResultColumn> columns, Func<long, int, object?> read, Func<long> loadedRows, RowEditPlan plan)
    {
        _editor = editor;
        _metadata = metadata;
        Session = session;
        Query = query;
        Columns = columns;
        _read = read;
        _loadedRows = loadedRows;
        Plan = plan;
        Capability = plan.Capability;
    }

    public ISession Session { get; }

    public SqlStatement Query { get; }

    public IReadOnlyList<ResultColumn> Columns { get; }

    public RowEditPlan Plan { get; private set; }

    /// <summary>편집 가능 여부와 이유(그리드 위 안내 줄).</summary>
    public EditCapability Capability { get; private set; }

    public bool CanEdit => Capability.CanEdit;

    /// <summary>낙관적 잠금(PRD 6.5.1-6): 켜면 WHERE 에 원래 값 비교를 더한다.</summary>
    public bool OptimisticLock { get; set; }

    public bool IsDirty => _updates.Count > 0 || _inserts.Count > 0 || _deletes.Count > 0;

    /// <summary>
    /// 이 결과를 다시 읽는 방법(반영 뒤 다시 읽기·「ROWID 추가 후 다시 실행」). 편집기 밖 결과(객체 상세 「데이터」 탭)가 채운다.
    /// null 이면 <see cref="GridEditCommands.Rerun"/>(편집기의 현재 문서에서 다시 실행).
    /// </summary>
    public Func<SqlStatement?, CancellationToken, Task>? Rerun { get; set; }

    /// <summary>마지막 반영 결과 알림(실패 행 수·이유). 없으면 null.</summary>
    public string? Notice { get; private set; }

    /// <summary>바뀐 값·상태가 바뀌었다(그리드 다시 그리기).</summary>
    public event EventHandler? Changed;

    public IEnumerable<long> InsertedRows => _inserts.Keys.OrderByDescending(k => k);

    /// <summary>질의 결과 (행, 열)의 원래 값(바꾸기 전 값). 새 행은 원본이 없어 null.</summary>
    public object? ReadCell(long row, int ordinal) => Original(row, ordinal);

    /// <summary>
    /// 편집 모드를 연다. 대상 테이블은 <paramref name="target"/>, 없으면 FROM 절 첫 테이블(PRD 6.5.1-5).
    /// 키는 메타데이터 제공자(TSK-04-04)로 구한다.
    /// </summary>
    public static async Task<GridEditSession> OpenAsync(IRowEditor editor, IMetadataProvider metadata, ISession session,
                                                        SqlStatement query, IReadOnlyList<ResultColumn> columns,
                                                        Func<long, int, object?> read, Func<long> loadedRows,
                                                        DbObjectRef? target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(loadedRows);

        var plan = await PlanAsync(editor, metadata, session, query, columns, target, ct).ConfigureAwait(true);
        var s = new GridEditSession(editor, metadata, session, query, columns, read, loadedRows, plan);
        s.Capability = s.Guard(plan);
        return s;
    }

    /// <summary>조인 결과에서 편집 대상 테이블을 바꾼다. 바뀐 값은 버린다.</summary>
    public async Task ChooseTargetAsync(DbObjectRef target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        Plan = await PlanAsync(_editor, _metadata, Session, Query, Columns, target, ct).ConfigureAwait(true);
        Capability = Guard(Plan);
        ResetAll();
    }

    public bool IsEditableColumn(int ordinal) => CanEdit && Capability.EditableColumnOrdinals.Contains(ordinal);

    /// <summary>셀 값을 바꾼다. 원래 값과 같아지면 그 셀 변경을 지운다.</summary>
    public bool SetCell(long row, int ordinal, object? value)
    {
        if (!IsEditableColumn(ordinal) || _deletes.Contains(row))
        {
            return false;
        }

        if (row < 0)
        {
            if (!_inserts.TryGetValue(row, out var ins))
            {
                return false;
            }

            ins[ordinal] = value;
        }
        else
        {
            Remember(row);
            var original = Original(row, ordinal);
            if (!_updates.TryGetValue(row, out var upd))
            {
                upd = [];
                _updates[row] = upd;
            }

            if (Equals(Normalize(original), Normalize(value)))
            {
                upd.Remove(ordinal);
                if (upd.Count == 0)
                {
                    _updates.Remove(row);
                    _originals.Remove(row);
                }
            }
            else
            {
                upd[ordinal] = value;
            }
        }

        _failures.Remove(row);
        OnChanged();
        return true;
    }

    /// <summary>바뀐(또는 새 행의) 값이 있으면 true.</summary>
    public bool TryGetPendingValue(long row, int ordinal, out object? value)
    {
        value = null;
        var map = row < 0 ? _inserts.GetValueOrDefault(row) : _updates.GetValueOrDefault(row);
        return map is not null && map.TryGetValue(ordinal, out value);
    }

    /// <summary>새 행을 더하고 그 행 번호(음수)를 돌려준다.</summary>
    public long AddRow()
    {
        if (!CanEdit)
        {
            return 0;
        }

        var row = _nextInsert--;
        _inserts[row] = [];
        OnChanged();
        return row;
    }

    /// <summary>
    /// 값을 채운 새 행을 더한다(행 붙여넣기·행 복제). 편집할 수 있는 열의 NULL 이 아닌 값만 넣는다 — ROWID·대상 테이블 밖 열·LOB 은 비우고,
    /// NULL 은 넣지 않아 INSERT 에 그 열이 빠진다(열 DEFAULT 가 들어간다). <paramref name="flagKeys"/> 면 키 열(ROWID 가 아닌 PK·유니크 키)에 넣은 값을
    /// 「바꿔야 함」으로 표시한다(<see cref="NeedsChange"/>, 같은 키로 반영하면 키 위반). 편집할 수 없으면 0.
    /// </summary>
    public long AddRowWith(IEnumerable<KeyValuePair<int, object?>> values, bool flagKeys = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!CanEdit)
        {
            return 0;
        }

        var row = _nextInsert--;
        var ins = new Dictionary<int, object?>();
        foreach (var (ordinal, value) in values)
        {
            if (IsEditableColumn(ordinal) && Normalize(value) is { } v)
            {
                ins[ordinal] = v;
            }
        }

        _inserts[row] = ins;
        if (flagKeys && !Plan.UsesRowId)
        {
            var keys = Capability.KeyColumnOrdinals.Where(ins.ContainsKey).ToDictionary(o => o, o => ins[o]);
            if (keys.Count > 0)
            {
                _copiedKeys[row] = keys;
            }
        }

        OnChanged();
        return row;
    }

    /// <summary>복제한 새 행의 키 칸이 아직 복제한 값 그대로면 true(반영 전에 바꿔야 한다, 그리드 경고 색).</summary>
    public bool NeedsChange(long row, int ordinal) =>
        _copiedKeys.TryGetValue(row, out var keys) && keys.TryGetValue(ordinal, out var copied)
        && TryGetPendingValue(row, ordinal, out var now) && Equals(Normalize(now), Normalize(copied));

    public void DeleteRow(long row)
    {
        if (!CanEdit)
        {
            return;
        }

        if (row < 0)
        {
            _inserts.Remove(row);
            _copiedKeys.Remove(row);
        }
        else
        {
            Remember(row); // 고친 뒤 지운 행도 WHERE 키는 원래 값
            _updates.Remove(row);
            _deletes.Add(row);
        }

        _failures.Remove(row);
        OnChanged();
    }

    public void ResetRow(long row)
    {
        _originals.Remove(row);
        _updates.Remove(row);
        _inserts.Remove(row);
        _copiedKeys.Remove(row);
        _deletes.Remove(row);
        _failures.Remove(row);
        OnChanged();
    }

    public void ResetAll()
    {
        _updates.Clear();
        _inserts.Clear();
        _deletes.Clear();
        _failures.Clear();
        _copiedKeys.Clear();
        _originals.Clear();
        Notice = null;
        OnChanged();
    }

    public GridEditRowState GetRowState(long row) =>
        _failures.ContainsKey(row) ? GridEditRowState.Failed
        : _deletes.Contains(row) ? GridEditRowState.Deleted
        : row < 0 && _inserts.ContainsKey(row) ? GridEditRowState.Inserted
        : _updates.ContainsKey(row) ? GridEditRowState.Modified
        : GridEditRowState.Unchanged;

    /// <summary>실패한 행의 이유. 없으면 null.</summary>
    public string? GetRowFailure(long row) => _failures.GetValueOrDefault(row);

    /// <summary>반영할 변경 목록(삭제 → 수정 → 새 행 순).</summary>
    public IReadOnlyList<RowChange> BuildChanges()
    {
        var target = Capability.Target;
        if (!CanEdit || target is null)
        {
            return [];
        }

        var list = new List<RowChange>();
        foreach (var row in _deletes.Order())
        {
            list.Add(new RowChange(RowChangeKind.Delete, row, target) { Key = KeyOf(row), OriginalValues = OriginalsOf(row) });
        }

        foreach (var (row, values) in _updates.OrderBy(p => p.Key))
        {
            list.Add(new RowChange(RowChangeKind.Update, row, target)
            {
                Key = KeyOf(row),
                NewValues = Named(values),
                OriginalValues = OriginalsOf(row),
            });
        }

        foreach (var (row, values) in _inserts.OrderByDescending(p => p.Key))
        {
            if (values.Count > 0)
            {
                list.Add(new RowChange(RowChangeKind.Insert, row, target) { NewValues = Named(values) });
            }
        }

        return list;
    }

    /// <summary>미리보기 글(GRID-05): 생성 DML 과 바인드 값.</summary>
    public string BuildPreview() => RowDmlBuilder.FormatPreview(BuildChanges().Select(RowDmlBuilder.Build).OfType<RowDml>());

    /// <summary>반영. 성공한 행은 변경 목록에서 빠지고, 실패한 행은 남아 <see cref="GridEditRowState.Failed"/> 로 표시된다.</summary>
    public async Task<ApplyResult> ApplyAsync(bool commit, CancellationToken ct)
    {
        var changes = BuildChanges();
        if (changes.Count == 0)
        {
            return new ApplyResult(0, false);
        }

        var result = await _editor.ApplyAsync(Session, changes, commit, ct).ConfigureAwait(true);
        var failed = result.Failures.ToDictionary(f => f.RowIndex);
        var aborted = result.AppliedCount == 0 && failed.Count == changes.Count;
        _failures.Clear();
        foreach (var c in changes)
        {
            if (failed.TryGetValue(c.RowIndex, out var f))
            {
                _failures[c.RowIndex] = f.Error?.Message ?? "반영하지 못했습니다.";
                continue;
            }

            if (!aborted)
            {
                _updates.Remove(c.RowIndex);
                _inserts.Remove(c.RowIndex);
                _deletes.Remove(c.RowIndex);
                _copiedKeys.Remove(c.RowIndex);
                _originals.Remove(c.RowIndex);
            }
        }

        Notice = result.Failures.Count == 0
            ? $"{result.AppliedCount} 행 반영{(result.Committed ? ", 커밋함" : " (커밋 안 함)")}."
            : $"{result.AppliedCount} 행 반영, {result.Failures.Count} 행은 반영하지 않았습니다: {result.Failures[0].Error?.Message}"
              + (commit && !result.Committed ? " 커밋하지 않았습니다." : string.Empty);
        OnChanged();
        return result;
    }

    private static async Task<RowEditPlan> PlanAsync(IRowEditor editor, IMetadataProvider metadata, ISession session, SqlStatement query,
                                                     IReadOnlyList<ResultColumn> columns, DbObjectRef? target, CancellationToken ct)
    {
        var shape = SelectAnalyzer.Analyze(query.Text);
        if (shape.Block != EditBlockReason.None)
        {
            return new RowEditPlan(new EditCapability(false, shape.Block) { Message = shape.BlockMessage }, new string?[columns.Count]);
        }

        if (target is null)
        {
            var first = shape.Tables.FirstOrDefault(t => !t.IsInlineView);
            if (first is null)
            {
                return new RowEditPlan(new EditCapability(false, EditBlockReason.NotAnalyzable) { Message = "편집할 테이블이 없습니다." },
                    new string?[columns.Count]);
            }

            // 스키마를 적지 않았으면 현재 사용자(따옴표 없는 이름이므로 대문자).
            var owner = first.Owner ?? (session.Server.SessionUser ?? session.Profile.User).ToUpperInvariant();
            target = new DbObjectRef(owner, first.Name!, "TABLE");
        }

        var info = await metadata.GetTableAsync(session, target, ct).ConfigureAwait(true);
        var plan = RowEditPlanner.Plan(query, columns, TableKeys.From(info));

        // IRowEditor 계약 경로로 한 번 더(구현이 바뀌어도 판단은 IRowEditor 가 정본).
        var cap = editor.Analyze(query, columns, TableKeys.From(info));
        return plan with { Capability = cap };
    }

    /// <summary>계획에 앱 쪽 조건을 더한다: 읽기 전용 프로필, 조인 결과의 키 보존(불러온 행에서 같은 키가 두 번).</summary>
    private EditCapability Guard(RowEditPlan plan)
    {
        var cap = plan.Capability;
        if (!cap.CanEdit)
        {
            return cap;
        }

        if (Session.Profile.ReadOnly)
        {
            return new EditCapability(false, EditBlockReason.ReadOnlyProfile)
            {
                Message = $"읽기 전용 접속 '{Session.Profile.Name}' 에서는 결과를 편집할 수 없습니다.",
                Target = cap.Target,
            };
        }

        if (plan.IsJoin && KeyPreservation.FindDuplicate(cap.KeyColumnOrdinals, _loadedRows(), _read) is { } dup)
        {
            return new EditCapability(false, EditBlockReason.NotKeyPreserved)
            {
                Message = $"대상 테이블이 키 보존이 아닙니다(같은 키가 {dup + 1} 번째 행에 또 나옵니다). 다른 대상 테이블을 고르세요.",
                Target = cap.Target,
            };
        }

        return cap;
    }

    /// <summary>
    /// 질의 결과 행의 원래 값. 행을 처음 고치거나 지울 때 그 행 전체를 기억해 두므로(<see cref="Remember"/>) 반영 전에 그 페이지가
    /// 메모리에서 밀려나도(결과 페이지 캐시가 디스크로 내림) 같은 값이다. 기억한 값이 NULL 이면(그때 페이지가 없었을 수도 있다) 다시 읽는다.
    /// 질의 결과는 세션 동안 바뀌지 않으므로 두 값은 같다. 새 행(음수)은 원본이 없다.
    /// </summary>
    private object? Original(long row, int ordinal) =>
        row < 0 ? null : (_originals.TryGetValue(row, out var values) ? values[ordinal] : null) ?? _read(row, ordinal);

    /// <summary>질의 결과 행의 원래 값을 처음 한 번 기억한다(그 행이 화면에 있어 페이지가 메모리에 있을 때 부른다).</summary>
    private void Remember(long row)
    {
        if (row >= 0 && !_originals.ContainsKey(row))
        {
            _originals[row] = [.. Enumerable.Range(0, Columns.Count).Select(o => _read(row, o))];
        }
    }

    private Dictionary<string, object?> KeyOf(long row) =>
        Capability.KeyColumnOrdinals.ToDictionary(o => Plan.ColumnNames[o]!, o => Original(row, o), StringComparer.Ordinal);

    private Dictionary<string, object?> OriginalsOf(long row)
    {
        if (!OptimisticLock)
        {
            return [];
        }

        var keys = Capability.KeyColumnOrdinals;
        return Capability.EditableColumnOrdinals.Where(o => !keys.Contains(o))
            .ToDictionary(o => Plan.ColumnNames[o]!, o => Original(row, o), StringComparer.Ordinal);
    }

    private Dictionary<string, object?> Named(Dictionary<int, object?> values) =>
        values.OrderBy(p => p.Key).ToDictionary(p => Plan.ColumnNames[p.Key]!, p => p.Value, StringComparer.Ordinal);

    private static object? Normalize(object? v) => v is DBNull ? null : v;

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
