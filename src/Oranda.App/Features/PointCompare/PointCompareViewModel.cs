using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;

namespace Oranda.App.Features.PointCompare;

/// <summary>시점 목록의 한 줄. 커밋 묶음이면 기준·커밋 시점이 있고, 「N분 전」·「직접 지정」은 열 때 시점 번호를 읽는다.</summary>
public sealed record PointOption(string Label, PointOptionKind Kind)
{
    public long? BasePoint { get; init; }
    public DateTimeOffset? BaseAt { get; init; }
    public long? CommitPoint { get; init; }
    public DateTimeOffset? CommitAt { get; init; }
    public string? TransactionId { get; init; }
    public long? GroupId { get; init; }
    public bool HasScriptFile { get; init; }
    public int? Minutes { get; init; }

    /// <summary>이 묶음이 건드린 표. 비면 화면의 표를 그대로 쓴다.</summary>
    public IReadOnlyList<DbObjectRef> Tables { get; init; } = [];

    /// <summary>표마다 범위 근거 문장(원장 문서에서).</summary>
    public IReadOnlyDictionary<DbObjectRef, IReadOnlyList<CompareStatementScope>> Scopes { get; init; } = new Dictionary<DbObjectRef, IReadOnlyList<CompareStatementScope>>();

    public override string ToString() => Label;
}

public enum PointOptionKind { Group, MinutesAgo, Custom }

/// <summary>바뀐 행 표의 한 줄(되돌림은 바뀐 열마다 한 줄).</summary>
public sealed record CompareRowItem(string Kind, string Key, string Column, string Base, string Commit, string Now)
{
    public bool IsConflict { get; init; }
}

/// <summary>반영 결과(1행을 바꾼 문장, 반대 문장 기록용).</summary>
public sealed record CompareApplyResult(int Applied, int ZeroRows, int Conflicts, int Failed, string? FirstError, IReadOnlyList<RollbackStep> Executed)
{
    /// <summary>비교한 표.</summary>
    public DbObjectRef? Table { get; init; }

    /// <summary>반영 직전 시점(반대 묶음의 기준).</summary>
    public ChangePoint? BeforePoint { get; init; }

    /// <summary>고른 행 범위에서 건너뛴 행: 그때 없던 행, 같은 값.</summary>
    public int MissingThen { get; init; }

    public int Unchanged { get; init; }

    /// <summary>고른 행 범위로 반영했는가(그리드 G2).</summary>
    public bool KeyScope { get; init; }
}

/// <summary>
/// 화면 3 「이 실행 전과 비교」(설계 8장, 시안 화면 3, RB-32). 시점을 고르면 세 시점(기준·커밋 직후·지금)을 견주어 바뀐 행과 되돌리는 DML 을 보이고,
/// 「반영(커밋 안 함)」으로 차례로 보낸다. DB 일은 <see cref="IPointCompareDb"/>, 화면 밖 일(새 편집기·원복 스크립트·반대 문장 기록)은 콜백.
/// </summary>
public sealed class PointCompareViewModel : INotifyPropertyChanged
{
    /// <summary>표에 보이는 줄 상한(DML 은 모두 만든다).</summary>
    public const int MaxShownRows = 1000;

    private readonly IPointCompareDb _db;
    private readonly IReadOnlyList<IReadOnlyList<object?>>? _keys;
    private bool _widened;
    private int _missingThen;
    private int _missingNow;
    private PointOption? _selectedPoint;
    private DbObjectRef? _selectedTable;
    private string _customText = string.Empty;
    private bool _includeConflicts;
    private bool _busy;
    private string _notes = string.Empty;
    private string _error = string.Empty;
    private string _status = string.Empty;
    private string _counts = string.Empty;
    private string _dml = string.Empty;
    private string _rowsHeader = string.Empty;
    private string _range = string.Empty;
    private bool _snapshotTooOld;
    private ComparePlan? _plan;
    private CompareScript? _script;
    private PointOption? _loaded;

