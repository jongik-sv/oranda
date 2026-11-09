using Oranda.Core.Journal;

namespace Oranda.Core.Execution.Providers;

/// <summary><see cref="ChangeCapture"/> 가 서버에서 읽은 DDL 한 건. 실패면 <see cref="Ddl"/> 이 null 이고 오류 번호·문구가 있다.</summary>
public readonly record struct DdlRead(string? Ddl, string? Owner, int? ErrorNumber = null, string? ErrorMessage = null);

/// <summary>서버 DDL 읽기(Oracle: DBMS_METADATA). DBMS 공급자가 구현하고, 시험이 가짜로 바꿔 끼운다.</summary>
public interface IDdlSource
{
    /// <summary>
    /// 「객체 없음」을 뜻하는 <see cref="DdlRead.ErrorNumber"/>(Oracle: ORA-31603). 새 객체·본문 없는 패키지는 이 번호면 조용히 넘긴다.
    /// 객체는 있으나 읽을 수 없는 경우(권한 부족 등)는 반드시 <b>다른</b> 번호로 돌려준다(Oracle 은 1031 을 빌림) — 같은 번호면
    /// CREATE OR REPLACE 앞 보관 실패가 일지에 남지 않고 조용히 사라진다.
    /// </summary>
    int ObjectNotFoundNumber { get; }

    /// <summary>RENAME 처럼 문장에 종류가 없을 때 객체 종류를 찾는다. 없으면 null.</summary>
    Task<string?> ResolveTypeAsync(string? owner, string name, CancellationToken ct);

    /// <summary><paramref name="getDdlType"/> 은 DBMS_METADATA 형식 이름(<c>PACKAGE_BODY</c>). 오류는 예외가 아니라 결과로 돌려준다.</summary>
    Task<DdlRead> ReadAsync(string getDdlType, string? owner, string name, CancellationToken ct);

    /// <summary>실패 행 사유에 쓸 오류 표기(Oracle: <c>ORA-01031 문구</c>).</summary>
    string DescribeError(int number, string? message);
}

/// <summary>
/// 변경 일지 보관 규칙: 무엇을 읽을지(계획), 실패를 일지 행으로 바꾸는 방법, 제한 시간. DBMS 별 오류 번호·표기는 <see cref="IDdlSource"/> 가 준다.
/// </summary>
public static class ChangeCapture
{
    /// <summary>보관 전체의 제한 시간. 넘으면 일지에 시간 초과 행을 남기고 실행은 계속한다.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static string Summarize(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim() is { Length: > 0 } t)
            {
                return t.Length <= 200 ? t : t[..200];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 대상의 현재 DDL 을 읽어 일지 행으로 돌려준다. 새 객체를 만드는 CREATE OR REPLACE 처럼 객체가 아직 없으면(<see cref="IDdlSource.ObjectNotFoundNumber"/>) 행이 없다.
    /// 사용자 취소(<paramref name="ct"/>)는 예외로 전파하고, 제한 시간·서버 오류는 실패 행이 된다.
    /// </summary>
    public static async Task<IReadOnlyList<JournalEntry>> CaptureAsync(ChangeTarget target, JournalEntry template, string fallbackOwner,
                                                                        IDdlSource source, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var rows = new List<JournalEntry>();
        var type = target.ObjectType;
        try
        {
            if (type is null)
            {
                type = await source.ResolveTypeAsync(target.Owner, target.Name, limit.Token);
                if (type is null)
                {
                    ct.ThrowIfCancellationRequested();
                    rows.Add(Row(template, target.Owner ?? fallbackOwner, target.Name, "OBJECT", null, "객체를 찾지 못했다"));
                    return rows;
                }
            }

            foreach (var (journalType, getDdlType, optional) in Plan(type, target.Operation))
            {
                var read = await source.ReadAsync(getDdlType, target.Owner, target.Name, limit.Token);
                ct.ThrowIfCancellationRequested();
                if (limit.IsCancellationRequested)
                {
                    throw new OperationCanceledException(limit.Token);
                }

                var owner = read.Owner ?? target.Owner ?? fallbackOwner;
                if (!string.IsNullOrEmpty(read.Ddl))
                {
                    rows.Add(Row(template, owner, target.Name, journalType, read.Ddl, null));
                }
                else if (read.ErrorNumber == source.ObjectNotFoundNumber && (optional || target.Operation == ChangeOperation.CreateOrReplace))
                {
                    continue; // 새 객체(보관할 이전 DDL 없음) 또는 본문이 없는 패키지.
                }
                else
                {
                    var reason = read.ErrorNumber is { } n ? source.DescribeError(n, read.ErrorMessage) : "서버가 DDL 을 돌려주지 않았다";
                    rows.Add(Row(template, owner, target.Name, journalType, null, reason));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            rows.Add(Row(template, target.Owner ?? fallbackOwner, target.Name, type ?? "OBJECT", null,
                $"시간 초과({timeout.TotalSeconds:0}초)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 가짜·드라이버가 던진 예상 밖 오류도 실행을 막지 못한다. 그때까지 모은 행은 버리고 실패 한 줄만 남긴다.
            rows.Clear();
            rows.Add(Row(template, target.Owner ?? fallbackOwner, target.Name, type ?? "OBJECT", null, $"보관 중 오류: {ex.Message}"));
        }

        return rows;
    }

    /// <summary>
    /// 문장 종류 → (일지 객체 종류, DBMS_METADATA 형식, 없어도 되는지). PACKAGE·TYPE 은 명세와 본문이 따로 버전이다:
    /// CREATE OR REPLACE PACKAGE 는 명세만 바꾸고, DROP·ALTER 는 본문까지 영향을 준다.
    /// 형식 이름이 다른 DBMS 는 <see cref="IDdlSource.ReadAsync"/> 안에서 바꾼다.
    /// </summary>
    public static IEnumerable<(string JournalType, string GetDdlType, bool Optional)> Plan(string type, ChangeOperation operation)
    {
        switch (type)
        {
            case "PACKAGE" or "TYPE":
                yield return (type, type + "_SPEC", false);
                if (operation != ChangeOperation.CreateOrReplace)
                {
                    yield return (type + " BODY", type + "_BODY", true);
                }

                break;
            case "DATABASE LINK":
                yield return (type, "DB_LINK", false);
                break;
            default:
                yield return (type, type.Replace(' ', '_'), false);
                break;
        }
    }

    private static JournalEntry Row(JournalEntry template, string owner, string name, string type, string? ddl, string? failure)
        => template with { CapturedAt = DateTimeOffset.UtcNow, Owner = owner, Name = name, ObjectType = type, PreviousDdl = ddl, FailureReason = failure };
}
