using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.TableEdit;

/// <summary>「변환해서 바꾸기…」 창이 쓰는 바깥 것(시험은 가짜를 넣는다).</summary>
public sealed record ColumnConversionServices
{
    public IExecutionService? Exec { get; init; }

    public IExecutionPolicy? Policy { get; init; }

    public IExecutionInteraction? Interaction { get; init; }

    public ITableEditChecks? Checks { get; init; }

    public IColumnConversionChecks? Conversions { get; init; }

    public IChangeBundleService? Bundles { get; init; }

    /// <summary>새 SQL 탭에 글 넣기(글, 접속 프로필). 연 것이면 true.</summary>
    public Func<string, Guid?, bool>? OpenEditor { get; init; }

    /// <summary>저장 파일 고르기(제안 이름). null = 취소.</summary>
    public Func<string, CancellationToken, Task<string?>>? PickFile { get; init; }

    /// <summary>예/아니오 묻기(⚠ 확인·미커밋 경고).</summary>
    public Func<string, CancellationToken, Task<bool>>? Confirm { get; init; }

    /// <summary>오늘 날짜(스크립트 머리).</summary>
    public Func<DateOnly>? Today { get; init; }

    /// <summary>검사 질의 하나의 시간 제한(기본 10초).</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>변환 단계 한 줄(설계 18.9 단계 목록).</summary>
/// <param name="Number">단계 번호(1~6)</param>
/// <param name="Text">단계 문장 요약</param>
/// <param name="Note">⚠ 표시(DML·N행·되돌릴 수 없음 …). 없으면 빈 글</param>
public sealed record ConversionStepRow(int Number, string Text, string Note)
{
    /// <summary>끝난 단계(중간 실패 뒤 표시).</summary>
    public bool IsDone { get; init; }

    public string Mark => IsDone ? "✔" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// 열 형식 변경 자동 변환 창(설계 18.9, 시안 ⑧): 데이터가 있어 형식을 바꿀 수 없는 열(검사 ✖ <see cref="CheckCode.IncompatibleTypeChange"/>)을
/// 「열 바꿔치기」(문장 1~6단계, 실행·묶음·스크립트) 또는 「테이블 다시 만들기」(검토용 스크립트만)로 바꾼다. 변환식을 고치면 단계·검사를 다시 만든다.
/// 검사(<see cref="ColumnConversionCheckRunner"/>)에 ✖ 가 있으면 실행·묶음 넣기를 막는다(1건이라도 변환 안 되는 행이면 ✖, TE-49). 스크립트 저장은 된다(TE-19).
/// 실행은 테이블 편집기와 같은 길(<see cref="TableEditRunner.ExecuteStatementsAsync"/>: 운영 확인 한 번·문장별), 중간에 실패하면 끝난 단계를 표시하고
/// 「남은 단계 스크립트」(<see cref="ColumnConversionPlan.RemainingScript"/>)를 SQL 편집기로 연다.
/// </summary>
public sealed partial class ColumnConversionViewModel : ObservableObject
{
    private readonly ISession _session;
    private readonly TableEditSession _edit;
    private readonly TableInfo _table;
    private readonly ColumnConversionServices _services;
    private CancellationTokenSource? _checkCts;
    private IReadOnlyList<DbObjectRef> _dependents = [];

    public ColumnConversionViewModel(ISession session, TableEditSession edit, string rowKey, TableInfo table, ColumnConversionServices services)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(services);
        _session = session;
        _edit = edit;
        _table = table;
        _services = services;
        RowKey = rowKey;
        Source = edit.Original.Column(rowKey) ?? throw new ArgumentException("원본에 없는 열이다(새 열은 바꿔치기할 것이 없다).", nameof(rowKey));
        Target = edit.Draft.Column(rowKey) ?? throw new ArgumentException("편집 모델에 없는 열이다.", nameof(rowKey));
        _expression = ColumnConversionDefaults.Expression(Source, Target, edit.Dialect.IsReservedWord);
        Rebuild();
    }