    internal PointCompareViewModel(IPointCompareDb db, string connection, DbObjectRef? table, IReadOnlyList<PointOption> points,
                                 IReadOnlyList<IReadOnlyList<object?>>? keys = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(points);
        _db = db;
        _keys = keys;
        Connection = connection;
        foreach (var p in points)
        {
            Points.Add(p);
        }

        _selectedPoint = Points.FirstOrDefault();
        _selectedTable = table ?? (_selectedPoint is { Tables.Count: > 0 } sp ? sp.Tables[0] : null);
        RefreshTables();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Connection { get; }

    public string PointName => _db.Dialect.PointName;

    public ObservableCollection<PointOption> Points { get; } = [];

    public ObservableCollection<DbObjectRef> Tables { get; } = [];

    public ObservableCollection<CompareRowItem> Rows { get; } = [];

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    /// <summary>고른 행 범위 안내(200행 상한 등). 키 범위일 때 안내 줄 맨 위에 보인다.</summary>
    public string? ScopeNote { get; init; }

    /// <summary>그리드 키 열 이름(키로 열었을 때).</summary>
    public IReadOnlyList<string>? KeyColumns { get; init; }

    /// <summary>「스크립트를 새 편집기에 열기」.</summary>
    public Action<string>? OpenEditor { get; init; }

    /// <summary>「원복 스크립트 열기」(기준 시점을 잃었을 때, 묶음에 원복 스크립트가 있으면).</summary>
    public Action<long>? OpenRollbackScript { get; init; }

    /// <summary>반영 뒤(1행을 바꾼 문장이 있으면): 반대 문장 기록(RD-8)·결과 다시 조회 같은 화면 밖 일.</summary>
    public Func<CompareApplyResult, PointOption, Task>? Applied { get; init; }

    /// <summary>반영 전 확인(운영 접속). null 이면 묻지 않는다(미리보기를 본 사용자가 「반영」을 눌렀다).</summary>
    public Func<string, CancellationToken, Task<bool>>? ConfirmApply { get; init; }

    public string Title => "이 실행 전과 비교 · " + (_selectedTable is { } t ? Name(t) : string.Empty);

    public PointOption? SelectedPoint
    {
        get => _selectedPoint;
        set
        {
            if (Set(ref _selectedPoint, value))
            {
                OnPropertyChanged(nameof(IsCustom));
                OnPropertyChanged(nameof(CanWidenToGroup));
                OnPropertyChanged(nameof(CommitText));
                RefreshTables();
            }
        }
    }

    public bool IsCustom => _selectedPoint?.Kind == PointOptionKind.Custom;

    /// <summary>직접 지정: 시점 번호 또는 <c>yyyy-MM-dd HH:mm:ss</c>.</summary>
    public string CustomText
    {
        get => _customText;
        set => Set(ref _customText, value ?? string.Empty);
    }

    public DbObjectRef? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (Set(ref _selectedTable, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public bool HasManyTables => Tables.Count > 1;

    public string CommitText => _selectedPoint is { CommitPoint: { } c } p
        ? $"커밋 {PointName} {c.ToString(CultureInfo.InvariantCulture)}" + (p.CommitAt is { } at ? " (" + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ")" : string.Empty)
        : "커밋 시점 없음";

    public bool IncludeConflicts
    {
        get => _includeConflicts;
        set
        {
            if (Set(ref _includeConflicts, value))
            {
                BuildScript();
            }
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanCompare));
            }
        }
    }

    public string Notes { get => _notes; private set => Set(ref _notes, value); }

    public string ErrorText { get => _error; private set => Set(ref _error, value); }

    public string StatusText { get => _status; private set => Set(ref _status, value); }

    public string CountsText { get => _counts; private set => Set(ref _counts, value); }

    public string RowsHeader { get => _rowsHeader; private set => Set(ref _rowsHeader, value); }

    public string RangeText { get => _range; private set => Set(ref _range, value); }

