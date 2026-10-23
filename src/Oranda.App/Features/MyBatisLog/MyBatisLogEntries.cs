using System.Text.RegularExpressions;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisLog;

/// <summary>
/// 로그에서 읽은 문장 하나(TSK-03-06). <see cref="Logger"/> 는 Preparing 줄의 logger 이름(<c>namespace.id</c>), 없으면 null.
/// <see cref="RawSql"/> 은 <c>?</c> 가 그대로인 Preparing 문장, <see cref="Params"/> 는 <see cref="MyBatisLogParser"/> 가 읽은 값(이름 <c>p1..</c>, 이 문장 안에서 번호).
/// <see cref="Text"/> 는 이 문장에 해당하는 로그 원문(일반 SQL 탭으로 붙여 넣을 때 쓴다).
/// </summary>
public sealed record MyBatisLogEntry(string? Logger, string RawSql, IReadOnlyList<MyBatisLogParam> Params, string Text);

/// <summary>
/// 로그 글을 Preparing 줄마다 쪼갠다. 값 나누기는 <see cref="MyBatisLogParser"/> 를 다시 쓰고, 여기서는 그 파서가 주지 않는 logger 이름과
/// <c>?</c> 가 남은 원문 SQL 만 더한다.
/// </summary>
public static partial class MyBatisLogEntries
{
    private const string PreparingMarker = "Preparing:";

    // `… DEBUG com.acme.hr.EmpMapper.selectEmpList - ==>  Preparing:` / `….selectEmpList:110 ==>  Preparing:` 둘 다 받는다.
    [GeneratedRegex(@"(?<name>[A-Za-z_$][\w$]*(?:\.[\w$]+)+)(?::\d+)?\s+(?:-\s+)?==>\s*Preparing:", RegexOptions.CultureInvariant)]
    private static partial Regex LoggerPattern();

    public static IReadOnlyList<MyBatisLogEntry> Split(string logText)
    {
        ArgumentNullException.ThrowIfNull(logText);
        var lines = logText.Split('\n');
        var starts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(PreparingMarker, StringComparison.Ordinal))
            {
                starts.Add(i);
            }
        }

        var result = new List<MyBatisLogEntry>(starts.Count);
        for (int s = 0; s < starts.Count; s++)
        {
            int end = s + 1 < starts.Count ? starts[s + 1] : lines.Length;
            var segment = string.Join('\n', lines[starts[s]..end]);
            var parsed = MyBatisLogParser.Parse(segment);
            if (parsed.Count == 0)
            {
                continue;
            }

            var head = lines[starts[s]].TrimEnd('\r');
            var raw = head[(head.IndexOf(PreparingMarker, StringComparison.Ordinal) + PreparingMarker.Length)..].Trim();
            var match = LoggerPattern().Match(head);
            result.Add(new MyBatisLogEntry(match.Success ? match.Groups["name"].Value : null, raw, parsed[0].Params, segment));
        }

        return result;
    }
}