    public string RowKey { get; }

    /// <summary>지금 열(원본).</summary>
    public ColumnDraft Source { get; }

    /// <summary>새 정의(편집 모델의 그 열).</summary>
    public ColumnDraft Target { get; }

    /// <summary>창 머리: 「SAMPLE.ORDERS.ORDER_NO VARCHAR2(20) → NUMBER(12)」.</summary>
    public string Heading => $"{_edit.Original.Owner}.{_edit.Original.Name}.{Source.Name} {TableEditCellText.TypeLabel(Source)} → {TableEditCellText.TypeLabel(ColumnConversionBuilder.ResolveTarget(Source, Target))}";

    public string EnvironmentText => _session.Profile.Environment switch
    {
        ConnectionEnvironment.Production => TableEditText.T("운영", "Production"),
        ConnectionEnvironment.Test => TableEditText.T("테스트", "Test"),
        _ => TableEditText.T("개발", "Development"),
    };

    public bool IsProduction => _session.Profile.Environment == ConnectionEnvironment.Production;

    /// <summary>방법: 참 = 열 바꿔치기(빠름, 열이 맨 뒤로), 거짓 = 테이블 다시 만들기(열 순서 유지, 스크립트만).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRebuild), nameof(Method), nameof(CanRun), nameof(CanAddToBundle))]
    private bool _isSwap = true;

    public bool IsRebuild
    {
        get => !IsSwap;
        set => IsSwap = !value;
    }

    public ColumnConversionMethod Method => IsSwap ? ColumnConversionMethod.ColumnSwap : ColumnConversionMethod.Rebuild;

    /// <summary>변환식(옛 열 이름을 쓴 SQL 식). 기본 = <see cref="ColumnConversionDefaults.Expression"/>.</summary>
    [ObservableProperty]
    private string _expression;

    /// <summary>바꿔치기 단계 목록(1~6).</summary>
    [ObservableProperty]
    private IReadOnlyList<ConversionStepRow> _steps = [];

    /// <summary>바꿔치기 계획(문장). 만들지 못하면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanAddToBundle), nameof(CanSaveScript))]
    private ColumnConversionPlan? _plan;

    /// <summary>다시 만들기 스크립트(검토용, 실행하지 않음).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveScript))]
    private string _rebuildScript = string.Empty;

    /// <summary>검사 줄(✖ → ⚠ → ✔). 검사 전이면 비어 있다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlocked), nameof(CanRun), nameof(CanAddToBundle), nameof(HasChecked))]
    private IReadOnlyList<FindingRow> _findings = [];

    private IReadOnlyList<CheckFinding> _checkFindings = [];

