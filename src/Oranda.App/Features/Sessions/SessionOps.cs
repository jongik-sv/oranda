using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Shell.Composition;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Sessions;

/// <summary>
/// 「세션」 도구 창의 우클릭 명령이 세션에 하는 일(커밋·롤백·미커밋 확인·끊기). 실행 컨트롤러와 접속 서비스는 쓸 때 꺼낸다
/// (창을 만들 때 Oracle 어셈블리를 깨우지 않는다, D-006). 시험은 가짜로 바꾼다.
/// </summary>
internal interface ISessionOps
{
    Task CommitAsync(ISession session, CancellationToken ct);

    Task RollbackAsync(ISession session, CancellationToken ct);

    /// <summary>미커밋이 있으면 접속 이름을 밝혀 커밋/롤백/취소를 묻는다. true = 이제 미커밋이 없다(닫아도 된다).</summary>
    Task<bool> ConfirmUncommittedAsync(ISession session, CancellationToken ct);

    Task DisconnectAsync(ISession session, CancellationToken ct);
}

/// <summary>컨테이너의 실행 컨트롤러·접속 서비스로 하는 실제 동작.</summary>
internal sealed class ContainerSessionOps(ServiceContainer container) : ISessionOps
{
    // 여러 작업 공간이 함께 쓰는 세션이면 함께 확정되는 작업 공간을 밝혀 먼저 묻는다(TSK-14-10).
    public Task CommitAsync(ISession session, CancellationToken ct) => ExecutionCommands.Resolve(container).ConfirmedCommitAsync(session, ct);

    public Task RollbackAsync(ISession session, CancellationToken ct) => ExecutionCommands.Resolve(container).ConfirmedRollbackAsync(session, ct);

    public Task<bool> ConfirmUncommittedAsync(ISession session, CancellationToken ct) =>
        ExecutionCommands.Resolve(container).ConfirmConnectionUncommittedAsync(session, ct);

    public Task DisconnectAsync(ISession session, CancellationToken ct) =>
        ConnectionManagerCommands.Resolve(container).DisconnectAsync(session, ct);
}
