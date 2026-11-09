using System.Data.Common;
using System.Globalization;
using Oranda.Core.Sessions;

namespace Oranda.Tibero.Sessions;

/// <summary>
/// 세션 연결로 질의 하나를 보내 행 목록을 받는 도우미(Oracle <c>SchemaQuery</c> 대응, T3~T5 계약 S5).
/// 메타데이터·DB 정보·모니터·튜닝처럼 「짧은 질의 → 표」 가 필요한 기능이 쓴다. 사용자 SQL 실행(결과 격자·페이지)은 실행 서비스 몫이다.
/// <list type="bullet">
/// <item>세션의 명령 잠금(<see cref="TiberoSession.CommandGate"/>, D-041)을 쥔 동안에만 보내고, 다 읽은 뒤 놓는다. 재진입하지 않는다 —
/// 잠금을 쥔 흐름 안에서 부르면 교착이다.</item>
/// <item>드라이버의 <c>*Async</c> 는 동기로 돈다(tb-exec 실측) → 명령·읽기를 스레드 풀에서 돌린다(부른 UI 스레드를 막지 않게).</item>
/// <item>바인드는 이름 바인드다(<see cref="TiberoSession.CreateCommand"/>). 값은 <see cref="Bind"/> 로 넣는다.</item>
/// <item>끊김 예외면 세션을 Broken 으로 돌리고(<see cref="TiberoSession.ReportFailure"/>) 그대로 던진다. 그 밖의 서버 오류도 그대로 던진다 —
/// 「볼 수 없음」 판단은 호출자가 <c>TiberoErrorClassifier.FromException(ex).IsNoAccess()</c> 로 한다.</item>
/// <item>SQL 문장은 카탈로그(<c>ISqlCatalog</c>, dbms: tibero)에서 꺼내 넘기는 것이 원칙이다(TC-03). 이 도우미는 문장 원문만 받는다.</item>
/// </list>
/// </summary>
internal static class TiberoSessionQuery
{
    /// <summary>질의 결과. 값은 드라이버가 준 .NET 값(NULL 은 null).</summary>
    internal sealed record Table(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

    /// <summary>
    /// 사전 뷰 앞머리: 사전 조회 권한(DBA 롤·SELECT ANY DICTIONARY)이 있으면 <c>dba</c>, 없으면 <c>all</c>(Oracle 과 같은 규칙).
    /// </summary>
    internal static string ViewPrefix(ISession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Server.Capabilities.CanQueryDictionary ? "dba" : "all";
    }

    /// <summary>
    /// <paramref name="sql"/> 을 보내 행을 모두(최대 <paramref name="maxRows"/>) 읽는다. <paramref name="bind"/> 로 매개변수를 넣는다.
    /// </summary>
    internal static async Task<Table> RunAsync(ISession session, string sql, Action<DbCommand>? bind = null,
                                               int maxRows = int.MaxValue, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        var tibero = session as TiberoSession ?? throw new ArgumentException("TiberoSession 이 아닌 세션은 쓸 수 없습니다.", nameof(session));

        await tibero.CommandGate.EnterAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadAsync(tibero, sql, bind, maxRows, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (tibero.ReportFailure(ex))
        {
            throw;
        }
        finally
        {
            tibero.CommandGate.Release();
        }
    }

    /// <summary>값 하나(첫 행 첫 열). 행이 없으면 null.</summary>
    internal static async Task<object?> ScalarAsync(ISession session, string sql, Action<DbCommand>? bind = null, CancellationToken ct = default)
    {
        var table = await RunAsync(session, sql, bind, 1, ct).ConfigureAwait(false);
        return table.Rows.Count == 0 || table.Columns.Count == 0 ? null : table.Rows[0][0];
    }

    /// <summary>이름 바인드 매개변수 하나를 더한다(<c>:name</c> 자리). null 은 DB NULL 로 보낸다.</summary>
    internal static void Bind(DbCommand command, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    internal static string? Str(object? v) => v switch
    {
        null => null,
        string s => s,
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    internal static long? Long(object? v) =>
        v is null ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    internal static DateTime? Date(object? v) => v switch
    {
        DateTime d => d,
        DateTimeOffset o => o.DateTime,
        _ => null,
    };

    private static async Task<Table> ReadAsync(TiberoSession session, string sql, Action<DbCommand>? bind, int maxRows, CancellationToken ct)
    {
        await using var command = session.CreateCommand(sql);
        bind?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var columns = new string[reader.FieldCount];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = reader.GetName(i);
        }

        var rows = new List<object?[]>();
        while (rows.Count < maxRows && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[columns.Length];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, ct).ConfigureAwait(false) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new Table(columns, rows);
    }
}