    /// <summary>2단계 UPDATE 가 바꿀 행 수(통계 또는 COUNT). 모르면 null.</summary>
    [ObservableProperty]
    private long? _rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanAddToBundle), nameof(CanSaveScript))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>중간 실패 뒤 남은 단계 스크립트. 없으면 빈 글.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemaining))]
    private string _remainingScript = string.Empty;

    /// <summary>실행이 끝까지 성공했다(창을 닫고 상세를 다시 읽는다).</summary>
    [ObservableProperty]
    private bool _succeeded;

    /// <summary>문장이 하나라도 반영됐다(실패해도 서버 구조가 바뀌었다 — 닫을 때 다시 읽는다).</summary>
    public bool Applied { get; private set; }

    public bool HasRemaining => RemainingScript.Length > 0;

    public bool HasChecked => Findings.Count > 0;

    /// <summary>검사에 ✖ 가 있다(실행·묶음 넣기 막음, TE-49).</summary>
    public bool IsBlocked => _checkFindings.Any(f => f.Severity == CheckSeverity.Block);

    /// <summary>「실행」: 바꿔치기, 검사를 마쳤고 ✖ 없음, 읽기 전용 아님, 실행 중 아님, 이미 끝나지 않음.</summary>
    public bool CanRun => IsSwap && Plan is not null && HasChecked && !IsBlocked && !IsBusy && !Succeeded && !Applied && !_session.Profile.ReadOnly;

    /// <summary>「묶음에 넣기」: 바꿔치기 문장(변환 DML 포함, TE-44)을 묶음에.</summary>
    public bool CanAddToBundle => IsSwap && Plan is not null && HasChecked && !IsBlocked && !IsBusy && _services.Bundles is not null;

    /// <summary>「스크립트 저장…」: ✖ 가 있어도 된다(TE-19).</summary>
    public bool CanSaveScript => !IsBusy && (IsSwap ? Plan is not null : RebuildScript.Length > 0);

    partial void OnExpressionChanged(string value) => Rebuild();

    partial void OnIsSwapChanged(bool value) => Rebuild();

    /// <summary>단계·스크립트를 다시 만들고 지난 검사 줄을 지운다(변환식·방법이 바뀜).</summary>
    private void Rebuild()
    {
        _checkCts?.Cancel();
        _checkCts = null; // 앞 검사의 결과가 새 계획에 붙지 않게
        IsBusy = false;
        _checkFindings = [];
        Findings = [];
        var request = Request();
        try
        {
            Plan = BuildPlan(request);
            StepRowsFrom(Plan, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Plan = null;
            Steps = [];
            StatusText = ex.Message;
        }

        try
        {
            var enterprise = _session.Server.Edition?.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) == true;
            var hasPk = _edit.Original.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey && c.IsEnabled);
            RebuildScript = ColumnConversionBuilder.BuildRebuild(request, _table.Columns, enterprise, hasPk, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            RebuildScript = string.Empty;
            if (!IsSwap)
            {
                StatusText = ex.Message;
            }
        }
    }

    private ColumnConversionRequest Request() => new(_edit.Original, RowKey, Target, Expression ?? string.Empty) { Dependents = _dependents };

    /// <summary>
    /// 바꿔치기 계획. 2단계 UPDATE 는 WHERE 없는 DML 이라 정책의 「WHERE 없는 DML 확인」(SAFE-03)이 걸리게 표시한다 — 개발 접속에서도 확인 없이 나가지 않게.
    /// </summary>
    private ColumnConversionPlan BuildPlan(ColumnConversionRequest request)
    {
        var plan = ColumnConversionBuilder.BuildSwap(request, _edit.Dialect, _edit.Context.Server);
        return plan with
        {
            Steps = [.. plan.Steps.Select(step => step with
            {
                Statements = [.. step.Statements.Select(s => s.Kind == StatementKind.Dml ? s with { IsUnfilteredDml = true } : s)],
            })],
        };
    }

    private void StepRowsFrom(ColumnConversionPlan plan, int applied)
    {
        var done = applied > 0 ? plan.CompletedSteps(applied) : 0;
        Steps = [.. plan.Steps.Select((step, i) => new ConversionStepRow(step.Number, Summary(step), Note(step)) { IsDone = i < done })];
    }

    private static string Summary(ColumnConversionStep step)
    {
        string First(int i) => i < step.Statements.Count ? FirstLine(step.Statements[i].Text) : string.Empty;
        return step.Kind switch
        {
            ColumnConversionStepKind.RestoreDefinition => TableEditText.T($"기본값·NOT NULL·주석·제약/인덱스 다시 만들기({step.Statements.Count}문장)", $"Restore default, NOT NULL, comment, constraints/indexes ({step.Statements.Count} statement(s))"),
            ColumnConversionStepKind.Recompile => TableEditText.T($"이 테이블을 쓰는 뷰·PL/SQL 다시 컴파일({step.Statements.Count}개)", $"Recompile dependent views and PL/SQL ({step.Statements.Count})"),
            ColumnConversionStepKind.DropOriginal => First(step.Statements.Count - 1),
            _ => First(0),
        };
    }

    private string Note(ColumnConversionStep step) => step.Kind switch
    {
        ColumnConversionStepKind.CopyValues => TableEditText.T(
            $"⚠ DML · {(Rows is { } n ? n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "행" : "행 수 모름")} · 커밋은 이 단계 끝에 한 번",
            $"⚠ DML · {(Rows is { } n2 ? n2.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " rows" : "row count unknown")} · one commit at the end of this step"),
        ColumnConversionStepKind.DropOriginal => TableEditText.T("⚠ 되돌릴 수 없음", "⚠ cannot be undone"),
        _ => string.Empty,
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..157] + "…" : line;
    }

    /// <summary>
    /// 검사(열 때·「다시 검사」): 의존 객체(6단계)를 읽고 <see cref="ColumnConversionCheckRunner"/> 를 돌린다. 검사 읽기가 없으면 ⚠ 「확인 못 함」 — 그래도 실행은 막는다
    /// (변환 안 되는 행을 모르면 3단계 DROP 뒤 되돌릴 수 없다).
    /// </summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        _checkCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _checkCts = cts;
        IsBusy = true;
        StatusText = TableEditText.T("검사하는 중…", "Checking…");
        try
        {
            IReadOnlyList<CheckFinding> findings;
            if (_services.Checks is not { } checks || _services.Conversions is not { } conversions)
            {
                findings = [new CheckFinding(CheckSeverity.Block, CheckCode.NotChecked, RowKey)
                {
                    Subject = Source.Name,
                    Detail = TableEditText.T("이 DBMS 용 변환 검사 읽기가 없어 실행할 수 없다(스크립트 저장은 됨)", "no conversion check reader for this DBMS — run is blocked (saving a script works)"),
                }];
            }
            else
            {
                try
                {
                    _dependents = await checks.DependentsAsync(_session, new DbObjectRef(_edit.Original.Owner, _edit.Original.Name, "TABLE"), cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _dependents = []; // 다시 컴파일 단계만 빠진다
                }

                Plan = BuildPlan(Request());
                var options = new TableEditCheckOptions { Environment = _session.Profile.Environment, Server = _session.Server, Timeout = _services.CheckTimeout };
                var result = await ColumnConversionCheckRunner.RunAsync(Request(), Method, _edit.Dialect, conversions, checks, _session, options, cts.Token);
                Rows = result.Rows;
                findings = result.Findings;
            }

            if (!ReferenceEquals(_checkCts, cts) || cts.IsCancellationRequested)
            {
                return; // 그새 변환식·방법이 바뀌었다
            }

            _checkFindings = [.. findings.OrderBy(f => f.Severity switch { CheckSeverity.Block => 0, CheckSeverity.Warning => 1, _ => 2 })];
            Findings = [.. _checkFindings.Select(f => new FindingRow(CheckFindingText.Format(f, _edit.Dialect.PurgesOnDrop), f.ViewRowsSql))];
            if (Plan is { } plan)
            {
                StepRowsFrom(plan, 0);
            }

            StatusText = IsBlocked
                ? TableEditText.T("✖ 가 있어 실행·묶음 넣기를 막습니다(스크립트 저장은 됩니다)", "Blocked: fix the ✖ lines first (saving a script still works)")
                : TableEditText.T("검사를 마쳤습니다", "Checked");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (ReferenceEquals(_checkCts, cts))
            {
                StatusText = ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_checkCts, cts))
            {
                _checkCts = null;
                IsBusy = false;
            }

            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(CanAddToBundle));
        }
    }

    /// <summary>
    /// 「실행」(바꿔치기): 문장을 테이블 편집기와 같은 길로 보낸다. 중간 실패면 끝난 단계를 ✔ 로 두고 남은 단계 스크립트를 만든다. 결과 한 줄을 돌려준다.
    /// </summary>
    public async Task<TableEditRunResult?> RunAsync(CancellationToken ct = default)
    {
        if (!CanRun || Plan is not { } plan)
        {
            return null;
        }

        var confirm = _services.Confirm;
        var runner = new TableEditRunner(_services.Exec)
        {
            RunSource = Oranda.Core.History.HistorySource.SchemaBrowser, // 자동 넣기 밖(DDL 만 넣으면 UPDATE 가 빠진다) — 끝난 뒤 계획 전체를 직접 넣는다
            Policy = _services.Policy,
            Interaction = _services.Interaction,
            ConfirmWarnings = confirm is null ? null : (findings, token) => confirm(
                CheckFindingText.FormatAll(findings, _edit.Dialect.PurgesOnDrop) + Environment.NewLine + Environment.NewLine + TableEditText.T("그래도 변환할까요?", "Convert anyway?"), token),
            ConfirmPendingCommit = confirm is null ? null : token => confirm(TableEditText.T(
                "이 세션에 커밋하지 않은 변경이 있습니다. 변환(DDL·COMMIT)이 그 변경도 커밋합니다. 계속할까요?",
                "This session has uncommitted changes. The conversion (DDL and COMMIT) commits them too. Continue?"), token),
        };
        IsBusy = true;
        try
        {
            var result = await runner.ExecuteStatementsAsync(_session, plan.Statements, _checkFindings, () => ReferenceEquals(Plan, plan), ct);
            Applied = result.SucceededCount > 0;
            if (result.Outcome == TableEditRunOutcome.Succeeded)
            {
                Succeeded = true;
                StepRowsFrom(plan, plan.Statements.Count);
                RemainingScript = string.Empty;
                StatusText = TableEditText.T("변환을 마쳤습니다", "The conversion finished") + await AddRunToCurrentBundleAsync(plan, ct);
            }
            else if (Applied)
            {
                StepRowsFrom(plan, result.SucceededCount);
                RemainingScript = plan.RemainingScript(RemainingStart(plan, result.SucceededCount), Environment.NewLine);
                StatusText = TableEditText.T($"{plan.CompletedSteps(result.SucceededCount)}단계까지 끝난 뒤 멈췄습니다. 「남은 단계 스크립트」로 이어 하세요 — ", $"Stopped after step {plan.CompletedSteps(result.SucceededCount)}. Continue with the remaining script — ")
                             + result.Message;
            }
            else
            {
                StatusText = result.Message;
            }

            OnPropertyChanged(nameof(CanRun));
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 남은 단계 스크립트의 시작 문장. 3단계 앞 확인 블록(바뀐 행을 보면 멈춤)이 실패했으면 2단계(값 복사)부터 다시 해야 한다 — 그대로 3단계부터 주면 같은 이유로 또 멈춘다.
    /// </summary>
    internal static int RemainingStart(ColumnConversionPlan plan, int applied)
    {
        if (applied >= plan.Statements.Count || plan.StepOf(applied).Kind != ColumnConversionStepKind.DropOriginal)
        {
            return applied;
        }

        var start = 0;
        foreach (var step in plan.Steps)
        {
            if (step.Kind == ColumnConversionStepKind.DropOriginal)
            {
                break;
            }

            start += step.Statements.Count;
        }

        var guard = start; // 3단계 첫 문장 = 확인 블록
        if (applied != guard)
        {
            return applied;
        }

        var copy = 0;
        foreach (var step in plan.Steps)
        {
            if (step.Kind == ColumnConversionStepKind.CopyValues)
            {
                return copy;
            }

            copy += step.Statements.Count;
        }

        return applied;
    }

    /// <summary>
    /// 실행이 끝난 변환을 「지금 묶음」에 계획 전체로 넣는다(변환 DML 포함, TE-44). 지금 묶음이 없으면 하지 않는다. 상태 줄에 덧붙일 글.
    /// </summary>
    private async Task<string> AddRunToCurrentBundleAsync(ColumnConversionPlan plan, CancellationToken ct)
    {
        if (_services.Bundles is not { } bundles)
        {
            return string.Empty;
        }

        try
        {
            if (await bundles.GetCurrentAsync(ct) is null)
            {
                return string.Empty;
            }

            var result = await bundles.AddAsync(bundles.OriginOf(_session.Id) ?? ChangeBundleOrigin.From(_session), Drafts(plan), ChangeBundleItemSource.TableEditor, ct);
            return " · " + Oranda.App.Features.ObjectDetail.ObjectDetailViewModel.BundleResultText(result, plan.Statements.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return " · " + TableEditText.T("묶음에 넣지 못했다: ", "could not add to the bundle: ") + ex.Message;
        }
    }

    private IReadOnlyList<ChangeBundleDraft> Drafts(ColumnConversionPlan plan) =>
        [.. plan.Statements.Select(s => new ChangeBundleDraft(s.Text, s.Kind is StatementKind.Dml or StatementKind.TransactionControl ? TableEditText.T("변환 DML", "conversion DML") : null, _edit.Original.Owner))];

    /// <summary>「남은 단계 스크립트」를 SQL 편집기로(실행하지 않음).</summary>
    public bool OpenRemainingInEditor() =>
        RemainingScript.Length > 0 && _services.OpenEditor?.Invoke(RemainingScript, _session.Profile.Id) == true;

    /// <summary>「테이블 다시 만들기」 스크립트를 SQL 편집기로(검토 뒤 사용자가 실행).</summary>
    public bool OpenRebuildInEditor() =>
        RebuildScript.Length > 0 && _services.OpenEditor?.Invoke(RebuildScript, _session.Profile.Id) == true;

    /// <summary>「그 행 보기」.</summary>
    public bool OpenViewRows(string sql) =>
        !string.IsNullOrWhiteSpace(sql) && _services.OpenEditor?.Invoke(sql.TrimEnd() + Environment.NewLine, _session.Profile.Id) == true;

    /// <summary>
    /// 「스크립트 저장…」: 바꿔치기는 의뢰용 형식(변환 DML 허용·머리 <c>-- DML 포함</c>, TE-44), 다시 만들기는 스크립트 그대로. 저장한 경로.
    /// </summary>
    public async Task<string?> SaveScriptAsync(CancellationToken ct = default)
    {
        if (!CanSaveScript || _services.PickFile is not { } pick)
        {
            return null;
        }

        string text;
        if (IsSwap && Plan is { } plan)
        {
            var header = new RequestScriptHeader($"{_edit.Original.Owner}.{_edit.Original.Name}.{Source.Name}", _services.Today?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now), _session.Profile.Dbms)
            {
                AllowsConversionDml = true,
            };
            text = RequestScriptWriter.Write(header, plan.Statements, Environment.NewLine);
        }
        else
        {
            text = RebuildScript;
        }

        var path = await pick($"{_edit.Original.Name}_{Source.Name}_convert.sql", ct);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            await File.WriteAllTextAsync(path, text, RequestScriptWriter.Encoding, ct);
            StatusText = TableEditText.T("스크립트를 저장했다: ", "Saved the script: ") + path;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = TableEditText.T("스크립트를 저장하지 못했다: ", "Could not save the script: ") + ex.Message;
            return null;
        }
    }

    /// <summary>「묶음에 넣기」(바꿔치기 문장, 변환 DML 포함 — 묶음 항목에 「변환 DML」 ⚠, TE-44).</summary>
    public async Task<ChangeBundleAddResult?> AddToBundleAsync(CancellationToken ct = default)
    {
        if (!CanAddToBundle || Plan is not { } plan || _services.Bundles is not { } bundles)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var origin = bundles.OriginOf(_session.Id) ?? ChangeBundleOrigin.From(_session);
            var drafts = Drafts(plan);
            var result = await bundles.AddAsync(origin, drafts, ChangeBundleItemSource.TableEditor, ct);
            StatusText = Oranda.App.Features.ObjectDetail.ObjectDetailViewModel.BundleResultText(result, drafts.Count);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
