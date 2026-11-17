using System.Collections.Concurrent;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;

namespace Oranda.App.Features.Execution;

/// <summary>
/// <see cref="IExecutionInteraction"/> 의 앱 구현(TSK-06-02). 실행 엔진이 확인·치환 변수·스크립트 오류를 물으면
/// <see cref="IExecutionDialogs"/> 로 대화상자를 띄운다. 어느 스레드에서 불려도 된다.
/// 치환 변수의 마지막 값은 메모리에만 둔다(디스크에 쓰지 않는다).
/// 생성자는 매개변수 없이 둔다(시작 때 Oracle 어셈블리를 깨우지 않기 위해, 04-02 decisions 와 같은 이유).
/// </summary>
[OrandaService(typeof(IExecutionInteraction))]
public sealed class AppExecutionInteraction : IExecutionInteraction
{
    private readonly ConcurrentDictionary<string, string> _lastSubstitutions = new(StringComparer.OrdinalIgnoreCase);
    private IExecutionDialogs? _dialogs;

    /// <summary>쓸 대화상자. 기본은 <see cref="ExecutionDialogs.Current"/>.</summary>
    public IExecutionDialogs Dialogs
    {
        get => _dialogs ?? ExecutionDialogs.Current;
        init => _dialogs = value;
    }

    /// <summary>확인 대화상자 머리 띠에 보일 접속. 실행 상태가 안다.</summary>
    public Func<Oranda.Core.Connection.ConnectionProfile?>? ProfileProvider { get; set; }

    /// <summary>원복 줄의 보관 일수·SCN 기록 줄을 정할 지금 설정. 없으면 기본값.</summary>
    public Func<RollbackSettings>? RollbackSettingsProvider { get; set; }

    /// <summary>원복 스크립트·백업 테이블 줄이 있는 확인(되돌리기 지점 설계 10.1). 제안이 없으면 예전 확인 창과 같다.</summary>
    public Task<ConfirmAnswer> ConfirmChangeAsync(SqlStatement stmt, PolicyDecision decision, ConfirmExtras extras, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        ArgumentNullException.ThrowIfNull(decision);

        // 운영 접속 그리드 반영(GridEditConfirmScope): 미리보기 글과 커밋 안내를 실은 창을 반영 하나에 한 번만 띄운다.
        // 이번 반영이 만든 DML 만 그 범위로 묻는다(그 밖의 문장은 늘 따로 묻는다).
        var grid = Oranda.App.Features.GridEdit.GridEditConfirmScope.Current is { } scope && scope.Covers(stmt) ? scope : null;
        var shown = grid is { Statements: > 1 } many && decision.Message is { Length: > 0 } m
            ? decision with { Message = m + Environment.NewLine + $"그리드 편집 {many.Statements}문장({many.Verbs})을 이 확인 한 번으로 모두 실행합니다. 원복 경고는 첫 문장 기준입니다." }
            : decision;
        extras ??= ConfirmExtras.None;
        var batch = grid is null && extras.Batch.Count > 0 ? extras.Batch : null;
        var request = new ConfirmRequest(stmt, shown, grid?.Profile ?? (batch is null ? null : extras.Profile) ?? ProfileProvider?.Invoke())
        {
            Extras = extras,
            Rollback = RollbackSettingsProvider?.Invoke() ?? new RollbackSettings(),
            Preview = grid?.Preview ?? (batch is null ? null : BatchPreview(batch)),
            Notice = grid?.Notice ?? (batch is null ? null : BatchNotice(batch.Count, extras.Findings)),
            StatementCount = grid?.Statements ?? batch?.Count ?? 1,
        };
        return grid is null
            ? Dialogs.ConfirmChangeAsync(request, ct)
            : grid.ConfirmAsync(decision, request.Extras, () => Dialogs.ConfirmChangeAsync(request, ct));
    }

    /// <summary>확인 한 번으로 실행할 문장 묶음의 본문(문장마다 <c>;</c>, 실행 순서, TE-34).</summary>
    internal static string BatchPreview(IReadOnlyList<SqlStatement> batch) =>
        string.Join(Environment.NewLine, batch.Select(s => s.Text.TrimEnd().TrimEnd(';') + ";"));

    /// <summary>문장 묶음 확인의 안내: 문장 수·DDL 자동 커밋(TE-21)과 실행 전 검사 줄(테이블 편집기 설계 10장, 운영은 이 창 하나로 묻는다).</summary>
    internal static string BatchNotice(int count, IReadOnlyList<Oranda.Core.Schema.TableEdit.CheckFinding> findings)
    {
        var text = Oranda.App.Features.TableEdit.TableEditText.T(
            $"{count}문장을 이 확인 한 번으로 차례로 실행합니다. 앞 문장이 성공한 뒤 뒤 문장이 실패하면 앞 문장은 남습니다(DDL 은 문장마다 자동 커밋). 이 확인에는 백업 테이블 제안이 없습니다 — 필요하면 먼저 백업하세요.",
            $"Runs {count} statement(s) in order with this one confirmation. If a later statement fails, the earlier ones stay applied (each DDL commits automatically). This confirmation offers no backup table — back up first if you need one.");
        return findings is { Count: > 0 }
            ? text + Environment.NewLine + Environment.NewLine + Oranda.App.Features.TableEdit.CheckFindingText.FormatAll(findings)
            : text;
    }

    public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        ArgumentNullException.ThrowIfNull(decision);
        var profile = ProfileProvider?.Invoke();
        return Dialogs.ConfirmAsync(new ConfirmRequest(stmt, decision, profile), ct);
    }

    public async Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        var answer = await Dialogs.AskSubstitutionsAsync(names, _lastSubstitutions, ct).ConfigureAwait(false);
        if (answer is not null)
        {
            foreach (var (k, v) in answer)
            {
                _lastSubstitutions[k] = v;
            }
        }

        return answer;
    }

    public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stmt);
        ArgumentNullException.ThrowIfNull(failure);
        return Dialogs.AskScriptErrorAsync(stmt, failure, ct);
    }
}

/// <summary>화면 대화상자의 기본 구현을 쥐는 자리. 시험은 <see cref="Current"/> 를 가짜로 바꾼다.</summary>
public static class ExecutionDialogs
{
    private static IExecutionDialogs? s_current;

    public static IExecutionDialogs Current
    {
        get => s_current ??= new AvaloniaExecutionDialogs();
        set => s_current = value;
    }

    /// <summary>아직 정해진 구현이 없으면 주 창 위에 띄우는 화면 구현을 쓴다(셸의 <c>MainWindow</c> 를 소유자로).</summary>
    public static void UseOwner(Func<Avalonia.Controls.Window?> owner) => s_current ??= new AvaloniaExecutionDialogs(owner);
}
