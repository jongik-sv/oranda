using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.Core.Metadata;

/// <summary>
/// DB 정보 구역 읽기(Oracle·Tibero 공통). 구역 하나를 읽지 못해도 나머지는 보여 준다.
/// 원래 질의(V$ 뷰 등)가 실패하면 — 대개 V$ 뷰를 읽을 권한이 없는 일반 계정 — 권한 없이 볼 수 있는 대신 질의(SYS_CONTEXT 등)나
/// 접속 때 얻은 서버 정보로 채우고 「안내」 줄을 붙인다. 대신할 것도 없으면 「오류」 줄(권한 오류면 필요한 권한 안내도)로 채운다.
/// </summary>
public static partial class DatabaseInfoSections
{
    /// <summary>오류 구역의 열 이름. 화면은 이 열 하나뿐인 구역을 「읽지 못한 구역」 으로 센다.</summary>
    public const string ErrorColumn = "오류";

    /// <summary>대신 질의로 채운 구역의 안내 줄 첫 칸.</summary>
    public const string NoteKey = "안내";

    /// <summary>
    /// 구역 하나. <paramref name="Id"/> 가 null 이면 질의 없이 <paramref name="Unsupported"/> 한 줄을 보인다.
    /// <paramref name="FallbackId"/> 는 원래 질의가 실패했을 때 보낼 대신 질의, <paramref name="VersionFromSession"/> 은 그것마저 없거나 실패했을 때
    /// 접속 때 얻은 서버 버전(<see cref="ServerInfo.Version"/>)으로 채울지.
    /// </summary>
    public sealed record Plan(string Title, string? Id, bool Transpose, string? FallbackId = null, bool VersionFromSession = false, string? Unsupported = null);

    /// <summary>질의 결과(열 이름·행).</summary>
    public sealed record Table(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

    /// <summary>카탈로그 id 하나를 보내 행을 모두 읽는다.</summary>
    public delegate Task<Table> Runner(string id, CancellationToken ct);

    public static async Task<IReadOnlyList<DetailSection>> LoadAsync(ISession s, IReadOnlyList<Plan> plan, Runner run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(run);
        var sections = new List<DetailSection>();
        foreach (var p in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (p.Id is null)
            {
                sections.Add(new DetailSection(p.Title, [NoteKey], [[p.Unsupported]]));
                continue;
            }

            try
            {
                sections.Add(Shape(p.Title, await run(p.Id, ct).ConfigureAwait(false), p.Transpose, note: null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) // 구역 하나의 실패(권한 부족 등)는 그 구역만 대신 값이나 「오류」 줄로 보이고 나머지 구역은 계속 읽는다.
            {
                sections.Add(await FallbackAsync(s, p, ex, run, ct).ConfigureAwait(false));
            }
        }

        return sections;
    }

    private static async Task<DetailSection> FallbackAsync(ISession s, Plan p, Exception failure, Runner run, CancellationToken ct)
    {
        var note = Note(s.Server.Dbms, failure);
        if (p.FallbackId is not null)
        {
            try
            {
                return Shape(p.Title, await run(p.FallbackId, ct).ConfigureAwait(false), p.Transpose, note);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) // 대신 질의도 실패하면 아래(접속 정보·오류 줄)로 넘어간다. 보이는 오류는 원래 질의의 것.
            {
            }
        }

        if (p.VersionFromSession && !string.IsNullOrWhiteSpace(s.Server.Version))
        {
            var banner = $"{s.Server.Dbms} {s.Server.Version}";
            return new DetailSection(p.Title, ["항목", "값"], [["version", banner], [NoteKey, note]]);
        }

        var rows = new List<IReadOnlyList<object?>> { new object?[] { Message(failure) } };
        if (IsPrivilegeError(failure))
        {
            rows.Add([PrivilegeHint(s.Server.Dbms)]);
        }

        return new DetailSection(p.Title, [ErrorColumn], rows);
    }

    /// <summary>결과를 구역으로: 한 행짜리는 「항목·값」 표로 세우고, 안내가 있으면 끝에 붙인다.</summary>
    private static DetailSection Shape(string title, Table table, bool transpose, string? note)
    {
        if (transpose)
        {
            var rows = new List<IReadOnlyList<object?>>();
            if (table.Rows.Count > 0)
            {
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    rows.Add([table.Columns[i].ToLowerInvariant(), table.Rows[0][i]]);
                }
            }

            if (note is not null)
            {
                rows.Add([NoteKey, note]);
            }

            return new DetailSection(title, ["항목", "값"], rows);
        }

        var list = new List<IReadOnlyList<object?>>(table.Rows.Select(r => (IReadOnlyList<object?>)r));
        if (note is not null && table.Columns.Count > 0)
        {
            var row = new object?[table.Columns.Count];
            row[0] = $"({NoteKey}) {note}";
            list.Add(row);
        }

        return new DetailSection(title, table.Columns, list);
    }

    private static string Message(Exception ex) => ex.Message.ReplaceLineEndings(" ");

    /// <summary>원래 질의가 실패해 대신 값을 보일 때의 안내.</summary>
    internal static string Note(Dbms dbms, Exception failure) =>
        IsPrivilegeError(failure)
            ? $"이 계정은 사전 뷰(V$ 등)를 읽을 권한이 없어 권한 없이 볼 수 있는 값만 보입니다. {PrivilegeHint(dbms)}"
            : $"원래 질의가 실패해 권한 없이 볼 수 있는 값만 보입니다({Code(failure) ?? Message(failure)}).";

    /// <summary>전체 정보를 보는 데 필요한 권한 안내.</summary>
    internal static string PrivilegeHint(Dbms dbms) => dbms == Dbms.Tibero
        ? "전체를 보려면 SELECT ANY DICTIONARY(또는 SELECT ANY TABLE) 권한이 필요합니다."
        : "전체를 보려면 SELECT_CATALOG_ROLE 역할이나 SELECT ANY DICTIONARY 권한이 필요합니다.";

    /// <summary>
    /// 뷰가 보이지 않거나 권한이 없다는 서버 오류인지. 권한 없는 뷰는 「없는 객체」 로 답한다:
    /// Oracle ORA-00942(없음)·ORA-01031(권한 없음), Tibero 8033(없음)·17004(권한 없음).
    /// </summary>
    internal static bool IsPrivilegeError(Exception ex) => PrivilegeCode().IsMatch(ex.Message);

    private static string? Code(Exception ex)
    {
        var m = AnyCode().Match(ex.Message);
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"\b(ORA-0*942|ORA-0*1031|(ODP|TBR)-8033|(ODP|TBR)-17004)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrivilegeCode();

    [GeneratedRegex(@"\b(ORA|ODP|TBR)-\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnyCode();
}
