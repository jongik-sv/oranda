using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Execution;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.PlSql;

namespace Oranda.App.Features.PlsqlRun;

/// <summary>PL/SQL 실행 대화상자가 쓰는 서비스. 모두 쓸 때 해석한다(시작 경로에서 Oracle·Language 어셈블리를 깨우지 않기 위해).</summary>
public sealed class PlsqlRunDeps
{
    public required Func<ISession?> Session { get; init; }

    public required Func<IMetadataProvider?> Metadata { get; init; }

    public required Func<IExecutionService> Execution { get; init; }

    /// <summary>실행 상태·자동 커밋 표시. 없으면 공유 상태 없이 돈다.</summary>
    public ExecutionState? State { get; init; }

    public ExecutionLog? Log { get; init; }

    /// <summary>생성한 블록을 SQL 편집기 새 탭으로 보낸다.</summary>
    public Action<string>? SendToEditor { get; init; }
}

/// <summary>인자 한 줄. 입력 칸과 실행 뒤 OUT 값을 함께 가진다.</summary>
public sealed partial class PlsqlArgumentRow : ObservableObject
{
    public PlsqlArgumentRow(CallParameter parameter)
    {
        Parameter = parameter;
        Name = parameter.IsReturn ? "반환값" : parameter.Name;
    }

    public CallParameter Parameter { get; }

    public string Name { get; }

    public string DataType => Parameter.DataType;

    public string DirectionText => Parameter.IsReturn ? "RETURN" : Parameter.Direction switch
    {
        ArgumentDirection.Out => "OUT",
        ArgumentDirection.InOut => "IN OUT",
        _ => "IN",
    };

    public bool HasDefault => Parameter.HasDefault && !Parameter.IsReturn;

    /// <summary>값을 입력할 수 있는 줄(OUT 전용·반환값·지원하지 않는 형식은 아님).</summary>
    public bool CanEdit => Parameter.IsSupported && Parameter.IsInput;

    public bool CanUseDefault => CanEdit && Parameter.Direction == ArgumentDirection.In && Parameter.HasDefault;

    public string Note => Parameter.UnsupportedReason ?? (Parameter.IsBoolean ? "TRUE / FALSE" : string.Empty);

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNull { get; set; }

    /// <summary>기본값이 있는 인자는 처음에 「기본값 사용」 으로 둔다.</summary>
    [ObservableProperty]
    public partial bool UseDefault { get; set; }

    /// <summary>실행 뒤 OUT 값 글. 실행 전에는 비어 있다.</summary>
    [ObservableProperty]
    public partial string OutText { get; set; } = string.Empty;

    public CallInput ToInput() => new(Parameter.Name, Text, IsNull, UseDefault);
}

/// <summary>호출 대상 후보(패키지 멤버 또는 오버로드) 한 개.</summary>
public sealed record PlsqlCandidate(ProcedureInfo Procedure, CallTarget Target, string Display);

/// <summary>
/// PL/SQL 실행 대화상자의 상태와 동작(PRD PLSQL-03). 이 클래스는 화면 형식을 모른다(시험이 직접 돌린다).
/// 열기 → 메타데이터에서 인자 읽기 → 입력 → 블록 미리보기 → 실행(정책 관문·바인드는 <see cref="IExecutionService"/>) → OUT 값·커서·DBMS_OUTPUT 표시.
/// </summary>
public sealed partial class PlsqlRunViewModel : ObservableObject
{
    private readonly PlsqlRunDeps _deps;
    private CancellationTokenSource? _cts;
    private CallBlock? _block;

    public PlsqlRunViewModel(PlsqlRunDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _deps = deps;
        Results = new ResultTabsViewModel(deps.Session()?.Profile);
    }

    public DbObjectRef? Target { get; private set; }

    public ObservableCollection<PlsqlCandidate> Candidates { get; } = [];

    [ObservableProperty]
    public partial PlsqlCandidate? SelectedCandidate { get; set; }

