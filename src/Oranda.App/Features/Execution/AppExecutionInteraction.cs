using System.Collections.Concurrent;
using Oranda.Core.Composition;
using Oranda.Core.Execution;

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
