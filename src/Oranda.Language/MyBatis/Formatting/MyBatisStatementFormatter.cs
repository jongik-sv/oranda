using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Core.Text;
using Oranda.Language.Formatting;

namespace Oranda.Language.MyBatis.Formatting;

/// <summary>
/// MyBatis 문장 포매터. 바깥 여는·닫는 태그는 원문 그대로 두고 본문만 포맷한다. 본문을 스캐너(<see cref="MyBatisBodyScanner"/>)로
/// 나눠 XML 구간·<c>#{…}</c>·엔티티를 자리표시로 바꾼 SQL 을 일반 엔진(<see cref="FormatEngine"/>)에 한 번 넘기고,
/// 결과를 원문 철자로 복원한 뒤 태그 줄 들여쓰기를 고친다(<see cref="MyBatisTagLayout"/>).
/// FMT-11 의 MyBatis 판(토큰 열·XML 구간 열·붙은 경계·멱등)을 하나라도 어기면 원문을 그대로 두고 사유를 채운다.
/// 문장 끝 옵션은 늘 Keep 이다. 범위 인자는 쓰지 않는다.
/// </summary>
[OrandaService(typeof(IMyBatisStatementFormatter))]
internal sealed class MyBatisStatementFormatter : IMyBatisStatementFormatter
{
    public FormatResult Format(string text, FormatterOptions o, TextRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(o);
        return FormatStatement(text, o, checkIdempotent: true);
    }

    /// <summary>
    /// 한 번 포맷하고(<see cref="Pass"/>), 멱등을 확인한다. 여러 줄 요소가 포맷 뒤 한 줄로 접히면 다음 번에는 통째 구간으로
    /// 읽혀 배치가 달라질 수 있으므로, 결과가 더 바뀌지 않을 때까지(최대 몇 번) 되풀이한다. 되풀이한 결과마다 같은 검사를 통과해야 한다.
    /// </summary>
    internal static FormatResult FormatStatement(string text, FormatterOptions o, bool checkIdempotent)
    {
        var whole = new TextRange(0, text.Length);
        try
        {
            o = o with { StatementTerminator = StatementTerminator.Keep };
            var first = Pass(text, o, whole);
            if (!first.Succeeded || !checkIdempotent) return first;

            var cur = first;
            for (int i = 0; i < MaxPasses; i++)
            {
                var next = Pass(cur.Text, o, new TextRange(0, cur.Text.Length));
                if (!next.Succeeded)
                {
                    return new FormatResult(text, whole, FormatFailure.NotIdempotent) { Message = $"다시 포맷하면 실패합니다({next.Failure}): {next.Message}" };
                }

                if (next.Text == cur.Text) return new FormatResult(cur.Text, whole);
                cur = next;
            }

            return new FormatResult(text, whole, FormatFailure.NotIdempotent) { Message = "두 번 포맷한 결과가 달라서 되풀이해도 수렴하지 않습니다." };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new FormatResult(text, whole, FormatFailure.InternalError) { Message = ex.Message };
        }
    }

    const int MaxPasses = 4;

    /// <summary>한 번 포맷한다. 검사 (a)~(d) 를 통과해야 한다.</summary>
    static FormatResult Pass(string text, FormatterOptions o, TextRange whole)
    {
        FormatResult Fail(FormatFailure failure, string? message) => new(text, whole, failure) { Message = message };

        var outer = Outer.Parse(text, out string? outerError);
        if (outerError is not null) return Fail(FormatFailure.MarkupUnsupported, outerError);
        if (outer is null) return new FormatResult(text, whole);

        var scan = MyBatisBodyScanner.Scan(outer.Body);
        if (!scan.Ok) return Fail(FormatFailure.MarkupUnsupported, scan.Error);

        var ph = new MyBatisPlaceholder(outer.Body, scan.Segments);
        if (ph.Sql.Length == 0) return new FormatResult(text, whole);
        if (ph.HasForeignMarker()) return Fail(FormatFailure.MarkupUnsupported, "엔티티 등을 풀면 /*@ 표식 같은 글이 생깁니다.");

        var read = FormatTokenizer.Read(ph.Sql);
        if (read.Unterminated) return Fail(FormatFailure.MarkupUnsupported, "엔티티·자리표시를 반영한 본문에 닫히지 않은 문자열·주석이 있습니다.");
        int broken = ph.FirstBrokenMarker(read);
        if (broken > 0)
        {
            var seg = ph.SegmentOfMarker(broken);
            return Fail(FormatFailure.MarkupUnsupported, $"XML 구간이 문자열·주석 안에 들어 있거나 SQL*Plus 줄로 읽힙니다: {outer.Body.Substring(seg.Start, Math.Min(seg.Length, 40))}");
        }

        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var ind = o.Indent;
        string unit = ind.UseTabs ? "\t" : new string(' ', Math.Clamp(ind.Size, 1, 16));
        string baseIndent = outer.CloseIndent + unit;

        var run = FormatEngine.Run(ph.Sql, o, baseIndent, newline, read);
        if (run is null) return Fail(FormatFailure.InternalError, "본문을 포맷하지 못했습니다.");
        var failure = FormatEngine.Verify(run.Items, run.Text, out string? message);
        if (failure != FormatFailure.None) return Fail(failure, message);

        string layout = ph.AttachCommas(run.Text);
        layout = MyBatisTagLayout.Glue(layout, ph, outer.Body);
        layout = MyBatisTagLayout.IndentTagLines(layout, ph, baseIndent);
        failure = FormatEngine.Verify(run.Items, layout, out message);
        if (failure != FormatFailure.None) return Fail(failure, message);

        string? restored = ph.Restore(layout);
        if (restored is null) return Fail(FormatFailure.TokenMismatch, "포맷 결과를 원문 철자로 되돌리지 못했습니다.");

        // (b) 공백 아닌 글자 흐름이 원문 본문과 같다.
        if (!MyBatisPlaceholder.SameFlow(outer.Body, restored)) return Fail(FormatFailure.TokenMismatch, "본문의 글자 흐름이 달라졌습니다.");

        // 짝 없는 서로게이트: 이웃하던 서로게이트 쌍 사이에 공백이 끼면 렉서가 Error 토큰 둘로 읽는다.
        if (!HasLoneSurrogate(outer.Body) && HasLoneSurrogate(restored)) return Fail(FormatFailure.TokenMismatch, "서로게이트 쌍 사이에 공백이 생겼습니다.");

        string result = outer.Head + newline + restored + newline + outer.CloseIndent + outer.Close + outer.Tail;

        // (c)(d) XML 구간 열과 붙은 경계.
        var mismatch = CheckMarkup(outer.Body, scan, restored);
        if (mismatch is not null) return Fail(FormatFailure.MarkupMismatch, mismatch);

        if (result == text) return new FormatResult(text, whole);

        return new FormatResult(result, whole);
    }

