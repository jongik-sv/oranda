using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 객체 상세의 「데이터」 탭(SCH-03, SQL Developer 테이블 편집기의 Data 탭·DataGrip 의 테이블 데이터 보기).
/// 표·뷰·머티리얼라이즈드 뷰의 행을 결과 영역(<see cref="SqlResultPaneViewModel"/>)으로 보인다 — 필요한 만큼 가져오기·거르기 줄·
/// 선택 집계·전체 행 수 세기·모두 가져오기·복사가 편집기 결과와 같다. 상세를 열 때는 읽지 않고 이 탭을 처음 고를 때 읽는다.
/// 질의·거르기·세기는 상세 문서의 세션으로 정책 관문을 거쳐 실행한다(<see cref="ObjectDataRunner"/>).
/// 표는 <c>ROWID</c> 를 함께 읽어, 「편집」을 켜면 지금 결과 그대로 편집 모드(<see cref="GridEditSession"/>)를 연다
/// (읽기 전용 접속·키 없는 결과는 편집 세션의 판단을 따른다).
/// </summary>
public sealed partial class ObjectDataViewModel : ObservableObject
{
    /// <summary>상세 구역 목록에 보이는 이름(「열」 바로 뒤).</summary>
    public const string SectionTitle = "데이터";

    /// <summary>표 질의의 별칭(<c>SELECT t.ROWID, t.* FROM … t</c>).</summary>
    private const string TableAlias = "t";

    private readonly Func<ISession, ObjectDataRunner?> _runner;
    private readonly Func<IRowEditor?> _rowEditor;
    private readonly Func<IMetadataProvider?> _metadata;
    private ISession? _session;
    private DbObjectRef? _target;
    private SqlStatement? _query;
    private int _generation;
    private Action? _cancel;
    private Task? _pending;

    /// <param name="runner">세션 → 실행 쪽(없으면 null: 실행 엔진이 연결되지 않았다)</param>
    /// <param name="rowEditor">편집 반영에 쓰는 행 편집기(편집을 켤 때 해석한다)</param>
    /// <param name="metadata">편집 대상 표의 키를 읽는 메타데이터 제공자(편집을 켤 때 해석한다)</param>
    public ObjectDataViewModel(Func<ISession, ObjectDataRunner?> runner, Func<IRowEditor?> rowEditor, Func<IMetadataProvider?> metadata)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(rowEditor);
        ArgumentNullException.ThrowIfNull(metadata);
        _runner = runner;
        _rowEditor = rowEditor;
        _metadata = metadata;

        // 행 수 세기·거르기·「편집 모드로 다시 실행」도 편집기의 활성 세션이 아니라 이 탭의 세션으로 돌린다.
        Pane = new SqlResultPaneViewModel
        {
            Runner = new ResultPaneRunner
            {
                CountRows = (stmt, ct) => RequireRunner().CountAsync(new ResultFilterRequest(stmt, string.Empty, string.Empty), ct),
                Filter = (request, ct) => RequireRunner().FilterAsync(request, ct),
                CountFiltered = (request, ct) => RequireRunner().CountAsync(request, ct),
                RerunForEdit = (_, ct) => SetEditingAsync(true, ct),
            },
        };

