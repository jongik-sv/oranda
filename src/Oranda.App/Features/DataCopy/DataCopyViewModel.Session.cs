using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

public sealed partial class DataCopyViewModel
{
    private async Task<CopySessions?> EnsureSessionsAsync(CancellationToken ct)
    {
        if (Editor.Source is not { } source || Editor.Target is not { } target)
        {
            Say(Editor.Source is null ? DataCopyText.SourceMissing : DataCopyText.TargetMissing, error: true);
            return null;
        }

        await _sessionGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_sessions is not null)
            {
                if (SameConnection(_sessions.Source, source.Profile, readOnlySource: true) && SameConnection(_sessions.Target, target.Profile))
                {
                    return _sessions;
                }

                await CloseSessionPairAsync().ConfigureAwait(true); // 접속을 바꿨으면 이전 세션은 쓰지 않는다
            }

            // 표를 추가하며 열어 둔 열 정보용 원본 세션이 같은 접속이면 점검 세션 쌍의 원본으로 넘겨 쓴다(비밀번호를 두 번 묻지 않는다).
            // 넘긴 뒤에는 쌍이 그 세션을 닫는다(대상을 못 열면 DataCopySessions 가 닫는다). 넘기기 전에 실패하면 열 정보용으로 그대로 남는다.
            var reuse = _metadataSession is { } meta && SameConnection(meta, source.Profile, readOnlySource: true) ? meta : null;
            if (reuse is null)
            {
                await CloseMetadataSessionAsync().ConfigureAwait(true);
            }

            var handedOver = false;
            Task<ISession?> Open(ConnectionProfile profile, CancellationToken token)
            {
                if (reuse is not null && !handedOver)
                {
                    handedOver = true;
                    _metadataSession = null;
                    return Task.FromResult<ISession?>(reuse);
                }

                return OpenSessionAsync(profile, token);
            }

            CopySessions? opened;
            try
            {
                opened = await DataCopySessions.OpenAsync(Open, source.Profile, target.Profile, ct).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Say("세션을 열지 못했습니다: " + e.Message, error: true);
                return null;
            }

            if (opened is null)
            {
                Say(DataCopyText.SessionsNotOpened, error: true);
                return null;
            }

            if (_disposed)
            {
                await DataCopySessions.CloseAsync(opened).ConfigureAwait(true); // 여는 사이에 창이 닫혔다
                return null;
            }

            // 대기 원인 감시(WH1): 실행기가 30초 넘게 기다리는 대상 명령의 원인을 보려고 짧은 세션을 같은 접속으로 연다(비밀번호를 다시 묻지 않는다).
            // 외부 적재 도구(조정 결정 88): 도구 찾기·실행기·대상 접속 정보(받은 비밀번호를 다시 쓴다). 없으면 외부 도구로 고른 테이블도 기본 방식으로 넣는다.
            _sessions = opened with
            {
                OpenMonitor = (session, token) => _services.OpenMonitorAsync(session.Profile, token),
                ExternalLoad = _services.ExternalLoad(opened.Target.Profile),
            };
            OnPropertyChanged(nameof(HasSessions));
            return _sessions;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// 열린 세션이 이 프로필로 연 것인지. 접속 ID·이름뿐 아니라 사용자·주소·환경 라벨·읽기 전용까지 같아야 한다
    /// (정책 관문은 세션이 열릴 때 받은 프로필 스냅숏으로 판단하므로, 바뀐 프로필에 옛 세션을 쓰지 않는다). 원본 세션은 늘 읽기 전용으로 연다.
    /// 비교 기준은 이 창이 세션을 열 때 넘긴 저장 프로필이다(<see cref="OpenedWith"/>): 세션 관리자는 드라이버용으로 바꾼 프로필
    /// (TNS 별칭 → 기술자, 프록시 → proxy[user])을 세션에 두므로, 세션이 보이는 값과 비교하면 TNS·프록시 접속은 늘 「바뀌었다」가 된다.
    /// </summary>
    private bool SameConnection(ISession session, ConnectionProfile profile, bool readOnlySource = false)
    {
        var opened = OpenedWith(session);
        return opened.Id == profile.Id && string.Equals(opened.Name, profile.Name, StringComparison.Ordinal)
               && string.Equals(opened.User, profile.User, StringComparison.Ordinal)
               && string.Equals(opened.DataSource, profile.DataSource, StringComparison.Ordinal)
               && opened.Environment == profile.Environment
               && opened.ReadOnly == (readOnlySource || profile.ReadOnly);
    }

    /// <summary>이 창이 <paramref name="session"/> 을 열 때 넘긴 저장 프로필. 이 창이 연 세션이 아니면 세션이 보이는 프로필.</summary>
    private ConnectionProfile OpenedWith(ISession session) => _openedWith.TryGetValue(session, out var profile) ? profile : session.Profile;

    /// <summary>세션을 열고 넘긴 저장 프로필을 기억한다(<see cref="SameConnection"/> 의 비교 기준).</summary>
    private async Task<ISession?> OpenSessionAsync(ConnectionProfile profile, CancellationToken ct)
    {
        var session = await _services.OpenSessionAsync(profile, ct).ConfigureAwait(true);
        if (session is not null)
        {
            _openedWith.AddOrUpdate(session, profile);
        }

        return session;
    }

    private static bool SameSnapshot(ConnectionProfile a, ConnectionProfile b) =>
        a.Id == b.Id && string.Equals(a.Name, b.Name, StringComparison.Ordinal) && string.Equals(a.User, b.User, StringComparison.Ordinal)
        && string.Equals(a.DataSource, b.DataSource, StringComparison.Ordinal) && a.Environment == b.Environment && a.ReadOnly == b.ReadOnly;

    /// <summary>
    /// 표 열 정보를 읽을 원본 세션. 점검용 세션 쌍이 열려 있고 원본이 같으면 그것을 쓰고, 아니면 원본만 읽기 전용으로 따로 연다
    /// (표를 추가하거나 「열 정보 읽기」를 누를 때만 연다). 원본을 고르지 않았거나 사용자가 비밀번호 입력을 취소하면 null.
    /// </summary>
    private async Task<ISession?> SourceSessionAsync(CancellationToken ct)
    {
        if (Editor.Source is not { } source)
        {
            return null;
        }

        await _sessionGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_sessions is { } pair && SameConnection(pair.Source, source.Profile, readOnlySource: true))
            {
                return pair.Source;
            }

            if (_metadataSession is { } open)
            {
                if (SameConnection(open, source.Profile, readOnlySource: true))
                {
                    return open;
                }

                await CloseMetadataSessionAsync().ConfigureAwait(true);
            }

            ISession? opened = null;
            try
            {
                opened = await OpenSessionAsync(source.Profile with { ReadOnly = true }, ct).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Say("원본 세션을 열지 못했습니다: " + e.Message, error: true);
            }

            if (opened is not null && _disposed)
            {
                await ConnectionManager.ConnectionService.CloseAsync(opened).ConfigureAwait(true); // 여는 사이에 창이 닫혔다
                return null;
            }

            _metadataSession = opened;
            return opened;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task CloseMetadataSessionAsync()
    {
        var session = _metadataSession;
        _metadataSession = null;
        if (session is not null)
        {
            await ConnectionManager.ConnectionService.CloseAsync(session).ConfigureAwait(true);
        }
    }
}
