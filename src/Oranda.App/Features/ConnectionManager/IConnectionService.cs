using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Language.Errors;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 접속 프로필 관리와 열린 접속(작업 세션, CONN-06). 접속은 여러 개를 동시에 열고, 그중 하나가 「지금 접속」(<see cref="ActiveSession"/>)이다.
/// 탭·문서가 쓸 세션은 직접 고르지 않고 <see cref="SessionRouter"/> 에 묻는다. 접속 관리자 화면·명령·다른 기능이 쓴다.
/// </summary>
public interface IConnectionService
{
    /// <summary>지금 접속(활성 문서의 접속). 없으면 null.</summary>
    ISession? ActiveSession { get; }

    /// <summary>열린 접속(프로필마다 첫 작업 세션). 연 순서이고 툴바 콤보 번호는 위치 + 1 이다. 같은 DB 에 더 연 세션은 <see cref="SessionsOf"/>.</summary>
    IReadOnlyList<ISession> Sessions { get; }

    /// <summary>지금 접속의 프로필, 지금 접속이 없으면 마지막으로 접속한 프로필(끊겨도 남는다. 「다시 접속」용).</summary>
    ConnectionProfile? LastProfile { get; }

    /// <summary>지금 접속이 끊긴 상태.</summary>
    bool IsBroken { get; }

    /// <summary>접속 대화 상자에서 고를 수 있는 DBMS(D-046). 기본은 공급자 어셈블리 유무만 본다. 실제 서비스는 세션 관리자 구현이 있는 DBMS 만 낸다(<see cref="DbmsAvailability.Detect(string, Oranda.Core.Composition.IDbmsServiceSelector?)"/>).</summary>
    IReadOnlyList<Dbms> AvailableDbms => DbmsAvailability.Current;

    /// <summary>
    /// <paramref name="dbms"/> 의 드라이버 상태(사용자가 위치를 지정하는 드라이버: Tibero). 그 DBMS 용 <see cref="IDbmsDriverStatus"/> 구현이 없으면 null
    /// (접속 대화상자는 Tibero 에서 null 을 「지정되지 않음」 으로 본다. Oracle 은 드라이버를 앱이 싣고 있어 묻지 않는다).
    /// </summary>
    IDbmsDriverStatus? DriverStatusOf(Dbms dbms) => null;

    /// <summary>지금 접속·열린 접속 목록·끊김 상태가 바뀌었다. 아무 스레드에서나 온다.</summary>
    event EventHandler? ActiveSessionChanged;

    /// <summary>접속에 성공했다(새로 열었거나 이미 열린 접속을 지금 접속으로 바꿨다). 활성 SQL 탭이 이 접속을 따른다.</summary>
    event EventHandler<ISession>? Connected;

    /// <summary>프로필의 열린 작업 세션(끊긴 것 포함). 없으면 null.</summary>
    ISession? SessionOf(Guid profileId);

    /// <summary>열린 작업 세션 중 끊긴 것인지.</summary>
    bool IsSessionBroken(Guid sessionId);

    /// <summary>
    /// 프로필의 열린 작업 세션 모두(TSK-14-01): 첫째가 접속(<see cref="SessionOf"/>), 그 뒤가 같은 DB 에 더 연 세션(연 순서). 없으면 빈 목록.
    /// </summary>
    IReadOnlyList<ISession> SessionsOf(Guid profileId) => SessionOf(profileId) is { } s ? [s] : [];

    /// <summary>열린 작업 세션 모두: 접속 순서대로, 접속마다 그 접속과 더 연 세션(<see cref="SessionsOf"/>). 앱 종료 미커밋 확인이 쓴다.</summary>
    IReadOnlyList<ISession> AllSessions => Sessions;

    /// <summary>열린 작업 세션(접속이든 더 연 세션이든)인지.</summary>
    bool IsOpen(ISession session) => Sessions.Contains(session);

    /// <summary>
    /// 이미 연 접속과 같은 DB 에 작업 세션을 하나 더 연다(TSK-14-01, 워크스페이스 세션). 비밀번호는 그 접속을 열 때 쓴 것(메모리에만 있다)으로 다시 묻지 않는다.
    /// 지금 접속은 바꾸지 않는다. 접속이 열려 있지 않거나 프로필당 상한(<see cref="ConnectionService.MaxSessionsPerProfile"/>)이면 실패.
    /// </summary>
    Task<ConnectOutcome> OpenSessionAsync(Guid profileId, CancellationToken ct) =>
        Task.FromResult(new ConnectOutcome(ConnectStatus.Failed, "같은 DB 에 세션을 더 열 수 없습니다."));

    /// <summary>
    /// 끊긴 접속을 같은 자리에 다시 열어(<see cref="ConnectAsync"/>) <paramref name="closed"/> 를 대신한 세션(여러 번 다시 열었으면 마지막 것, TSK-14-07).
    /// 접속을 끊어 이미 있던 추가 세션이 그 자리로 올라온 것은 대체가 아니다(null): 그 세션을 쓰던 워크스페이스가 남의 트랜잭션으로 몰래 옮기지 않게.
    /// </summary>
    ISession? ReopenedAs(ISession closed) => null;