        // 거르면 결과 영역이 편집 상태를 버린다(감싼 질의는 ROWID 로 행을 찾을 수 없다). 새 결과에서도 ROWID 열은 감춘다.
        Pane.FilterApplied += (_, _) =>
        {
            IsEditing = false;
            HideRowId();
        };
        Pane.PropertyChanged += OnPaneChanged;
    }

    /// <summary>데이터를 보이는 결과 영역(편집기 결과와 같은 화면).</summary>
    public SqlResultPaneViewModel Pane { get; }

    /// <summary>보이는 객체. 상세를 열기 전이면 null.</summary>
    public DbObjectRef? Target => _target;

    /// <summary>데이터 질의(처음 읽을 때 만든다). 결과 영역의 <see cref="SqlResultPaneViewModel.Statement"/> 와 같은 개체다.</summary>
    public SqlStatement? Query => _query;

    /// <summary>표라서 편집할 수 있다(뷰·머티리얼라이즈드 뷰는 읽기 전용).</summary>
    public bool IsTable => _target is { ObjectType: "TABLE" };

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>편집 모드가 켜져 고칠 수 있다(툴바 「편집」 토글).</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>읽기 실패·편집 거절 같은 이 탭의 안내(행 수·거르기 안내는 결과 영역의 상태 줄에).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>「편집」 토글을 쓸 수 있다: 표의 결과를 읽었고 거른 결과가 아니다.</summary>
    public bool CanToggleEdit => IsTable && IsLoaded && !IsLoading && !Pane.IsFiltered && !Pane.IsFiltering
                                 && Pane.Model is { HasResult: true, Error: null };

    public bool CanRefresh => _target is not null && !IsLoading && !Pane.IsFiltering;

    public bool CanCountRows => !IsLoading && Pane.CanCountRows && !Pane.IsCountingRows;

    /// <summary>툴바의 행 수 표시: 센 뒤 「전체 N행」, 세는 중이면 그 안내, 세지 않았으면 빈 글.</summary>
    public string RowCountText => Pane.CountedRowCount is { } total
        ? "전체 " + total.ToString("N0", CultureInfo.CurrentCulture) + "행"
        : Pane.IsCountingRows ? "세는 중…" : string.Empty;

    /// <summary>데이터 탭을 두는 객체 유형: 표·뷰·머티리얼라이즈드 뷰.</summary>
    public static bool Supports(DbObjectRef? target) => target is { ObjectType: "TABLE" or "VIEW" or "MATERIALIZED VIEW" };

    /// <summary>
    /// 데이터 질의. 표는 행을 고칠 수 있게 ROWID 를 앞에 붙이고(<c>SELECT t.ROWID, t.* FROM OWNER.NAME t</c>),
    /// 뷰·머티리얼라이즈드 뷰는 <c>SELECT * FROM OWNER.NAME</c>(읽기 전용). 이름은 그리드 편집 DML 과 같은 규칙으로
    /// 필요할 때만 큰따옴표로 감싼다(소문자·특수 문자·예약어).
    /// </summary>
    public static SqlStatement BuildQuery(DbObjectRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = RowDmlBuilder.QualifiedName(target);
        var text = target.ObjectType is "TABLE"
            ? $"SELECT {TableAlias}.ROWID, {TableAlias}.* FROM {name} {TableAlias}"
            : $"SELECT * FROM {name}";
        return new SqlStatement(text, default, StatementKind.Query) { Verb = "SELECT" };
    }

    /// <summary>상세를 (다시) 열 때: 지난 결과·거르기 글을 지우고 대상만 바꾼다. 읽지는 않는다(탭을 고를 때 읽는다).</summary>
    public void Reset(ISession session, DbObjectRef target)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        Close();
        _session = session;
        _target = target;
        _query = null;
        Pane.WhereText = string.Empty;
        Pane.OrderByText = string.Empty;
        Pane.IsFilterBarVisible = false;
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(IsTable));
        RaiseDerived();
    }

    /// <summary>결과를 닫는다(문서를 닫을 때·다시 열 때). 읽는 중이면 취소하고 늦게 온 결과는 버린다.</summary>
    public void Close()
    {
        _generation++;
        _cancel?.Invoke();
        _cancel = null;
        ForgetActiveEdit();
        Pane.Clear();
        Pane.Statement = null;
        IsLoading = false;
        IsLoaded = false;
        IsEditing = false;
        StatusText = string.Empty;
    }

    /// <summary>탭을 고를 때: 아직 읽지 않았으면 읽는다. 읽는 중이면 그 읽기를 기다린다.</summary>
    public Task EnsureLoadedAsync() =>
        IsLoaded ? Task.CompletedTask
        : IsLoading && _pending is { } pending ? pending
        : StartLoad(edit: false, CancellationToken.None);

    /// <summary>
    /// 새로 고침: 같은 질의를 다시 읽는다(편집 모드였으면 다시 편집 모드로). 거른 결과면 같은 조건으로 다시 거른다.
    /// 반영하지 않은 편집이 있으면 버리지 않고 안내만 한다.
    /// </summary>
    public Task RefreshAsync()
    {
        if (Pane.IsEditDirty)
        {
            StatusText = "반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 새로 고치세요.";
            return Task.CompletedTask;
        }

        return Pane.IsFiltered ? Pane.ApplyFilterAsync() : StartLoad(IsEditing, CancellationToken.None);
    }

    /// <summary>읽는 중인 데이터 질의를 취소한다(서버 중단은 실행 엔진 몫).</summary>
    public void CancelLoad() => _cancel?.Invoke();

    /// <summary>툴바 「행 수 세기」: 결과 영역의 「전체 행 수 세기」와 같다(거른 결과면 거른 행 수).</summary>
    public Task CountRowsAsync() => Pane.CountAllRowsAsync();

    /// <summary>
    /// 「편집」 토글. 켜면 지금 결과에 편집 모드를 연다(다시 읽지 않는다). 고칠 수 없으면(읽기 전용 접속 등) 이유를 안내 막대에 보이고 꺼진 채로 둔다.
    /// 끄면 편집 상태를 풀려고 읽기 전용으로 다시 읽는다 — 반영하지 않은 편집이 있으면 끄지 않는다.
    /// </summary>
    public async Task SetEditingAsync(bool on, CancellationToken ct = default)
    {
        if (on != IsEditing)
        {
            if (on)
            {
                Pane.HideEditHint();
                await OpenEditAsync(ct).ConfigureAwait(true);
            }
            else if (Pane.IsEditDirty)
            {
                StatusText = "반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 편집을 끄세요.";
            }
            else
            {
                await StartLoad(edit: false, ct).ConfigureAwait(true);
            }
        }

        OnPropertyChanged(nameof(IsEditing)); // 토글 단추를 지금 상태로 맞춘다
    }

    partial void OnIsLoadedChanged(bool value) => RaiseDerived();

    partial void OnIsLoadingChanged(bool value) => RaiseDerived();

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e) => RaiseDerived();

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanToggleEdit));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCountRows));
        OnPropertyChanged(nameof(RowCountText));
    }

    private Task StartLoad(bool edit, CancellationToken ct) => _pending = LoadAsync(edit, ct);

    private ObjectDataRunner RequireRunner() =>
        _session is { } s && _runner(s) is { } runner
            ? runner
            : throw new InvalidOperationException("실행 엔진이 연결되지 않았습니다.");

    /// <summary>
    /// 데이터 질의를 실행해 결과 영역에 보인다. 실패·취소는 이 탭의 안내로 보이고 지금 결과는 그대로 둔다(밖으로 던지지 않는다).
    /// 그새 다시 읽기·닫기가 있었으면 늦게 온 결과는 닫고 버린다. <paramref name="edit"/> 면 읽은 뒤 편집 모드를 연다.
    /// </summary>
    private async Task LoadAsync(bool edit, CancellationToken ct)
    {
        if (_session is not { } session || _target is not { } target)
        {
            return;
        }

        if (_runner(session) is not { } runner)
        {
            StatusText = "데이터를 읽을 수 없습니다(실행 엔진이 연결되지 않았습니다).";
            return;
        }

        var generation = ++_generation;
        _cancel?.Invoke();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancel = cts.Cancel;
        var query = _query ??= BuildQuery(target);
        IsLoading = true;
        StatusText = "데이터를 읽는 중…";

        IResultSet? result = null;
        string? error = null;
        try
        {
            result = await runner.LoadAsync(query, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            error = "데이터 읽기를 취소했습니다.";
        }
        catch (Exception e)
        {
            error = "데이터를 읽지 못했습니다: " + e.Message.ReplaceLineEndings(" ");
        }

        if (generation != _generation)
        {
            if (result is not null)
            {
                await result.DisposeAsync().ConfigureAwait(true);
            }

            return;
        }

        _cancel = null;
        IsLoading = false;
        if (result is null)
        {
            StatusText = error ?? string.Empty;
            return;
        }

        ForgetActiveEdit();
        Pane.Statement = query;
        Pane.SetResult(result);
        HideRowId();
        IsEditing = false;
        IsLoaded = true;
        StatusText = string.Empty;
        OnPropertyChanged(nameof(Query));
        if (edit)
        {
            await OpenEditAsync(ct).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 지금 결과에 편집 모드를 연다(F9 와 같은 편집 세션·DML 미리보기·반영/커밋). 대상 표는 이 객체, 행은 ROWID 로 찾는다.
    /// 반영한 뒤에는 편집기가 아니라 이 탭에서 같은 질의를 다시 읽는다.
    /// </summary>
    private async Task OpenEditAsync(CancellationToken ct)
    {
        if (!IsTable)
        {
            StatusText = "뷰의 데이터는 여기서 편집할 수 없습니다.";
            return;
        }

        if (_session is not { } session || _query is not { } query || Pane.IsFiltered
            || Pane.Model is not { HasResult: true, Error: null } model)
        {
            return;
        }

        if (_rowEditor() is not { } editor || _metadata() is not { } metadata)
        {
            StatusText = "편집할 수 없습니다(행 편집기가 연결되지 않았습니다).";
            return;
        }

        var generation = _generation;
        GridEditSession es;
        try
        {
            es = await GridEditSession.OpenAsync(
                editor, metadata, session, query, model.Columns,
                (row, col) => model.TryGetValue(row, col, out var v) ? v : null,
                () => model.LoadedRowCount, _target, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            StatusText = "편집 모드를 열지 못했습니다: " + e.Message.ReplaceLineEndings(" ");
            return;
        }

        if (generation != _generation || !ReferenceEquals(Pane.Model, model))
        {
            return; // 그새 다시 읽었거나 닫았다
        }

        es.Rerun = (_, token) => StartLoad(edit: true, token);
        GridEditBinding.Attach(model, es); // 바꾼 값·새 행을 그리드에 그리고 셀 편집기가 세션을 찾는다
        Pane.SetEditSession(es); // 편집 툴바(고칠 수 있을 때)나 고칠 수 없는 이유(안내 막대)를 보인다
        GridEditCommands.Active = es;
        IsEditing = es.CanEdit;
        StatusText = es.CanEdit ? string.Empty : "편집할 수 없습니다: " + (es.Capability.Message ?? es.Capability.Reason.ToString());
    }

    /// <summary>
    /// 표 결과의 첫 열(편집이 행을 찾는 ROWID)은 감춘다(SQL Developer·DataGrip 처럼 데이터 열만 보인다). 값은 결과에 남아
    /// 편집 DML 의 조건에 쓰이고, 「모든 열 보이기」로 다시 볼 수 있다.
    /// </summary>
    private void HideRowId()
    {
        if (IsTable && Pane.Model is { HasResult: true } m && m.Columns[0].Kind == ColumnDataKind.RowId)
        {
            m.HideColumn(0);
        }
    }

    /// <summary>결과를 바꾸기 전에: 편집 명령이 버릴 편집 상태를 가리키지 않게 한다.</summary>
    private void ForgetActiveEdit()
    {
        if (GridEditBinding.Of(Pane.Model) is { } es && ReferenceEquals(GridEditCommands.Active, es))
        {
            GridEditCommands.Active = null;
        }
    }
}
