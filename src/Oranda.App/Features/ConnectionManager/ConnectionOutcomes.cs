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


/// <summary>접속을 시도한 결과 종류.</summary>
public enum ConnectStatus
{
    Connected,

    /// <summary>저장된 비밀번호가 없다(저장 안 함). 물어서 다시 시도한다.</summary>
    PasswordRequired,

    /// <summary>비밀번호 만료(<see cref="DbErrorKind.PasswordExpired"/>, Oracle ORA-28001). 비밀번호 변경 대화상자를 띄운다(CONN-04).</summary>
    PasswordExpired,

    /// <summary>지원하지 않는 서버 버전. <see cref="ConnectOutcome.ServerVersion"/>·<see cref="ConnectOutcome.Reason"/> 를 보인다.</summary>
    Unsupported,

    Failed,
}

/// <summary>접속 시도 결과. 비밀번호는 담지 않는다.</summary>
public sealed record ConnectOutcome(ConnectStatus Status, string Message, ISession? Session = null, string? ServerVersion = null, string? Reason = null)
{
    public bool IsConnected => Status == ConnectStatus.Connected;
}

/// <summary>비밀번호 변경 시도 결과.</summary>
public sealed record PasswordChangeOutcome(bool Succeeded, string Message);

/// <summary>
/// 접속 시험 결과(SQL Developer 의 Test, DBeaver 의 Test Connection). 비밀번호는 담지 않는다.
/// <see cref="ConnectStatus.Connected"/> 면 서버 버전과 걸린 시간을, 실패면 서버 오류 번호(ORA-·TBR- 번호의 숫자)와 메시지를 담는다.
/// </summary>
public sealed record ConnectionTestOutcome(ConnectStatus Status, string Message, string? ServerVersion = null, TimeSpan Elapsed = default, int? ErrorNumber = null)
{
    public bool Succeeded => Status == ConnectStatus.Connected;
}
