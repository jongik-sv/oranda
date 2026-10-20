using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>
/// 세션·SQL 모니터 창이 보이는 접속 상태: 권한 없음 안내 띠(이유·GRANT 문)와 「모니터 접속: …」 표시.
/// 메뉴를 끄는 대신 창을 열어 이유와 해결 방법을 보인다(접속 점검 <c>CanQueryPerformanceViews</c> = false 이거나 실행 중 ORA-00942·ORA-01031).
/// </summary>
public sealed partial class MonitorAccessState : ObservableObject
{
    /// <summary>안내 띠 문구. 없으면 null(띠를 숨김).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    /// <summary>「SQL 복사」 단추가 복사할 문. 안내 띠와 함께 켜진다.</summary>
    [ObservableProperty]
    private string? _grantSql;

    /// <summary>창 머리에 보일 「모니터 접속: ft-dba(SYSTEM)」. 세션이 없으면 빈 문자열.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccount))]
    private string _accountText = string.Empty;

    /// <summary>모니터 접속이 운영(Production) 프로필이면 true(머리 표시를 환경 색으로).</summary>
    [ObservableProperty]
    private bool _isProduction;

    public bool HasNotice => Notice is not null;

    public bool HasAccount => AccountText.Length > 0;

    /// <summary><paramref name="profile"/> 계정이 V$ 뷰를 읽지 못한다는 안내 띠를 켠다. 계정 이름은 프로필의 실제 값.</summary>
    public void ShowDenied(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        GrantSql = GrantStatement(profile.User);
        Notice = $"이 접속({profile.User})은 V$ 뷰를 읽을 권한이 없어 모니터를 쓸 수 없습니다. "
            + $"DBA 가 `{GrantSql}` (또는 SELECT ANY DICTIONARY)를 주거나, 권한 있는 다른 접속으로 여세요.";
    }

    /// <summary>안내 띠를 지운다.</summary>
    public void ClearNotice()
    {
        Notice = null;
        GrantSql = null;
    }

    /// <summary>머리의 「모니터 접속」 표시를 맞춘다. null 이면 비운다.</summary>
    public void SetAccount(ConnectionProfile? profile)
    {
        IsProduction = profile?.Environment == ConnectionEnvironment.Production;
        AccountText = profile is null
            ? string.Empty
            : $"모니터 접속: {profile.Name}({profile.User})" + (IsProduction ? " · 운영" : string.Empty);
    }

    /// <summary><c>GRANT SELECT_CATALOG_ROLE TO &lt;계정&gt;;</c>. 단순 식별자는 대문자로, 그 밖은 큰따옴표로 감싼다.</summary>
    public static string GrantStatement(string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        var name = SimpleIdentifier().IsMatch(user)
            ? user.ToUpperInvariant()
            : "\"" + user.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return $"GRANT SELECT_CATALOG_ROLE TO {name};";
    }

