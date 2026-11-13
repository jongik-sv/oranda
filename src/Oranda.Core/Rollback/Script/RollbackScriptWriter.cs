using System.Globalization;
using System.Text;

namespace Oranda.Core.Rollback.Script;

/// <summary>
/// 원복 스크립트 글 만들기(되돌리기 지점 설계 6.7, RB-03). 파일 글은 저장하지 않고 열 때마다 원장 문서에서 만든다
/// (머리 주석의 상태가 늘 지금 상태와 맞게). 문장은 실행한 순서의 역순으로 쓴다.
/// </summary>
public static class RollbackScriptWriter
{
    /// <summary>WHERE 의 AND 사슬을 이 글자 수에서 줄을 바꾼다(시안 화면 2 의 모양).</summary>
    public const int WrapWidth = 80;

    /// <summary>「확인 필요」 묶음의 경고(설계 7.3).</summary>
    public const string UnverifiedWarning =
        "트랜잭션이 Oranda 가 모르는 방법으로 끝났습니다(PL/SQL 안 COMMIT·ROLLBACK, 접속 닫힘). 롤백된 트랜잭션이면 원복 실행이 지우지 않은 행을 다시 넣을 수 있습니다.";

    /// <summary>상태 → 머리 주석 글(설계 11.2).</summary>
    public static string StateText(RollbackScriptState state) => state switch
    {
        RollbackScriptState.Confirmed => "확정",
        RollbackScriptState.Incomplete => "불완전",
        RollbackScriptState.Invalid => "무효",
        RollbackScriptState.NotCreated => "만들지 않음",
        RollbackScriptState.Unverified => "확인 필요",
        _ => "열림",
    };

    /// <summary>
    /// 묶음과 문서로 스크립트 글을 만든다. 문장 요약은 묶음의 <see cref="RollbackStatementInfo.Summary"/>(가린 글)를 쓰고, 없으면 문서의 글 첫 줄을 줄여 쓴다.
    /// </summary>
    /// <param name="group">묶음(머리 주석)</param>
    /// <param name="document">암호화 파일의 평문(문장·원복 문장)</param>
    /// <param name="zone">시각을 보일 시간대. null 이면 이 PC 의 시간대</param>
    public static string Write(RollbackGroup group, RollbackScriptDocument document, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(document);
        zone ??= TimeZoneInfo.Local;
        var infos = group.Statements.ToDictionary(s => s.Seq);
        var statements = document.Statements.OrderBy(s => s.Seq).ToList();
        bool Invalid(RollbackScriptStatement s) => s.Invalidated || (infos.TryGetValue(s.Seq, out var i) && i.Invalidated);
        var stepCount = statements.Where(s => !Invalid(s)).Sum(s => s.Steps.Count);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"-- Oranda 원복 스크립트 #{group.Id} · 접속 {OneLine(group.ProfileName)} · 상태: {StateText(group.State)}").Append('\n');
        sb.Append("-- ").Append(EndText(group, zone))
          .Append(CultureInfo.InvariantCulture, $" · 실행한 문장 {statements.Count}개 · 원복 문장 {stepCount}개").Append('\n');
        sb.Append("-- 실행한 순서의 역순입니다. 그 뒤 다른 사람이 바꾼 행은 0행으로 끝납니다.").Append('\n');
        if (group.State == RollbackScriptState.Unverified)
        {
            sb.Append("-- 경고: ").Append(UnverifiedWarning).Append('\n');
        }

