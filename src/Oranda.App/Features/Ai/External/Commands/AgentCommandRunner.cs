using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai.External.Commands;

/// <summary>실행 전에 거른 까닭. <see cref="None"/> 이면 실행(또는 확인)까지 갔다.</summary>
public enum CommandRefusal
{
    None,

    /// <summary>레지스트리에 없는 id·별칭(기능 게이트가 끈 명령 포함) → <c>command_not_found</c>.</summary>
    NotFound,

    /// <summary>등급표 「거부」·표에 없는 명령 → <c>command_denied</c>.</summary>
    Denied,

    /// <summary>활성 조건이 지금 거짓 → <c>command_disabled</c>.</summary>
    Disabled,
}

/// <summary><c>run_command</c> 결과 상태(<c>status</c> 칸).</summary>
public enum CommandRunStatus
{
    /// <summary>바로 등급을 실행했다(대화상자를 여는 명령은 열린 채로 돌려준다).</summary>
    Done,

    /// <summary>띠에서 사람이 받아들여 실행했다.</summary>
    Confirmed,

    /// <summary>띠에서 사람이 버렸다. 실행하지 않았다.</summary>
    Rejected,

    /// <summary>기다리는 동안 사람이 정하지 않았다. 띠를 거두고 실행하지 않았다.</summary>
    Timeout,

    /// <summary>확인을 기다리는 사이 문맥이 바뀌어 지금은 실행할 수 없다(레지스트리가 거절).</summary>
    Disabled,
}

/// <summary>사람의 띠 답.</summary>
public enum CommandConfirmAnswer
{
    Accepted,
    Rejected,
    Timeout,
}

/// <param name="Refusal">실행 전에 거른 까닭(<see cref="CommandRefusal.None"/> 이면 <see cref="Status"/> 가 뜻을 가진다).</param>
/// <param name="Status">실행 상태.</param>
/// <param name="Command">찾은 정의(정식 id). 못 찾았으면 null.</param>
/// <param name="Grade">등급(못 찾았으면 거부).</param>
public sealed record CommandRunResult(CommandRefusal Refusal, CommandRunStatus Status, CommandDefinition? Command, CommandGrade Grade)
{
    public static CommandRunResult Refused(CommandRefusal refusal, CommandDefinition? command, CommandGrade grade) => new(refusal, CommandRunStatus.Disabled, command, grade);
}

/// <summary>
/// 밖의 에이전트의 <c>run_command</c> 본체(2단계 계획 U3, §3.3). UI 스레드에서 부른다.
/// <list type="number">
/// <item>id·별칭을 레지스트리로 정식 id 로 푼다(별칭으로 등급표를 비켜 가지 않게). 없으면 거부.</item>
/// <item>등급표 「거부」(표에 없는 id 포함)면 실행하지 않는다.</item>
/// <item>활성 조건이 거짓이면 실행하지 않는다.</item>
/// <item>「띠 한 번」 이면 사람에게 묻고, 받아들인 뒤 활성 조건을 다시 본다.</item>
/// <item>메뉴와 같은 길(<see cref="ICommandRegistry.ExecuteAsync"/>)로 실행한다. 인자(<c>parameter</c>)는 넘기지 않는다(E-31).</item>
/// </list>
/// 대화상자를 여는 명령은 사람이 닫을 때까지 끝나지 않으므로 <see cref="StartWait"/> 만 기다리고 돌려준다(다리를 잡지 않는다).
/// 그 뒤의 예외는 메뉴·단추와 같이 기록에만 남긴다.
/// </summary>
public sealed class AgentCommandRunner(ICommandRegistry registry, IContextKeys keys)
{
    private readonly ICommandRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IContextKeys _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    /// <summary>실행이 끝나기를 기다리는 시간. 넘으면 열린 채(대화상자 등)로 돌려준다.</summary>
    public TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>실행 전 검사만(띠·실행 없음). 거부면 그 까닭.</summary>
    public CommandRunResult Check(string idOrAlias)
    {
        ArgumentNullException.ThrowIfNull(idOrAlias);
        if (_registry.Find(idOrAlias.Trim()) is not { } command)
        {
            return CommandRunResult.Refused(CommandRefusal.NotFound, null, CommandGrade.Deny);
        }

        var grade = CommandGradeTable.Grade(command.Id);
        if (grade == CommandGrade.Deny)
        {
            return CommandRunResult.Refused(CommandRefusal.Denied, command, grade);
        }

        return _registry.CanExecute(command.Id, _keys)
            ? new CommandRunResult(CommandRefusal.None, CommandRunStatus.Done, command, grade)
            : CommandRunResult.Refused(CommandRefusal.Disabled, command, grade);
    }

    /// <param name="idOrAlias">명령 id 또는 별칭.</param>
    /// <param name="confirm">「띠 한 번」 등급에서 사람에게 묻는 창구(정의 → 답). 바로 등급에서는 부르지 않는다.</param>
    /// <param name="ct">기다림(띠·시작)을 끊는다. 이미 시작한 명령은 끊지 않는다(메뉴로 연 것과 같게).</param>
    public async Task<CommandRunResult> RunAsync(string idOrAlias, Func<CommandDefinition, CancellationToken, Task<CommandConfirmAnswer>> confirm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        var check = Check(idOrAlias);
        if (check.Refusal != CommandRefusal.None || check.Command is not { } command)
        {
            return check;
        }

        var status = CommandRunStatus.Done;
        if (check.Grade == CommandGrade.Confirm)
        {
            switch (await confirm(command, ct).ConfigureAwait(true))
            {
                case CommandConfirmAnswer.Rejected:
                    return check with { Status = CommandRunStatus.Rejected };
                case CommandConfirmAnswer.Timeout:
                    return check with { Status = CommandRunStatus.Timeout };
            }

            if (!_registry.CanExecute(command.Id, _keys))
            {
                return check with { Status = CommandRunStatus.Disabled };
            }

            status = CommandRunStatus.Confirmed;
        }

        ct.ThrowIfCancellationRequested();
        var run = _registry.ExecuteAsync(command.Id, _keys, parameter: null, CancellationToken.None);
        if (await Task.WhenAny(run, Task.Delay(StartWait, CancellationToken.None)).ConfigureAwait(true) == run)
        {
            return check with { Status = await run.ConfigureAwait(true) ? status : CommandRunStatus.Disabled };
        }

        _ = run.ContinueWith(
            t => Trace.TraceError("에이전트가 부른 명령 '{0}' 실행 중 예외: {1}", command.Id, t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        return check with { Status = status };
    }
}