    /// <summary>예외가 V$ 뷰 권한 부족(ORA-00942 테이블·뷰 없음, ORA-01031 권한 부족)인지.</summary>
    public static bool IsPrivilegeError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message;
            if (m.Contains("ORA-00942", StringComparison.Ordinal) || m.Contains("ORA-01031", StringComparison.Ordinal)
                || (m.Contains("table or view", StringComparison.OrdinalIgnoreCase) && m.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_$#]*$")]
    private static partial Regex SimpleIdentifier();
}

/// <summary>모니터별로 마지막에 고른 접속 프로필 기억(다음에 열 때 기본값). 비밀번호는 다루지 않는다.</summary>
public interface IMonitorProfileMemory
{
    Guid? Recall(string monitorId);

    void Remember(string monitorId, Guid profileId);
}

/// <summary>데이터 폴더의 작은 JSON 파일(<c>monitor-profiles.json</c>)에 기억을 둔다. 읽기·쓰기 실패는 무시한다(기억이 없을 뿐).</summary>
public sealed class MonitorProfileMemory(string path) : IMonitorProfileMemory
{
    private readonly object _gate = new();

    public Guid? Recall(string monitorId)
    {
        lock (_gate)
        {
            return Read().TryGetValue(monitorId, out var id) && id != Guid.Empty ? id : null;
        }
    }

    public void Remember(string monitorId, Guid profileId)
    {
        lock (_gate)
        {
            var map = Read();
            map[monitorId] = profileId;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(map), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 기억하지 못할 뿐 모니터 동작에는 영향이 없다.
            }
        }
    }

    private Dictionary<string, Guid> Read()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Guid>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// 모니터 전용 세션(<see cref="SessionPurpose.Monitor"/>)을 어느 프로필로 어떻게 열지 정한다.
/// 기본은 작업 세션의 프로필(권한이 없고 기억해 둔 다른 프로필이 있으면 그쪽), 작업 세션이 없으면 프로필 고르기, 「다른 접속으로 열기」는 늘 프로필 고르기.
/// 비밀번호는 안전 저장소에서 꺼내고 없으면 묻는다. 메인 작업 접속은 건드리지 않는다.
/// </summary>
public sealed class MonitorSessionOpener(
    IConnectionService connection,
    ISessionManager sessions,
    ICredentialStore? credentials,
    IMonitorProfileMemory? memory,
    string monitorId)
{
    /// <summary>저장된 접속 목록과 지금 기본값을 보여 주고 하나를 고르게 한다. 취소하면 null. 없으면 고를 수 없다.</summary>
    public Func<IReadOnlyList<ConnectionProfile>, Guid?, Task<ConnectionProfile?>>? PickProfile { get; set; }

    /// <summary>저장된 비밀번호가 없을 때 묻는다. 취소하면 null.</summary>
    public Func<ConnectionProfile, Task<string?>>? AskPassword { get; set; }

    /// <summary>모니터 창이 처음 열릴 때의 세션.</summary>
    public async Task<ISession?> OpenDefaultAsync(CancellationToken ct)
    {
        var work = connection.ActiveSession;
        if (work is not null && work.Server.Capabilities.CanQueryPerformanceViews)
        {
            return await OpenAsync(work.Profile, ct).ConfigureAwait(true);
        }

        if (memory?.Recall(monitorId) is { } remembered
            && (await connection.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles.FirstOrDefault(p => p.Id == remembered) is { } profile)
        {
            return await OpenAsync(profile, ct).ConfigureAwait(true);
        }

        return work is not null
            ? await OpenAsync(work.Profile, ct).ConfigureAwait(true)
            : await OpenChosenAsync(ct).ConfigureAwait(true);
    }

    /// <summary>「다른 접속으로 열기」: 프로필을 고르게 하고 그 프로필로 연다. 취소하면 null, 저장된 접속이 없으면 <see cref="InvalidOperationException"/>.</summary>
    public async Task<ISession?> OpenChosenAsync(CancellationToken ct)
    {
        var profiles = (await connection.LoadAsync(ct).ConfigureAwait(true)).Value.Profiles;
        if (profiles.Count == 0)
        {
            throw new InvalidOperationException("저장된 접속이 없습니다. 접속 관리자에서 접속을 먼저 만드세요.");
        }

        if (PickProfile is null)
        {
            return null;
        }

        var current = memory?.Recall(monitorId) ?? connection.ActiveSession?.Profile.Id;
        return await PickProfile(profiles, current).ConfigureAwait(true) is { } picked
            ? await OpenAsync(picked, ct).ConfigureAwait(true)
            : null;
    }

    /// <summary>「다른 접속으로 열기」로 연 프로필을 다음에 열 때의 기본값으로 기억한다.</summary>
    public void Remember(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        memory?.Remember(monitorId, profile.Id);
    }

    /// <summary>프로필로 모니터 전용 세션을 연다. 비밀번호를 얻지 못하면(취소) null.</summary>
    public async Task<ISession?> OpenAsync(ConnectionProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string? secret = null;
        if (profile.CredentialRef is { } reference && credentials is not null
            && await credentials.LoadAsync(reference).ConfigureAwait(true) is { } bytes)
        {
            secret = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
        }

        if (string.IsNullOrEmpty(secret) && AskPassword is not null)
        {
            secret = await AskPassword(profile).ConfigureAwait(true);
        }

        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        using var credential = new SecureCredential(secret);
        return await sessions.OpenAsync(profile, credential, SessionPurpose.Monitor, ct).ConfigureAwait(true);
    }
}