    /// <summary>(c) 결과 본문을 스캐너로 다시 읽은 XML 구간 열(종류·원문 글·순서, 통째 구간으로 묶기 전 기준)이 같고, (d) 붙어 있던·떨어져 있던 경계가 그대로인가.</summary>
    internal static string? CheckMarkup(string before, BodyScan beforeScan, string after)
    {
        var rescan = MyBatisBodyScanner.Scan(after);
        if (!rescan.Ok) return $"결과 본문을 XML 로 다시 읽지 못했습니다: {rescan.Error}";

        var x = beforeScan.Fine.Where(s => s.IsMarkup).ToList();
        var y = rescan.Fine.Where(s => s.IsMarkup).ToList();
        if (x.Count != y.Count) return $"XML 구간 수가 달라졌습니다(원문 {x.Count}, 결과 {y.Count}).";
        for (int i = 0; i < x.Count; i++)
        {
            string a = before.Substring(x[i].Start, x[i].Length);
            string b = after.Substring(y[i].Start, y[i].Length);
            if (x[i].Kind != y[i].Kind || a != b) return $"XML 구간 {i + 1}번째가 달라졌습니다: {Brief(a)} ≠ {Brief(b)}.";
            if (MyBatisPlaceholder.GluedBefore(before, x[i]) != MyBatisPlaceholder.GluedBefore(after, y[i]))
            {
                return $"앞 글자와 XML 구간 사이의 붙은 경계가 달라졌습니다: {Brief(a)}.";
            }

            if (MyBatisPlaceholder.GluedAfter(before, x[i]) != MyBatisPlaceholder.GluedAfter(after, y[i]))
            {
                return $"뒤 글자와 XML 구간 사이의 붙은 경계가 달라졌습니다: {Brief(a)}.";
            }
        }

        return null;
    }

    internal static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return true;
        }

        return false;
    }

    static string Brief(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>문장을 바깥 여는 태그·본문·닫는 태그·뒤 글로 나눈 것.</summary>
    sealed record Outer(string Head, string Body, string Close, string Tail, string CloseIndent)
    {
        /// <summary>
        /// 나눈다. 스스로 닫힌 바깥 태그처럼 포맷할 본문이 없으면 null(오류 없음). 모양이 잘못됐으면 <paramref name="error"/> 에 사유.
        /// </summary>
        public static Outer? Parse(string text, out string? error)
        {
            error = null;
            int i = 0;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i + 1 >= text.Length || text[i] != '<' || !MyBatisBodyScanner.IsNameStart(text[i + 1]))
            {
                error = "문장이 여는 태그로 시작하지 않습니다.";
                return null;
            }

            int k = i + 1;
            while (k < text.Length && MyBatisBodyScanner.IsNameChar(text[k])) k++;
            string name = text[(i + 1)..k];
            int end = MyBatisBodyScanner.TagEnd(text, k, out bool openQuote);
            if (end < 0)
            {
                error = openQuote ? $"<{name}> 태그의 속성 따옴표가 닫히지 않았습니다." : $"<{name}> 태그가 닫히지 않았습니다.";
                return null;
            }

            if (end - 2 >= k && text[end - 2] == '/') return null;

            int cs = text.LastIndexOf("</" + name, StringComparison.Ordinal);
            int ce = cs < 0 ? -1 : cs + name.Length + 2;
            while (ce >= 0 && ce < text.Length && char.IsWhiteSpace(text[ce])) ce++;
            if (cs < end || ce < 0 || ce >= text.Length || text[ce] != '>')
            {
                error = $"</{name}> 닫는 태그를 찾지 못했습니다.";
                return null;
            }

            ce++;
            string tail = text[ce..];
            if (!string.IsNullOrWhiteSpace(tail))
            {
                error = "닫는 태그 뒤에 다른 글이 있습니다.";
                return null;
            }

            int ls = text.LastIndexOf('\n', cs - 1) + 1;
            string before = text[ls..cs];
            string closeIndent = before.Trim(' ', '\t').Length == 0 ? before : "";
            return new Outer(text[..end], text[end..cs], text[cs..ce], tail, closeIndent);
        }
    }
}
