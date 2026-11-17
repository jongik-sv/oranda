using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Sessions;

namespace Oranda.Core.ChangeBundle;

/// <summary>
/// 앱이 부르는 반영 묶음 서비스(설계 9장): 「지금 묶음」, 테이블 편집기 「묶음에 넣기」, 묶음 실행의 실행 표시.
/// 자동 넣기(9.3)도 이 서비스를 지난다(<c>ChangeBundleRecorder</c>). 넣는 문장은 소유자를 붙인 꼴로 바꿔 저장한다(<see cref="IOwnerRewriter.Qualify"/>).
/// 저장·판정 실패는 예외 대신 결과로 돌려준다(실행 흐름을 막지 않는다).
/// </summary>
public interface IChangeBundleService
{
    /// <summary>「지금 묶음」. 없으면 null.</summary>
    Task<ChangeBundleInfo?> GetCurrentAsync(CancellationToken ct);

    /// <summary>
    /// 열린 세션의 사실(그 세션에서 성공한 <c>ALTER SESSION SET CURRENT_SCHEMA</c> 를 따른 현재 스키마 포함). 서비스가 모르는 세션이면 null —
    /// 부르는 쪽은 <see cref="ChangeBundleOrigin.From(ISession, string?)"/> 로 대신한다.
    /// </summary>
    ChangeBundleOrigin? OriginOf(Guid sessionId);

    /// <summary>
    /// 새 묶음을 만들고 <paramref name="makeCurrent"/> 면 「지금 묶음」으로 둔다. 대상 소유자는 <paramref name="origin"/> 의 현재 스키마로 채운다(TE-24).
    /// origin 이 없으면 빈 목록으로 두고, DBMS 처럼 첫 문장의 소유자를 따른다. DBMS 는 비워 두고 첫 문장을 따른다(9.3). 쓸 수 없으면 null.
    /// </summary>
    Task<ChangeBundleInfo?> CreateAsync(string name, ChangeBundleOrigin? origin, bool makeCurrent, CancellationToken ct);

    /// <summary>
    /// 실행하지 않은 문장을 「지금 묶음」 끝에 넣는다(테이블 편집기 「묶음에 넣기」, 묶음 창 직접 넣기). 판정은 자동 넣기와 같다(DBMS·대상 소유자·해시 중복),
    /// 다만 실행 표시는 남기지 않는다. 같은 해시가 이미 있으면 넣지 않는다.
    /// </summary>
    Task<ChangeBundleAddResult> AddAsync(ChangeBundleOrigin origin, IReadOnlyList<ChangeBundleDraft> statements, ChangeBundleItemSource source,
        CancellationToken ct);

    /// <summary>
    /// 실행에 성공한 문장 하나(자동 넣기). 넣거나, 같은 해시가 있으면 그 문장에 실행 표시만 남긴다. 대상 소유자를 문장에서 읽지 못하면
    /// 분류기의 <see cref="Execution.SqlStatement.ChangeTarget"/> 소유자, 그것도 없으면 세션의 현재 스키마로 본다.
    /// </summary>
    Task<ChangeBundleAddResult> AddExecutedAsync(ChangeBundleOrigin origin, Execution.SqlStatement statement, ChangeBundleItemSource source,
        CancellationToken ct);

    /// <summary>묶음 「실행…」이 문장 하나를 실행한 결과를 남긴다(성공·실패 모두, 실행 표시 칸은 성공만 본다).</summary>
    Task RecordRunAsync(long itemId, ChangeBundleOrigin origin, bool succeeded, CancellationToken ct);
}

/// <summary>
/// 문장을 보낸 접속의 사실. <see cref="CurrentSchema"/> 는 소유자를 안 쓴 문장의 소유자(대문자 규칙 이름),
/// <see cref="ConnectionName"/> 은 접속 이름(실행 표시에 남는다, 호스트는 두지 않는다).
/// </summary>
public sealed record ChangeBundleOrigin(Dbms Dbms, string CurrentSchema, ConnectionEnvironment Environment, string ConnectionName)
{
    /// <summary>세션에서 만든다. 현재 스키마를 모르면 접속 사용자(<see cref="ServerInfo.SessionUser"/>, 없으면 프로필 사용자)를 대문자로 쓴다.</summary>
    public static ChangeBundleOrigin From(ISession session, string? currentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return From(session.Profile, session.Server, currentSchema);
    }

    /// <inheritdoc cref="From(ISession, string?)"/>
    public static ChangeBundleOrigin From(ConnectionProfile profile, ServerInfo server, string? currentSchema = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(server);
        var schema = currentSchema ?? (server.SessionUser ?? profile.User).ToUpperInvariant();
        return new ChangeBundleOrigin(server.Dbms, schema, profile.Environment, profile.Name);
    }
}

/// <summary>
/// 넣을 문장 하나. <see cref="Warning"/> 은 만든 쪽이 아는 ⚠(서버 이름 제약 TE-8 등).
/// <see cref="TargetOwner"/> 는 만든 쪽이 아는 대상 소유자(문장에서 읽지 못할 때 쓴다, 없으면 null).
/// </summary>
public sealed record ChangeBundleDraft(string Text, string? Warning = null, string? TargetOwner = null);

/// <summary>넣기 결과의 큰 갈래.</summary>
public enum ChangeBundleAddOutcome
{
    /// <summary>하나 이상 넣었거나 실행 표시를 남겼다(<see cref="ChangeBundleAddResult.Added"/>·<see cref="ChangeBundleAddResult.Marked"/>).</summary>
    Done,

    /// <summary>모두 이미 있었다(실행하지 않은 넣기).</summary>
    AlreadyPresent,

    /// <summary>「지금 묶음」이 없다.</summary>
    NoCurrentBundle,

    /// <summary>접속 DBMS 가 묶음 DBMS 와 다르다(<see cref="ChangeBundleDbmsMismatch"/> 도 낸다).</summary>
    DbmsMismatch,

    /// <summary>대상 소유자가 묶음 대상이 아니라 아무것도 넣지 않았다.</summary>
    OwnerNotTarget,

    /// <summary>저장소에 쓸 수 없다.</summary>
    NotWritable,
}

/// <summary>넣기 결과. <see cref="Added"/> = 새로 넣은 문장 수, <see cref="Marked"/> = 같은 해시라 실행 표시만 남긴 수, <see cref="Skipped"/> = 대상 소유자가 아니라 뺀 수.</summary>
public sealed record ChangeBundleAddResult(ChangeBundleAddOutcome Outcome, long? BundleId, int Added, int Marked, int Skipped, int AlreadyPresent)
{
    public static ChangeBundleAddResult Of(ChangeBundleAddOutcome outcome, long? bundleId = null) => new(outcome, bundleId, 0, 0, 0, 0);
}

/// <summary>묶음 또는 그 문장이 바뀌었다(묶음 창 다시 읽기·제목 건수 잠깐 표시). <paramref name="BundleId"/> = null 이면 묶음 목록·「지금 묶음」이 바뀜.</summary>
public sealed record ChangeBundleChanged(long? BundleId, int Added, int Marked) : OrandaEvent;

/// <summary>자동 넣기가 DBMS 가 달라 넣지 않았다(설계 9.3: 메시지 창에 한 줄). 문구는 앱이 만든다.</summary>
public sealed record ChangeBundleDbmsMismatch(long BundleId, string BundleName, Dbms BundleDbms, Dbms SessionDbms) : OrandaEvent;
