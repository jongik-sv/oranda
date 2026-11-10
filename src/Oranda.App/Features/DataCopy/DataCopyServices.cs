using System.Collections.Concurrent;
using System.Text;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
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

    /// <summary>
    /// 이 창이 세션을 열 때 받은 비밀번호(접속 Id 별). 대기 원인 감시 세션(WH1)이 다시 묻지 않고 쓴다. 창을 닫으면 지운다(<see cref="ForgetSecrets"/>).
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SecureCredential> _secrets = new();

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

    /// <summary>대기 원인 감시 세션 열기를 바꿔 쓴다(시험). 없으면 이 창이 받은 비밀번호로 <see cref="SessionPurpose.Monitor"/> 세션을 연다.</summary>
    public Func<ConnectionProfile, CancellationToken, Task<ISession?>>? MonitorOpener { get; init; }

    /// <summary>세션에서 문장 하나를 실행하고 실패 이유(성공이면 null)를 돌려준다. 없으면 <see cref="Execution"/> 으로 실행한다.</summary>
    public Func<ISession, string, CancellationToken, Task<string?>>? StatementRunner { get; init; }

    /// <summary>
    /// 이 DBMS 쌍을 복사할 수 있는가. 같은 DBMS 끼리는 항상 참, 다른 DBMS 는 지원 쌍(Oracle↔Tibero)이고 두 DBMS 의 교차 부품(<see cref="ICopyDbmsParts"/>)이
    /// 모두 등록돼 있을 때 참. 서비스 선택기가 없는 조립(시험)은 쌍 규칙만 본다. <see cref="PairSupport"/> 로 바꿔 쓸 수 있다(시험).
    /// </summary>
    public bool IsPairSupported(Dbms source, Dbms target)
    {
        if (PairSupport is { } custom)
        {
            return custom(source, target);
        }

        if (source == target)
        {
            return true;
        }

        // Core 디스패처의 쌍 규칙(DispatchRules.IsCrossCopyPair, xdb-copy D1)과 같다 — 그쪽은 Core 내부 형식이라 여기서 한 번 더 적는다.
        if ((source, target) is not ((Dbms.Oracle, Dbms.Tibero) or (Dbms.Tibero, Dbms.Oracle)))
        {
            return false;
        }

        var selector = DbmsServices.SelectorOf(_provider);
        return selector is null || (selector.Find<ICopyDbmsParts>(source) is not null && selector.Find<ICopyDbmsParts>(target) is not null);
    }

    /// <summary>쌍 판정을 바꿔 쓴다(시험).</summary>
    public Func<Dbms, Dbms, bool>? PairSupport { get; init; }

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

        if (_provider is not ServiceContainer container)
        {
            return null;
        }

        // 앱의 전용 세션 경로(DedicatedSessionCommands.OpenDedicatedAsync)와 같다: 안전 저장소에서 꺼내고 없으면 묻는다.
        // 다른 점은 받은 비밀번호를 이 창이 닫힐 때까지 들고 있는 것뿐이다(대기 원인 감시 세션이 다시 묻지 않게, WH1).
        var secret = await StoredSecretAsync(container, profile).ConfigureAwait(true);
        if (string.IsNullOrEmpty(secret) && container.GetService<ShellController>()?.MainWindow is { } owner)
        {
            secret = await ConnectionDialogs.PromptPasswordAsync(owner, profile).ConfigureAwait(true);
        }

        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        using var credential = new SecureCredential(secret);
        var session = await container.GetRequiredService<ISessionManager>()
            .OpenAsync(profile, credential, SessionPurpose.Dedicated, ct).ConfigureAwait(true);
        Remember(profile, secret);
        return session;
    }

    /// <summary>
    /// 대기 원인 감시용 짧은 세션(WH1, <see cref="CopySessions.OpenMonitor"/>)을 <paramref name="profile"/> 로 연다. 비밀번호는 이 창이 세션을 열 때 받은 것,
    /// 없으면 안전 저장소의 것이다. 새로 묻지 않는다: 둘 다 없으면 null(감시 없이 대기 시간만 보인다).
    /// </summary>
    public async Task<ISession?> OpenMonitorAsync(ConnectionProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (MonitorOpener is { } opener)
        {
            return await opener(profile, ct).ConfigureAwait(true);
        }

        if (_provider is not ServiceContainer container)
        {
            return null;
        }

        // 들고 있는 비밀번호는 글(string)로 꺼내지 않고 버퍼째 새 그릇으로 옮긴다(쓰고 나면 지운다).
        SecureCredential? credential = null;
        if (_secrets.TryGetValue(profile.Id, out var kept))
        {
            try
            {
                credential = new SecureCredential(kept.Secret);
            }
            catch (ObjectDisposedException)
            {
                // 창이 닫히는 중이다.
            }
        }

        if (credential is null && await StoredSecretAsync(container, profile).ConfigureAwait(true) is { Length: > 0 } stored)
        {
            credential = new SecureCredential(stored);
        }

        if (credential is null)
        {
            return null;
        }

        using (credential)
        {
            return await container.GetRequiredService<ISessionManager>()
                .OpenAsync(profile, credential, SessionPurpose.Monitor, ct).ConfigureAwait(true);
        }
    }

    /// <summary>들고 있던 비밀번호를 지운다(창을 닫을 때).</summary>
    public void ForgetSecrets()
    {
        foreach (var id in _secrets.Keys)
        {
            if (_secrets.TryRemove(id, out var credential))
            {
                credential.Dispose();
            }
        }
    }

    private void Remember(ConnectionProfile profile, string secret)
    {
        var credential = new SecureCredential(secret);
        _secrets.AddOrUpdate(profile.Id, credential, (_, old) =>
        {
            old.Dispose();
            return credential;
        });
    }

    private static async Task<string?> StoredSecretAsync(ServiceContainer container, ConnectionProfile profile)
    {
        if (profile.CredentialRef is not { } reference || container.GetService<ICredentialStore>() is not { } store
            || await store.LoadAsync(reference).ConfigureAwait(true) is not { } bytes)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    /// <summary>
    /// 같은 접속으로 전용 세션 여러 개 열기를 바꿔 쓴다(시험, 동시 복사 레인 PF2). 없으면 <see cref="SessionOpener"/> 를 여러 번 부르고,
    /// 그것도 없으면 앱의 전용 세션 경로(비밀번호 한 번)를 쓴다.
    /// </summary>
    public Func<ConnectionProfile, int, CancellationToken, Task<DedicatedSession.DedicatedSessionBatch>>? SessionsOpener { get; init; }

    /// <summary>
    /// 같은 접속으로 전용 세션을 <paramref name="count"/>개까지 연다(<see cref="DedicatedSession.DedicatedSessionCommands.OpenDedicatedManyAsync"/>).
    /// 여는 중 실패하면 그때까지 연 세션과 오류 글을 돌려준다. 연 세션은 부른 쪽이 닫는다.
    /// </summary>
    public async Task<DedicatedSession.DedicatedSessionBatch> OpenSessionsAsync(ConnectionProfile profile, int count, CancellationToken ct)
    {
        if (SessionsOpener is { } many)
        {
            return await many(profile, count, ct).ConfigureAwait(true);
        }

        if (SessionOpener is { } opener)
        {
            var opened = new List<ISession>();
            for (var i = 0; i < count; i++)
            {
                ISession? session;
                try
                {
                    session = await opener(profile, ct).ConfigureAwait(true);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return new DedicatedSession.DedicatedSessionBatch(opened, e.Message);
                }

                if (session is null)
                {
                    break;
                }

                opened.Add(session);
            }

            return new DedicatedSession.DedicatedSessionBatch(opened, null);
        }

        return _provider is ServiceContainer container
            ? await DedicatedSession.DedicatedSessionCommands.OpenDedicatedManyAsync(container, profile, count, ct).ConfigureAwait(true)
            : new DedicatedSession.DedicatedSessionBatch([], null);
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
