using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>실행 확인 대화상자의 한 줄: 대상 계정 하나에 쓰는 표 수와 대상 처리 종류.</summary>
public sealed record RunOwnerLine(string Owner, int Tables, string Actions, bool IsSessionUser);

/// <summary>
/// 「복사 실행」 확인(결정 B-2 a): 대상 계정이 둘 이상이거나 대상 접속 계정이 아닌 계정에 쓰면 계정별로 지우고 넣을 표 수·처리 종류를 보이고 묻는다.
/// 기본 단추는 취소다. 그 밖(접속 계정 하나에만 쓰는 작업)에는 묻지 않는다.
/// </summary>
public sealed record RunConfirmation(string SessionUser, IReadOnlyList<RunOwnerLine> Owners)
{
    /// <summary>물어야 하면 확인 내용, 아니면 null. <paramref name="sessionUser"/> 는 대상 접속 계정.</summary>
    public static RunConfirmation? For(CopyJob job, string sessionUser)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(sessionUser);
        var groups = job.Tables.Where(r => r.Kind != RuleKind.Exclude)
            .GroupBy(r => CopyOwners.Target(job, r), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0 || (groups.Count == 1 && string.Equals(groups[0].Key, sessionUser, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var lines = groups.Select(g => new RunOwnerLine(
            g.Key,
            g.Count(),
            string.Join(", ", g.GroupBy(r => r.Action).Select(a => ActionText(a.Key) + " " + a.Count().ToString(System.Globalization.CultureInfo.InvariantCulture))),
            string.Equals(g.Key, sessionUser, StringComparison.OrdinalIgnoreCase))).ToList();
        return new RunConfirmation(sessionUser, lines);
    }

    /// <summary>대화상자 첫 줄.</summary>
    public string Header => Owners.Count > 1
        ? Fill(DataCopyText.L("대상 계정 {0}곳의 표를 지우고 넣습니다. 계속할까요?"), Owners.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : Fill(DataCopyText.L("대상 접속 계정({0})이 아닌 계정의 표를 지우고 넣습니다. 계속할까요?"), SessionUser);

    // 문구 표의 글(한/영)에 값 하나를 넣는다.
    private static string Fill(string template, string value) => template.Replace("{0}", value, StringComparison.Ordinal);

    /// <summary>계정 한 줄의 글: <c>APP2 · 표 3개 · 비우고 넣기 2, PK 기준 MERGE 1</c>(접속 계정이면 표시).</summary>
    public static string LineText(RunOwnerLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tables = Fill(DataCopyText.L("표 {0}개"), line.Tables.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var owner = line.IsSessionUser ? $"{line.Owner} ({DataCopyText.L("접속 계정")})" : line.Owner;
        return $"{owner} · {tables} · {line.Actions}";
    }

    private static string ActionText(TargetAction action) =>
        DataCopyConverters.ActionText.Convert(action, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? action.ToString();
}