    public string DmlText
    {
        get => _dml;
        private set
        {
            if (Set(ref _dml, value))
            {
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public bool CanCompare => !_busy;

    /// <summary>고른 행의 키로 범위를 좁히는 중인가(그리드에서 열었고 「묶음 전체 보기」를 누르지 않음).</summary>
    public bool UsesKeys => _keys is not null && !_widened;

    /// <summary>「이 커밋 묶음 전체 보기」: 그리드에서 열었고 커밋 묶음을 골랐을 때.</summary>
    public bool CanWidenToGroup => UsesKeys && _selectedPoint?.Kind == PointOptionKind.Group;

    /// <summary>그때 없던 행(고른 행 범위에서 건너뜀).</summary>
    public int MissingThen => _missingThen;

    /// <summary>바뀐 칸이 없는 행.</summary>
    public int Unchanged => _plan?.Count(CompareKind.Same) ?? 0;

    /// <summary>범위를 고른 행에서 커밋 묶음 전체로 넓혀 다시 비교한다(지운 행까지 되살릴 수 있다).</summary>
    public async Task WidenToGroupAsync(CancellationToken ct = default)
    {
        if (!CanWidenToGroup)
        {
            return;
        }

        _widened = true;
        OnPropertyChanged(nameof(UsesKeys));
        OnPropertyChanged(nameof(CanWidenToGroup));
        await LoadAsync(ct).ConfigureAwait(true);
    }

    public bool CanApply => !_busy && _script is { Statements.Count: > 0 };

    public bool CanOpenRollbackScript => _snapshotTooOld && _loaded is { GroupId: not null, HasScriptFile: true } && OpenRollbackScript is not null;

    public ComparePlan? Plan => _plan;

    public CompareScript? Script => _script;

    /// <summary>고른 시점으로 다시 비교한다.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_selectedPoint is not { } option || _selectedTable is not { } table)
        {
            ErrorText = "비교할 시점과 테이블을 고르세요.";
            return;
        }

        if (IsBusy)
        {
            return; // 불러오기·반영이 겹치지 않게
        }

        IsBusy = true;
        ErrorText = string.Empty;
        StatusText = string.Empty;
        try
        {
            var point = await ResolveAsync(option, ct).ConfigureAwait(true);
            if (point is null)
            {
                Clear(); // 이전 시점의 DML 을 남겨 반영하지 않게
                return;
            }

            var request = new CompareRequest(table, point.BasePoint!.Value)
            {
                BaseAt = point.BaseAt,
                CommitPoint = point.CommitPoint,
                CommitAt = point.CommitAt,
                TransactionId = point.TransactionId,
                Statements = point.Scopes.TryGetValue(table, out var scopes) ? scopes : [],
                Keys = UsesKeys ? _keys : null,
                KeyColumns = KeyColumns,
            };
            var load = await PointCompareLoader.LoadAsync(_db, request, Now(), ct).ConfigureAwait(true);
            _loaded = point;
            _snapshotTooOld = load.SnapshotTooOld;
            OnPropertyChanged(nameof(CanOpenRollbackScript));
            var notes = new List<string>(load.Notes);
            if (load.Plan is { } plan)
            {
                notes.AddRange(plan.Warnings);
            }

            if (ScopeNote is { Length: > 0 } scope && UsesKeys)
            {
                notes.Insert(0, scope);
            }

            Notes = string.Join(Environment.NewLine, notes);
            RangeText = load.Range is { } r ? "범위: " + r : string.Empty;
            _plan = load.Plan;
            _missingThen = 0;
            if (UsesKeys && _plan is { } keyed)
            {
                // 그리드가 고른 행(설계 8.5 G2): 되돌림만 만든다. 그때 없던 행은 지금처럼 건너뜀으로 센다.
                _missingThen = keyed.Rows.Count(x => x.Kind == CompareKind.Remove);
                _missingNow = keyed.Rows.Count(x => x.Kind == CompareKind.Restore); // 고른 뒤 다른 세션이 지운 행
                _plan = keyed with { Rows = [.. keyed.Rows.Where(x => x.Kind is CompareKind.Revert or CompareKind.Conflict or CompareKind.Same)] };
            }

            OnPropertyChanged(nameof(CanWidenToGroup));
            if (load.Error is { } error)
            {
                Clear();
                ErrorText = error;
                return;
            }

            ShowRows();
            BuildScript();
        }
        catch (OperationCanceledException)
        {
            Clear();
            ErrorText = "비교를 취소했습니다.";
        }
        catch (Exception e)
        {
            Clear();
            ErrorText = "비교하지 못했습니다: " + e.Message.ReplaceLineEndings(" ");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Clear()
    {
        _plan = null;
        _script = null;
        Rows.Clear();
        CountsText = string.Empty;
        RowsHeader = string.Empty;
        DmlText = string.Empty;
        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>만들어진 DML 을 차례로 보낸다(커밋 안 함). 0행·충돌은 세고 계속한다(설계 14장).</summary>
    public async Task<CompareApplyResult?> ApplyAsync(CancellationToken ct = default)
    {
        if (_script is not { Statements.Count: > 0 } script || _loaded is not { } point || _plan is not { } plan || IsBusy)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            if (ConfirmApply is { } confirm && !await confirm(DmlText, ct).ConfigureAwait(true))
            {
                StatusText = "반영을 취소했습니다.";
                return null;
            }

            ChangePoint? before = null;
            try
            {
                if (await _db.CurrentPointAsync(ct).ConfigureAwait(true) is { } cur)
                {
                    before = new ChangePoint(cur, PointName, Now(), "compare");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 반대 묶음의 기준 시점만 빠진다
            }

            int applied = 0, zero = 0, conflicts = 0, failed = 0;
            string? first = null;
            var executed = new List<RollbackStep>();
            foreach (var s in script.Statements)
            {
                try
                {
                    var rows = await _db.ExecuteAsync(s.Sql, ct).ConfigureAwait(true);
                    if (rows == 1)
                    {
                        applied++;
                        executed.Add(new RollbackStep(s.Sql));
                    }
                    else if (rows == 0)
                    {
                        zero++;
                    }
                    else
                    {
                        applied++; // 키 조건이라 2행 이상은 드물다(ROWID 표). 반대 문장은 남기지 않는다
                    }
                }
                catch (PointCompareDbException e) when (e.Error?.Kind == DbErrorKind.ConstraintViolation)
                {
                    conflicts++;
                    first ??= "충돌: 같은 키가 이미 있음 — " + e.Message;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failed++;
                    first ??= e.Message.ReplaceLineEndings(" ");
                }
            }

            var result = new CompareApplyResult(applied, zero, conflicts, failed, first, executed) { Table = plan.Table.Table, BeforePoint = before, MissingThen = UsesKeys ? _missingThen : 0, Unchanged = UsesKeys ? plan.Count(CompareKind.Same) : 0, KeyScope = UsesKeys };
            _script = null; // 한 번만 반영한다(다시 하려면 「비교」로 새로 만든다)
            OnPropertyChanged(nameof(CanApply));
            StatusText = Describe(result);
            if (Applied is { } after)
            {
                try
                {
                    await after(result, point).ConfigureAwait(true);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    StatusText += " (뒤처리 실패: " + e.Message.ReplaceLineEndings(" ") + ")";
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText = "반영을 멈췄습니다. 이미 보낸 문장은 커밋 전이라 롤백으로 되돌릴 수 있습니다.";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenScriptInEditor()
    {
        if (DmlText.Length > 0)
        {
            OpenEditor?.Invoke(DmlText);
        }
    }

    public void OpenRollbackScriptOfGroup()
    {
        if (CanOpenRollbackScript && _loaded?.GroupId is { } id)
        {
            OpenRollbackScript!(id);
        }
    }

    public static string Describe(CompareApplyResult r)
    {
        var parts = new List<string> { $"{r.Applied.ToString(CultureInfo.InvariantCulture)} 행을 되돌렸습니다(커밋 안 함). 커밋하려면 커밋 단추, 되돌리려면 롤백." };
        if (r.ZeroRows > 0)
        {
            parts.Add($"0행 {r.ZeroRows.ToString(CultureInfo.InvariantCulture)}개: 그 뒤 다른 사람이 바꾼 행");
        }

        if (r.Conflicts > 0)
        {
            parts.Add($"충돌 {r.Conflicts.ToString(CultureInfo.InvariantCulture)}개: 같은 키가 이미 있음");
        }

        if (r.Failed > 0)
        {
            parts.Add($"실패 {r.Failed.ToString(CultureInfo.InvariantCulture)}개: {r.FirstError}");
        }

        return string.Join(" ", parts);
    }

    private async Task<PointOption?> ResolveAsync(PointOption option, CancellationToken ct)
    {
        switch (option.Kind)
        {
            case PointOptionKind.Group when option.BasePoint is not null:
                return option;
            case PointOptionKind.MinutesAgo when option.Minutes is { } m:
                {
                    var at = Now().AddMinutes(-m);
                    if (await _db.PointMinutesAgoAsync(m, ct).ConfigureAwait(true) is not { } n)
                    {
                        ErrorText = $"{m.ToString(CultureInfo.InvariantCulture)}분 전의 {PointName} 를 읽지 못했습니다.";
                        return null;
                    }

                    return option with { BasePoint = n, BaseAt = at };
                }

            case PointOptionKind.Custom:
                {
                    var text = CustomText.Trim();
                    if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                    {
                        return option with { BasePoint = n };
                    }

                    if (DateTime.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
                    {
                        var at = new DateTimeOffset(local);
                        if (at > Now())
                        {
                            ErrorText = "지금보다 뒤의 시각입니다.";
                            return null;
                        }

                        // 서버 시각 기준 「몇 분 전」으로 바꿔 읽는다(클라이언트·서버 시간대가 달라도 맞게)
                        if (await _db.PointMinutesAgoAsync((decimal)(Now() - at).TotalMinutes, ct).ConfigureAwait(true) is { } p)
                        {
                            return option with { BasePoint = p, BaseAt = at };
                        }

                        ErrorText = $"그 시각의 {PointName} 를 읽지 못했습니다(너무 오래된 시각일 수 있음).";
                        return null;
                    }

                    ErrorText = $"{PointName} 번호 또는 yyyy-MM-dd HH:mm:ss 로 입력하세요.";
                    return null;
                }

            default:
                ErrorText = "시점을 알 수 없습니다.";
                return null;
        }
    }

    private void ShowRows()
    {
        Rows.Clear();
        if (_plan is not { } plan)
        {
            return;
        }

        var cols = plan.Table.Columns;
        string Value(IReadOnlyList<object?>? v, int p) => v is null ? "없음" : v[p] is null ? "NULL" : Convert.ToString(v[p], CultureInfo.InvariantCulture) ?? string.Empty;
        string Summary(IReadOnlyList<object?> v) => string.Join(" · ", Enumerable.Range(0, cols.Count).Where(i => cols[i].Kind != RollbackValueKind.Large).Take(4).Select(i => Value(v, i)));

        var changed = plan.Rows.Where(r => r.Kind != CompareKind.Same).OrderBy(r => Order(r.Kind)).ToList();
        foreach (var r in changed)
        {
            if (Rows.Count >= MaxShownRows)
            {
                break;
            }

            var key = PointCompareKeyText(r);
            var kind = KindText(r.Kind);
            switch (r.Action)
            {
                case CompareAction.Update:
                    foreach (var p in r.ChangedColumns)
                    {
                        Rows.Add(new CompareRowItem(kind, key, cols[p].Name, Value(r.Base, p), Value(r.Commit, p), Value(r.Now, p)) { IsConflict = r.Kind == CompareKind.Conflict });
                    }

                    break;
                case CompareAction.Insert:
                    Rows.Add(new CompareRowItem(kind, key, Summary(r.Base!), "있음", r.Commit is null ? "(지워짐)" : "있음", r.Now is null ? "없음" : "있음")
                    {
                        IsConflict = r.Kind == CompareKind.Conflict,
                    });
                    break;
                default:
                    Rows.Add(new CompareRowItem(kind, key, "기준 시점에 없던 행", "없음", r.Commit is null ? "없음" : "새로 생김", r.Now is null ? "없음" : "있음")
                    {
                        IsConflict = r.Kind == CompareKind.Conflict,
                    });
                    break;
            }
        }

        RowsHeader = $"바뀐 행 ({plan.Rows.Count.ToString(CultureInfo.InvariantCulture)}행 중 {changed.Count.ToString(CultureInfo.InvariantCulture)}행"
                     + (Rows.Count < changed.Count ? $", 앞 {MaxShownRows.ToString(CultureInfo.InvariantCulture)}줄 표시)" : ")");
        CountsText = UsesKeys
            ? $"되돌릴 값 {plan.Count(CompareKind.Revert)} 행 · UPDATE   충돌 {plan.Count(CompareKind.Conflict)} · " + (_includeConflicts ? "포함" : "제외")
              + $"   건너뜀: 그때 없던 행 {_missingThen}, 같은 값 {plan.Count(CompareKind.Same)}" + (_missingNow > 0 ? $", 지금 없는 행 {_missingNow}" : string.Empty)
            : $"되돌릴 값 {plan.Count(CompareKind.Revert)} 행 · UPDATE   되살릴 행 {plan.Count(CompareKind.Restore)} · INSERT   "
              + $"지울 행 {plan.Count(CompareKind.Remove)} · DELETE   충돌 {plan.Count(CompareKind.Conflict)} · " + (_includeConflicts ? "포함" : "제외");
    }

    private void BuildScript()
    {
        if (_plan is not { } plan || _loaded is not { } point)
        {
            return;
        }

        _script = PointComparePlanner.BuildDml(plan, _db.Dialect, _includeConflicts, PointCompareSql.Quote);
        var header = $"기준 {PointName} {point.BasePoint?.ToString(CultureInfo.InvariantCulture)}"
                     + (point.BaseAt is { } at ? " (" + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ")" : string.Empty)
                     + (point.GroupId is { } g ? " · 커밋 묶음 #" + g.ToString(CultureInfo.InvariantCulture) : string.Empty);
        DmlText = _script.Statements.Count == 0 && _script.Skipped.Count == 0 ? string.Empty : PointComparePlanner.Format(_script, header);
        if (_script.Statements.Count == 0 && ErrorText.Length == 0)
        {
            StatusText = "되돌릴 차이가 없습니다.";
        }

        ShowRows(); // 「충돌 포함·제외」 글을 맞춘다
        OnPropertyChanged(nameof(CanApply));
    }

    private void RefreshTables()
    {
        var tables = _selectedPoint is { Tables.Count: > 0 } p ? p.Tables : _selectedTable is { } t ? [t] : [];
        Tables.Clear();
        foreach (var item in tables)
        {
            Tables.Add(item);
        }

        if (_selectedTable is null || !Tables.Contains(_selectedTable))
        {
            SelectedTable = Tables.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasManyTables));
    }

    private static string PointCompareKeyText(CompareRow r)
        => string.Join(", ", r.Key.Select(v => v is null ? "NULL" : Convert.ToString(v, CultureInfo.InvariantCulture)));

    private static int Order(CompareKind k) => k switch
    {
        CompareKind.Revert => 0,
        CompareKind.Conflict => 1,
        CompareKind.Restore => 2,
        _ => 3,
    };

    public static string KindText(CompareKind k) => k switch
    {
        CompareKind.Revert => "되돌림",
        CompareKind.Restore => "되살림",
        CompareKind.Remove => "지움",
        CompareKind.Conflict => "충돌",
        _ => "같음",
    };

    private static string Name(DbObjectRef t) => string.IsNullOrEmpty(t.Owner) ? t.Name : t.Owner + "." + t.Name;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