    public ObservableCollection<PlsqlArgumentRow> Rows { get; } = [];

    /// <summary>OUT·반환값 REF CURSOR 결과를 담는 영역(결과 그리드, TSK-06-03).</summary>
    public ResultTabsViewModel Results { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = "PL/SQL 실행";

    [ObservableProperty]
    public partial string Preview { get; set; } = string.Empty;

    /// <summary>실행할 수 없는 이유(여러 줄). 비면 빈 글.</summary>
    [ObservableProperty]
    public partial string ProblemText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial string DbmsOutputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    public bool CanRun => !IsRunning && _block is { CanRun: true } && _deps.Session() is not null && _deps.State?.Busy != true;

    public bool HasMultipleCandidates => Candidates.Count > 1;

    public bool HasOutValues => Rows.Any(r => r.OutText.Length > 0);

    partial void OnSelectedCandidateChanged(PlsqlCandidate? value) => Rebuild(value);

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanRun));

    // ---- 열기 ------------------------------------------------------------------------------

    /// <summary>
    /// 대상의 프로시저·함수 목록을 읽어 채운다. 패키지는 멤버마다(오버로드 포함) 후보가 되고,
    /// <paramref name="member"/> 가 있으면 그 이름만 남긴다. 단독 프로시저·함수는 오버로드가 없다.
    /// </summary>
    public async Task OpenAsync(DbObjectRef target, string? member = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        Title = $"PL/SQL 실행 — {target.Owner}.{target.Name}" + (member is null ? string.Empty : "." + member);
        var session = _deps.Session();
        var meta = _deps.Metadata();
        if (session is null || meta is null)
        {
            FailOpen("접속이 없습니다. 접속한 뒤 실행하세요.", target);
            return;
        }

        IReadOnlyList<ProcedureInfo> procs;
        try
        {
            procs = await Task.Run(() => meta.GetProceduresAsync(session, target, ct), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            FailOpen("인자 정보를 읽지 못했습니다: " + e.Message, target);
            return;
        }

        bool isPackage = string.Equals(target.ObjectType, "PACKAGE", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(target.ObjectType, "PACKAGE BODY", StringComparison.OrdinalIgnoreCase);
        Candidates.Clear();
        foreach (var p in procs.Where(p => member is null || string.Equals(p.Name, member, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Overload))
        {
            var callTarget = isPackage ? new CallTarget(target.Owner, target.Name, p.Name) : new CallTarget(target.Owner, null, target.Name);
            Candidates.Add(new PlsqlCandidate(p, callTarget, Signature(p)));
        }

        OnPropertyChanged(nameof(HasMultipleCandidates));
        if (Candidates.Count == 0)
        {
            Rows.Clear();
            Preview = string.Empty;
            FailOpen(isPackage ? "실행할 수 있는 프로시저·함수가 없습니다." : "프로시저·함수 정보를 찾지 못했습니다.", target);
            return;
        }

        SelectedCandidate = Candidates[0];
        if (SelectedCandidate == Candidates[0] && Rows.Count == 0)
        {
            Rebuild(SelectedCandidate);
        }

        // 인자 줄이 하나도 없으면 빈 칸으로 보이므로 무엇을 하면 되는지 적는다.
        if (Rows.Count == 0)
        {
            SetStatus("인자가 없습니다. 「실행」 을 누르면 바로 호출합니다.", error: false);
        }
    }

    /// <summary>열기 실패를 창 상태 줄에 보이고 메시지 창·앱 로그에도 남긴다(창만 비어 보이고 흔적이 없던 결함, TSK-12-02).</summary>
    private void FailOpen(string text, DbObjectRef target)
    {
        SetStatus(text, error: true);
        _deps.Log?.AddMessage(MessageKind.Error, $"PL/SQL 실행 창 — {target.Owner}.{target.Name}: {text}");
        Trace.TraceWarning("PL/SQL 실행 창을 열지 못했다 ({0}.{1}, {2}): {3}", target.Owner, target.Name, target.ObjectType, text);
    }

    /// <summary>후보 표시 글: <c>NAME(A IN NUMBER, B OUT VARCHAR2) RETURN NUMBER</c>.</summary>
    public static string Signature(ProcedureInfo p)
    {
        var args = string.Join(", ", p.Arguments.OrderBy(a => a.Position).Select(a =>
            $"{a.Name} {(a.Direction == ArgumentDirection.InOut ? "IN OUT" : a.Direction.ToString().ToUpperInvariant())} {a.DataType}{(a.HasDefault ? " DEFAULT" : string.Empty)}"));
        var text = $"{p.Name}({args})";
        if (p.IsFunction)
        {
            text += " RETURN " + (p.ReturnType ?? "?");
        }

        return p.Overload is { } o && o > 0 ? $"{text}  [오버로드 {o.ToString(CultureInfo.InvariantCulture)}]" : text;
    }

    private void Rebuild(PlsqlCandidate? candidate)
    {
        foreach (var r in Rows)
        {
            r.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();
        Results.Clear();
        DbmsOutputText = string.Empty;
        if (candidate is null)
        {
            _block = null;
            Preview = string.Empty;
            return;
        }

        foreach (var p in CallBlockBuilder.Describe(candidate.Procedure))
        {
            var row = new PlsqlArgumentRow(p) { UseDefault = p.HasDefault && !p.IsReturn && p.Direction == ArgumentDirection.In };
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        Refresh();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlsqlArgumentRow.Text) or nameof(PlsqlArgumentRow.IsNull) or nameof(PlsqlArgumentRow.UseDefault))
        {
            Refresh();
        }
    }

    /// <summary>입력이 바뀔 때마다 블록·바인드·문제 목록을 다시 만든다.</summary>
    private void Refresh()
    {
        if (SelectedCandidate is not { } c)
        {
            return;
        }

        _block = CallBlockBuilder.Build(c.Target, c.Procedure, Rows.Where(r => r.CanEdit).Select(r => r.ToInput()));
        Preview = _block.Text;
        ProblemText = string.Join(Environment.NewLine, _block.Problems);
        OnPropertyChanged(nameof(CanRun));
    }

    // ---- 실행 ------------------------------------------------------------------------------

    /// <summary>블록을 실행하고 OUT 값·커서·DBMS_OUTPUT 을 화면 상태에 담는다. 끝나면 true(성공한 경우).</summary>
    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        var session = _deps.Session();
        if (session is null)
        {
            SetStatus("접속이 없습니다. 접속한 뒤 실행하세요.", error: true);
            return false;
        }

        if (!CanRun || _block is not { } block)
        {
            SetStatus(ProblemText.Length > 0 ? ProblemText : "지금은 실행할 수 없습니다.", error: true);
            return false;
        }

        ClearResults();
        IsRunning = true;
        _deps.State?.BeginRun();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        ExecutionResult? result = null;
        try
        {
            var exec = _deps.Execution();
            var options = new ExecutionOptions { Source = HistorySource.Editor };
            result = await Task.Run(() => exec.ExecuteAsync(session, block.Statement, block.Binds, options, cts.Token), CancellationToken.None)
                .ConfigureAwait(true);
            if (_deps.State is { AutoCommit: true, ReadOnly: false } && result.Status == ExecutionStatus.Succeeded && result.TransactionPending)
            {
                await Task.Run(() => session.CommitAsync(cts.Token), CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("취소되었습니다.", error: false);
            return false;
        }
        catch (Exception e)
        {
            SetStatus("실행 중 오류: " + e.Message, error: true);
            _deps.Log?.AddMessage(MessageKind.Error, "PL/SQL 실행 중 오류: " + e.Message);
            return false;
        }
        finally
        {
            _cts = null;
            _deps.State?.ApplyTransaction(session.HasPendingTransaction);
            _deps.State?.EndRun();
            IsRunning = false;
        }

        return Present(block, result);
    }

    /// <summary>실행을 취소한다(서버에서 중단은 엔진이 맡는다).</summary>
    public void Cancel()
    {
        _deps.State?.MarkCancelling();
        _cts?.Cancel();
    }

    private bool Present(CallBlock block, ExecutionResult r)
    {
        if (r.DbmsOutput.Count > 0)
        {
            DbmsOutputText = string.Join(Environment.NewLine, r.DbmsOutput);
        }

        var time = r.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "초";
        var title = SelectedCandidate?.Target.QualifiedName ?? "PL/SQL";
        switch (r.Status)
        {
            case ExecutionStatus.Succeeded:
                foreach (var o in block.Outputs)
                {
                    r.OutValues.TryGetValue(o.BindName, out var value);
                    var row = Rows.FirstOrDefault(x => string.Equals(x.Parameter.BindName, o.BindName, StringComparison.OrdinalIgnoreCase));
                    if (value is Oranda.Core.Grid.IResultSet cursor)
                    {
                        Results.AddQuery(cursor, ":" + o.Label, block.Statement);
                        if (row is not null)
                        {
                            row.OutText = "(커서 — 결과 탭 참고)";
                        }
                    }
                    else if (row is not null)
                    {
                        row.OutText = CallBlock.FormatOut(o, value);
                    }
                }

                OnPropertyChanged(nameof(HasOutValues));
                SetStatus($"완료 ({time})" + (r.TransactionPending ? " — 미커밋 변경 있음" : string.Empty), error: false);
                _deps.Log?.AddMessage(MessageKind.Info, $"{title} — 완료 ({time})");
                return true;

            case ExecutionStatus.Failed:
                var text = Execution.DbErrorText.Format(r.Error);
                SetStatus(text, error: true);
                _deps.State?.SetError(block.Statement, r.Error, null);
                _deps.Log?.AddMessage(MessageKind.Error, $"{title} — {text}");
                return false;

            case ExecutionStatus.Denied:
                SetStatus("거부됨: " + (r.Policy?.Message ?? "정책 관문이 거부했습니다."), error: true);
                _deps.Log?.AddMessage(MessageKind.Warning, $"{title} — 거부됨: {r.Policy?.Message}");
                return false;

            default:
                SetStatus("취소되었습니다.", error: false);
                return false;
        }
    }

    private void ClearResults()
    {
        foreach (var r in Rows)
        {
            r.OutText = string.Empty;
        }

        Results.Clear();
        DbmsOutputText = string.Empty;
        SetStatus(string.Empty, error: false);
        OnPropertyChanged(nameof(HasOutValues));
    }

    private void SetStatus(string text, bool error)
    {
        StatusText = text;
        StatusIsError = error;
    }

    // ---- 편집기로 보내기 ----------------------------------------------------------------------

    /// <summary>
    /// 편집기에 넣을 글: 입력값을 주석으로 적고 블록을 붙인다. 바인드는 편집기에서 실행할 때 바인드 대화상자가 받는다
    /// (값을 글 안에 끼워 넣지 않는다).
    /// </summary>
    public string BuildEditorText()
    {
        if (_block is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var row in Rows.Where(r => r.CanEdit && !r.UseDefault))
        {
            var value = row.IsNull ? "NULL" : row.Text;
            sb.Append(CultureInfo.InvariantCulture, $"-- :{row.Parameter.BindName} ({row.DataType}) = {value}").AppendLine();
        }

        sb.AppendLine(_block.Text);
        sb.Append('/');
        return sb.ToString();
    }

    /// <summary>생성한 블록을 SQL 편집기 새 탭으로 보낸다. 보냈으면 true.</summary>
    public bool SendToEditor()
    {
        var text = BuildEditorText();
        if (text.Length == 0 || _deps.SendToEditor is null)
        {
            return false;
        }

        _deps.SendToEditor(text);
        return true;
    }
}
