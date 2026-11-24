using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Localization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Security;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>컴파일을 막는 세션(<c>plsql.compile_blocker</c> 의 한 행). 서버가 막는 세션을 모르면 <see cref="Sid"/> 가 null.</summary>
public sealed record CompileBlocker(int? Sid, int? Serial, string? User, string? Program, string? Machine, string? RunningObject);

/// <summary>막는 세션 찾기 결과: 찾았으면 <see cref="Blocker"/>, 못 찾았으면(권한 없음·연결 못 엶·읽기 실패) <see cref="Unknown"/> 에 이유.</summary>
public sealed record CompileBlockerResult(CompileBlocker? Blocker, string? Unknown);

/// <summary>
/// 컴파일(CREATE)이 끝나지 않을 때 그 세션을 막는 세션을 찾는다(PHC-8). 기다리는 세션은 명령이 끝나기 전에는 다른 문장을 보낼 수 없으므로
/// 반드시 다른 연결에서 읽는다.
/// </summary>
public interface ICompileBlockerProbe
{
    /// <summary><paramref name="waiting"/> 세션(SID = <paramref name="sid"/>)을 막는 세션을 찾는다. 예외를 던지지 않는다(취소만 전파).</summary>
    Task<CompileBlockerResult> FindAsync(ISession waiting, int sid, CancellationToken ct);
}

/// <summary>
/// 앱 구현: 같은 접속 프로필의 열린 모니터 세션(<see cref="SessionPurpose.Monitor"/>, 세션 모니터 등)이 있으면 그것을 빌려 쓰고,
/// 없으면 저장된 비밀번호로 모니터 세션을 짧게 열어 <c>plsql.compile_blocker</c> 를 읽은 뒤 닫는다. 컴파일 중에 비밀번호를 묻지 않는다(없으면 「모름」).
/// Oracle 만(이 DBMS 에 문장이 없으면 「모름」).
/// </summary>
internal sealed class SessionCompileBlockerProbe(IServiceProvider services) : ICompileBlockerProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary><c>plsql.compile_blocker</c> 의 열 순서(결과 집합이 열 이름을 주지 않을 때).</summary>
    private static readonly string[] CatalogColumns =
    [
        "waiting_sid", "waiting_event", "waiting_seconds", "blocking_session_status", "blocking_instance", "blocking_sid", "blocking_serial",
        "blocking_username", "blocking_program", "blocking_machine", "blocking_status", "blocking_event", "blocking_sql_id", "running_object",
    ];

    public async Task<CompileBlockerResult> FindAsync(ISession waiting, int sid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        var sql = CatalogRouting.Text(services, waiting, "plsql.compile_blocker");
        if (sql is null || services.GetService(typeof(IExecutionService)) is not IExecutionService exec)
        {
            return new CompileBlockerResult(null, UiText.Get("Plsql.compile.blockerNoQuery", "이 서버에서는 막는 세션을 찾지 않습니다"));
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        ISession? opened = null;
        try
        {
            var monitor = BorrowMonitor(waiting);
            if (monitor is null)
            {
                opened = await OpenMonitorAsync(waiting.Profile, limit.Token).ConfigureAwait(false);
                if (opened is null)
                {
                    return new CompileBlockerResult(null, UiText.Get("Plsql.compile.blockerNoPassword", "다른 연결을 열 저장된 비밀번호가 없음"));
                }

                monitor = opened;
            }

            var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT", BindNames = ["sid"] };
            var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase) { ["sid"] = new BindValue(BindDataType.Number, sid) };
            var r = await exec.ExecuteAsync(monitor, stmt, binds, new ExecutionOptions { Source = HistorySource.Editor, SkipHistory = true }, limit.Token).ConfigureAwait(false);
            if (r.ResultSet is not { } rows)
            {
                return new CompileBlockerResult(null, r.Error.IsNoAccess()
                    ? UiText.Format("Plsql.compile.blockerNoAccess", "V$SESSION·DBA_OBJECTS 를 읽을 권한이 없음: {0}", r.Error!.Code)
                    : r.Error is { } e ? $"{e.Code} {e.Message}" : r.Status.ToString());
            }

            await using (rows.ConfigureAwait(false))
            {
                var page = await rows.GetPageAsync(0, limit.Token).ConfigureAwait(false);
                if (page.RowCount == 0)
                {
                    return new CompileBlockerResult(null, UiText.Get("Plsql.compile.blockerNotWaiting", "컴파일 세션이 지금 기다리고 있지 않음"));
                }

                var names = rows.Columns.Count > 0 ? rows.Columns.Select(c => c.Name).ToList() : [.. CatalogColumns];
                object? Cell(string name)
                {
                    var i = names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 && i < page.Columns.Count && page.Columns[i].Count > 0 && page.Columns[i][0] is { } v and not DBNull ? v : null;
                }

                return new CompileBlockerResult(ToBlocker(Cell), null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CompileBlockerResult(null, UiText.Get("Plsql.compile.blockerTimeout", "다른 연결에서 제때 답이 없음"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CompileBlockerResult(null, ex.Message);
        }
        finally
        {
            if (opened is not null)
            {
                _ = TimedClose.RunAsync(() => opened.DisposeAsync(), "막는 세션 찾기 연결").ContinueWith(
                    static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }

    /// <summary>열 이름으로 값을 꺼내 막는 세션을 만든다(시험이 쓴다).</summary>
    internal static CompileBlocker ToBlocker(Func<string, object?> cell)
    {
        static int? Int(object? v) => v is null ? null : Convert.ToInt32(v, CultureInfo.InvariantCulture);
        static string? Str(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture) is { Length: > 0 } s ? s : null;
        return new CompileBlocker(Int(cell("blocking_sid")), Int(cell("blocking_serial")), Str(cell("blocking_username")),
            Str(cell("blocking_program")), Str(cell("blocking_machine")), Str(cell("running_object")));
    }

    /// <summary>같은 프로필의 열린 모니터 세션(다른 기능이 연 것, 닫지 않는다).</summary>
    private ISession? BorrowMonitor(ISession waiting) =>
        (services.GetService(typeof(ISessionManager)) as ISessionManager)?.Sessions
            .FirstOrDefault(s => s.Purpose == SessionPurpose.Monitor && s.Profile.Id == waiting.Profile.Id && s.State == SessionState.Idle);

    private async Task<ISession?> OpenMonitorAsync(ConnectionProfile profile, CancellationToken ct)
    {
        if (services.GetService(typeof(ISessionManager)) is not ISessionManager sessions
            || profile.CredentialRef is not { } reference
            || services.GetService(typeof(ICredentialStore)) is not ICredentialStore store
            || await store.LoadAsync(reference).ConfigureAwait(false) is not { } bytes)
        {
            return null;
        }

        var secret = Encoding.UTF8.GetString(bytes);
        Array.Clear(bytes);
        if (secret.Length == 0)
        {
            return null;
        }

        using var credential = new SecureCredential(secret);
        return await sessions.OpenAsync(profile, credential, SessionPurpose.Monitor, ct).ConfigureAwait(false);
    }
}