    /// <summary>열린 작업 세션 하나를 지금 접속으로 바꾼다. 목록에 없으면 false.</summary>
    bool Switch(ISession session);

    Task<StoredDocument<ConnectionsDocument>> LoadAsync(CancellationToken ct);

    /// <summary>프로필을 추가·수정한다(같은 <c>Id</c> 면 수정). 비밀번호는 안전 저장소에만 둔다.</summary>
    /// <param name="profile">저장할 프로필. <c>Id</c> 가 비어 있으면 새로 발급한다.</param>
    /// <param name="password">새 비밀번호. null·빈 값이면 저장된 것을 그대로 둔다.</param>
    /// <param name="savePassword">false 면 저장된 비밀번호를 지우고 접속 때마다 묻는다.</param>
    /// <param name="ct">취소.</param>
    Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? password, bool savePassword, CancellationToken ct);

    /// <summary>프로필과 저장된 비밀번호·풀어 놓은 지갑을 지운다.</summary>
    Task DeleteProfileAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// 접속한다. 다른 접속은 닫지 않고 목록에 더하며 새 접속이 지금 접속이 된다. 같은 프로필이 이미 열려 있으면 새로 열지 않고
    /// 그 접속을 지금 접속으로 바꾼다(끊긴 것이면 닫고 같은 자리에 다시 연다). 열린 접속이 <see cref="ConnectionService.MaxSessions"/> 개면 실패.
    /// </summary>
    Task<ConnectOutcome> ConnectAsync(ConnectionProfile profile, string? password, CancellationToken ct);

    /// <summary>지금 접속을 끊는다. 다른 접속은 그대로이고 마지막에 연 접속이 지금 접속이 된다.</summary>
    Task DisconnectAsync(CancellationToken ct);

    /// <summary>
    /// 열린 작업 세션 하나를 끊는다(목록에 없으면 아무것도 안 한다). 접속(프로필의 첫 세션)을 끊었는데 같은 DB 에 더 연 세션이 있으면
    /// 그중 처음 연 것이 같은 자리(콤보 번호)의 접속이 된다(TSK-14-01).
    /// </summary>
    Task DisconnectAsync(ISession session, CancellationToken ct);

    /// <summary>마지막 프로필로 다시 접속한다(저장된 비밀번호가 있을 때).</summary>
    Task<ConnectOutcome> ReconnectAsync(string? password, CancellationToken ct);

    /// <summary>비밀번호를 바꾼다. 비밀번호를 저장하는 프로필이면 저장소의 값도 새 값으로 바꾼다.</summary>
    Task<PasswordChangeOutcome> ChangePasswordAsync(ConnectionProfile profile, string? oldPassword, string newPassword, CancellationToken ct);

    /// <summary>저장된 비밀번호가 있는지.</summary>
    Task<bool> HasStoredPasswordAsync(ConnectionProfile profile, CancellationToken ct);

    /// <summary>ADB 지갑 zip 으로 프로필을 만들어 저장한다(지갑은 데이터 폴더 <c>wallets/{id}</c> 에 푼다). 비밀번호는 비워 둔다.</summary>
    Task<ConnectionProfile> ImportWalletAsync(string zipPath, string? walletAlias, CancellationToken ct);

    /// <summary>
    /// 지금 값(저장 안 한 편집 값 포함)으로 임시 연결을 열어 서버 버전을 읽고 바로 닫는다.
    /// 세션 관리자를 거치지 않으므로 앱의 활성 접속·세션 목록·마지막 접속 시각은 바뀌지 않고, 비밀번호도 저장하지 않는다.
    /// <paramref name="password"/> 가 비면 저장된 비밀번호를 쓰고, 그것도 없으면 <see cref="ConnectStatus.PasswordRequired"/>.
    /// </summary>
    Task<ConnectionTestOutcome> TestConnectionAsync(ConnectionProfile profile, string? password, CancellationToken ct);

    /// <summary>
    /// 프로필을 복제해 저장한다. 이름 뒤에 「 (사본)」 을 붙이고(겹치면 「 (사본) (2)」 …), 비밀번호·마지막 접속 시각은 옮기지 않는다.
    /// 앱이 풀어 놓은 지갑 폴더(<c>wallets/{id}</c>)를 쓰면 사본의 폴더로 복사한다(원본을 지워도 사본이 접속되게).
    /// </summary>
    Task<ConnectionProfile> DuplicateProfileAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// 폴더 목록·접속의 폴더(<c>Group</c>)를 한 번에 바꿔 저장한다(<see cref="ConnectionFolders"/> 의 작업을 넘긴다).
    /// 비밀번호·안전 저장소 항목은 건드리지 않는다. 읽기 전용 파일이면 <see cref="ConnectionValidationException"/>.
    /// </summary>
    Task UpdateFoldersAsync(Func<ConnectionsDocument, ConnectionsDocument> edit, CancellationToken ct);
}
