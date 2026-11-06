using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 데이터 복사 창이 쓰는 서비스 모음. 모두 처음 쓸 때 <see cref="IServiceProvider"/> 에서 꺼낸다(D-006: 첫 프레임 전에 Oracle 어셈블리를 올리지 않는다).
/// 구현이 아직 등록되지 않았으면 null 이고, 창은 죽지 않고 상태 줄에 알린다. 시험은 속성에 가짜를 직접 넣는다.
/// </summary>
public sealed class DataCopyServices
{
    private readonly IServiceProvider? _provider;

    public DataCopyServices(IServiceProvider? provider = null) => _provider = provider;

    public ICopyJobStore? Store { get => field ?? Get<ICopyJobStore>(); init; }

    public ICopyPrecheckService? Precheck { get => field ?? Get<ICopyPrecheckService>(); init; }

    public ICopyRunner? Runner { get => field ?? Get<ICopyRunner>(); init; }

    public ICopyRunLog? RunLog { get => field ?? Get<ICopyRunLog>(); init; }

    public IMetadataProvider? Metadata { get => field ?? Get<IMetadataProvider>(); init; }

    public IExecutionService? Execution { get => field ?? Get<IExecutionService>(); init; }

    public IConnectionStore? Connections { get => field ?? Get<IConnectionStore>(); init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>접속 목록을 바꿔 쓴다(시험). 없으면 <see cref="Connections"/> 에서 읽는다.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ConnectionProfile>>>? ProfileLoader { get; init; }

    /// <summary>전용 세션 열기를 바꿔 쓴다(시험). 없으면 앱의 전용 세션 경로(비밀번호 안전 저장소·입력창)를 쓴다.</summary>
    public Func<ConnectionProfile, CancellationToken, Task<ISession?>>? SessionOpener { get; init; }

    /// <summary>세션에서 문장 하나를 실행하고 실패 이유(성공이면 null)를 돌려준다. 없으면 <see cref="Execution"/> 으로 실행한다.</summary>
    public Func<ISession, string, CancellationToken, Task<string?>>? StatementRunner { get; init; }

    private T? Get<T>()
        where T : class => _provider?.GetService(typeof(T)) as T;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadProfilesAsync(CancellationToken ct)
    {
        if (ProfileLoader is { } loader)
        {
            return await loader(ct).ConfigureAwait(true);
        }

        if (Connections is not { } store)
        {
            return [];
        }

        var document = await store.LoadAsync(ct).ConfigureAwait(true);
        return document.Value.Profiles;
    }

    public async Task<ISession?> OpenSessionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (SessionOpener is { } opener)
        {
            return await opener(profile, ct).ConfigureAwait(true);
        }

        return _provider is ServiceContainer container
            ? await DedicatedSession.DedicatedSessionCommands.OpenDedicatedAsync(container, profile, ct).ConfigureAwait(true)
            : null;
    }

    public async Task<string?> RunStatementAsync(ISession session, string sql, CancellationToken ct)
    {
        if (StatementRunner is { } runner)
        {
            return await runner(session, sql, ct).ConfigureAwait(true);
        }

        if (Execution is not { } execution)
        {
            return DataCopyText.ExecutionMissing;
        }

        var statement = new SqlStatement(sql, default, StatementKind.Ddl) { Verb = "ALTER" };
        var options = new ExecutionOptions { Source = HistorySource.DataCopy };
        var result = await execution.ExecuteAsync(session, statement, new Dictionary<string, BindValue>(), options, ct).ConfigureAwait(true);
        return result.Status == ExecutionStatus.Succeeded ? null : result.Error?.Message ?? result.Policy?.Message ?? result.Status.ToString();
    }
}