        for (var i = statements.Count - 1; i >= 0; i--)
        {
            var s = statements[i];
            infos.TryGetValue(s.Seq, out var info);
            var summary = info?.Summary is { Length: > 0 } given ? OneLine(given) : Summarize(s.Text);
            var rows = s.RowsAffected is { } n ? $" · {n.ToString("N0", CultureInfo.InvariantCulture)}행" : string.Empty;
            sb.Append(CultureInfo.InvariantCulture, $"-- [{i + 1}/{statements.Count}] {Time(s.ExecutedAt, zone, "HH:mm:ss")} {summary}{rows}").Append('\n');
            if (Invalid(s))
            {
                sb.Append("-- 무효: ").Append(info?.Reason is { Length: > 0 } why ? OneLine(why) : "저장점까지 롤백함").Append('\n');
                continue;
            }

            // 상태로 글을 고른다: 「만들지 않음」 문장의 이유(예: MERGE)는 문서의 IncompleteReason 칸에 실려 오지만 「불완전」이 아니다.
            if (info?.State == RollbackScriptState.NotCreated && s.Steps.Count == 0)
            {
                var why = info.Reason is { Length: > 0 } r ? r : s.IncompleteReason;
                sb.Append("-- 원복 문장 없음").Append(why is { Length: > 0 } ? ": " + OneLine(why) : string.Empty).Append('\n');
            }
            else if (s.IncompleteReason is { Length: > 0 } incomplete)
            {
                sb.Append("-- 불완전: ").Append(OneLine(incomplete)).Append('\n');
            }

            foreach (var step in s.Steps)
            {
                if (step.Note is { Length: > 0 } note)
                {
                    sb.Append("-- ").Append(OneLine(note)).Append('\n');
                }

                sb.Append(Format(step.Sql)).Append(";\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 원복 문장 한 줄 → 사람이 읽는 모양: WHERE·VALUES 앞에서 줄을 바꾸고, WHERE 의 AND 사슬은 <see cref="WrapWidth"/> 에서 <c>   AND</c> 로 잇는다.
    /// </summary>
    public static string Format(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var values = RollbackSqlText.IndexOfTop(sql, " VALUES (");
        if (sql.StartsWith("INSERT ", StringComparison.Ordinal) && values > 0)
        {
            return sql[..values] + "\n" + sql[values..];
        }

        var where = RollbackSqlText.IndexOfTop(sql, " WHERE ");
        if (where < 0)
        {
            return sql;
        }

        var preds = RollbackSqlText.SplitTop(sql[(where + 7)..], " AND ");
        var lines = new List<string>();
        var line = " WHERE " + preds[0];
        foreach (var p in preds.Skip(1))
        {
            var candidate = line + " AND " + p;
            if (candidate.Length > WrapWidth)
            {
                lines.Add(line);
                line = "   AND " + p;
            }
            else
            {
                line = candidate;
            }
        }

        lines.Add(line);
        return sql[..where] + "\n" + string.Join("\n", lines);
    }

    /// <summary>문장 글을 한 줄로(공백 줄임, 80자에서 「…」).</summary>
    public static string Summarize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var one = OneLine(text);
        return one.Length <= 80 ? one : one[..79] + "…";
    }

    /// <summary>
    /// 주석 줄에 넣는 글: 줄바꿈·공백을 공백 하나로 줄인다. 이유·요약에 줄바꿈이 있으면 그 뒤가 주석 밖의 SQL 이 되어 「원복 실행」 때 실행되므로 반드시 거친다.
    /// </summary>
    public static string OneLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string EndText(RollbackGroup group, TimeZoneInfo zone)
    {
        if (group.EndedAt is not { } ended)
        {
            return "열림(트랜잭션이 끝나지 않음)";
        }

        var time = Time(ended, zone, "yyyy-MM-dd HH:mm:ss");
        return group.EndKind switch
        {
            TransactionEndKind.Commit => group.CommitPoint is { } p ? $"커밋 {time} ({p.Text})" : $"커밋 {time}",
            TransactionEndKind.Rollback => $"롤백 {time}",
            _ => group.CommitPoint is { } p ? $"끝 {time} ({p.Text}, 결과 모름)" : $"끝 {time} (결과 모름)",
        };
    }

    private static string Time(DateTimeOffset at, TimeZoneInfo zone, string format) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString(format, CultureInfo.InvariantCulture);
}
