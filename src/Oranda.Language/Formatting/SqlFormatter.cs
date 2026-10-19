using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Core.Text;

namespace Oranda.Language.Formatting;

/// <summary>
/// 포매터(FMT-01·02·03·07·11). 전체·범위(선택·현재 문장)를 포맷하고, 출력이 입력과 토큰 열·주석이 같고(FMT-11)
/// 두 번 포맷해도 같을 때(멱등)만 적용한다. 범위는 줄 경계·여러 줄 토큰 경계로 넓히고 첫 줄의 원래 들여쓰기를 지킨다.
/// 엔진은 <see cref="FormatEngine"/>, 확장 규칙은 LayoutRules.cs(TSK-05-02). 소유: TSK-05-01.
/// </summary>
[OrandaService(typeof(ISqlFormatter))]
internal sealed class SqlFormatter : ISqlFormatter
{
    public FormatResult Format(string text, FormatterOptions o, TextRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(o);

        int start = 0, end = text.Length;
        string baseIndent = "";
        if (range is { } r) (start, end, baseIndent) = Expand(text, r);
        var whole = new TextRange(start, end - start);
        string fragment = text[start..end];
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        try
        {
            var run = FormatEngine.Run(fragment, o, baseIndent, newline);
            if (run is null || run.Text == fragment) return new FormatResult(text, whole);

            var read = FormatTokenizer.Read(run.Text);
            var failure = FormatEngine.Verify(run.Items, read, out var message);
            if (failure != FormatFailure.None) return new FormatResult(text, whole, failure) { Message = message };

            var again = FormatEngine.Run(run.Text, o, baseIndent, newline, read);
            if (again is not null && again.Text != run.Text)
                return new FormatResult(text, whole, FormatFailure.NotIdempotent) { Message = "두 번 포맷한 결과가 다르다." };

            return new FormatResult(string.Concat(text.AsSpan(0, start), run.Text, text.AsSpan(end)), whole);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return new FormatResult(text, whole, FormatFailure.InternalError) { Message = ex.Message };
        }
    }

    /// <summary>범위를 여러 줄 토큰·줄 경계로 넓힌다. 끝은 마지막 줄의 줄바꿈 앞.</summary>
    static (int Start, int End, string BaseIndent) Expand(string text, TextRange r)
    {
        int s = Math.Clamp(r.Start, 0, text.Length);
        int e = Math.Clamp(r.End, s, text.Length);
        if (e > s && text[e - 1] == '\n') e--;
        if (e > s && text[e - 1] == '\r') e--;

        var items = FormatTokenizer.Read(text).Items;
        bool changed = true;
        while (changed)
        {
            changed = false;
            int ls = s == 0 ? 0 : text.LastIndexOf('\n', s - 1) + 1;
            int nl = text.IndexOf('\n', e);
            int le = nl < 0 ? text.Length : nl;
            if (le > 0 && le <= text.Length && le > ls && text[le - 1] == '\r') le--;
            if (le < e) le = e;
            foreach (var it in items)
            {
                if (it.IsRaw) continue;
                if (it.Start < ls && it.End > ls) { ls = it.Start; changed = true; }
                if (it.Start < le && it.End > le) { le = it.End; changed = true; }
            }

            s = ls;
            e = le;
        }

        int i = s;
        while (i < e && (text[i] == ' ' || text[i] == '\t')) i++;
        return (s, e, text[s..i]);
    }
}
