using System.Globalization;
using System.Net;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Transfer;

namespace Oranda.Oracle.Transfer;

/// <summary>테이블 정의서 쓰기: xlsx(<see cref="TableSpecXlsx"/>)·Markdown·HTML. 소유: TSK-13-06.</summary>
[OrandaService(typeof(ITableSpecWriter))]
internal sealed class TableSpecWriter : ITableSpecWriter
{
    internal static readonly string[] ColumnHeaders = ["순번", "열 이름", "타입", "NULL", "PK", "FK", "기본값", "주석"];

    internal static readonly string[] IndexHeaders = ["인덱스", "고유", "열"];

    public string Extension(TableSpecFormat format) => format switch
    {
        TableSpecFormat.Xlsx => ".xlsx",
        TableSpecFormat.Markdown => ".md",
        TableSpecFormat.Html => ".html",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public Task WriteAsync(TableSpecDocument document, TableSpecFormat format, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);
        ct.ThrowIfCancellationRequested();

        // 압축·XML 쓰기는 CPU 일이라 표가 많으면 UI 를 멈추므로 풀 스레드에서 한다.
        return Task.Run(() => Write(document, format, destination), ct);
    }

    private static void Write(TableSpecDocument document, TableSpecFormat format, Stream destination)
    {
        switch (format)
        {
            case TableSpecFormat.Xlsx:
                TableSpecXlsx.Write(document, destination);
                break;
            case TableSpecFormat.Markdown:
                WriteText(destination, Markdown(document));
                break;
            case TableSpecFormat.Html:
                WriteText(destination, Html(document));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    // ---- 공통 ----

    internal static string Heading(TableSpecTable t) => $"{t.Owner}.{t.Name}";

    internal static string YesNo(bool value) => value ? "Y" : string.Empty;

    internal static string Joined(IReadOnlyList<string> columns) => string.Join(", ", columns);

    /// <summary>NULL 칸: 허용하면 <c>Y</c>, 아니면 <c>N</c>(NOT NULL).</summary>
    internal static string NullCell(TableSpecColumn c) => c.IsNullable ? "Y" : "N";

    internal static string[] ColumnRow(TableSpecColumn c) =>
    [
        c.No.ToString(CultureInfo.InvariantCulture), c.Name, c.DataType, NullCell(c), YesNo(c.IsPrimaryKey),
        c.References ?? string.Empty, c.DefaultValue ?? string.Empty, c.Comment ?? string.Empty,
    ];

    internal static string[] IndexRow(TableSpecIndex i) => [i.Name, YesNo(i.IsUnique), Joined(i.Columns)];

    private static void WriteText(Stream destination, string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        destination.Write(bytes, 0, bytes.Length);
    }

    // ---- Markdown ----

    internal static string Markdown(TableSpecDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(MdText(doc.Title)).Append('\n').Append('\n');
        sb.Append("생성: ").Append(doc.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\n\n");
        sb.Append("## 목차\n\n");
        var anchors = Anchors(doc);
        for (var i = 0; i < doc.Tables.Count; i++)
        {
            var t = doc.Tables[i];
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. [{MdText(Heading(t))}](#{anchors[i]})");
            if (!string.IsNullOrWhiteSpace(t.Comment))
            {
                sb.Append(" — ").Append(MdText(t.Comment));
            }

            sb.Append('\n');
        }

        foreach (var t in doc.Tables)
        {
            sb.Append("\n## ").Append(MdText(Heading(t))).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(t.Comment))
            {
                sb.Append(MdText(t.Comment)).Append("\n\n");
            }

            MdTable(sb, ColumnHeaders, t.Columns.Select(ColumnRow));
            if (t.Indexes.Count > 0)
            {
                sb.Append("\n**인덱스**\n\n");
                MdTable(sb, IndexHeaders, t.Indexes.Select(IndexRow));
            }
        }

        return sb.ToString();
    }

    private static void MdTable(StringBuilder sb, string[] headers, IEnumerable<string[]> rows)
    {
        sb.Append("| ").Append(string.Join(" | ", headers)).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", headers.Length))).Append('\n');
        foreach (var row in rows)
        {
            sb.Append("| ").Append(string.Join(" | ", row.Select(MdText))).Append(" |\n");
        }
    }

    /// <summary>표 칸·제목에 들어가는 글: 줄바꿈은 공백으로, 표 구분자(<c>|</c>)와 백슬래시는 이스케이프한다.</summary>
    internal static string MdText(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>GitHub 방식 제목 앵커(소문자, 공백→<c>-</c>, 글자·숫자·<c>_</c>·<c>-</c>만). 같은 이름이 겹치면 <c>-1</c>, <c>-2</c>.</summary>
    private static string[] Anchors(TableSpecDocument doc)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new string[doc.Tables.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var sb = new StringBuilder();
            foreach (var ch in Heading(doc.Tables[i]).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch is '_' or '-')
                {
                    sb.Append(ch);
                }
                else if (ch == ' ')
                {
                    sb.Append('-');
                }
            }

            var key = sb.ToString();
            var n = seen.GetValueOrDefault(key);
            seen[key] = n + 1;
            result[i] = n == 0 ? key : $"{key}-{n}";
        }

        return result;
    }

    // ---- HTML ----

    internal static string Html(TableSpecDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"ko\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<title>").Append(H(doc.Title)).Append("</title>\n");
        sb.Append("<style>\n");
        sb.Append("body{font-family:-apple-system,'Malgun Gothic',sans-serif;margin:24px;color:#222}\n");
        sb.Append("table{border-collapse:collapse;margin:8px 0 20px}\n");
        sb.Append("th,td{border:1px solid #bbb;padding:4px 10px;text-align:left;vertical-align:top}\n");
        sb.Append("th{background:#eef2f7}\n");
        sb.Append(".muted{color:#666}\n");
        sb.Append("</style>\n</head>\n<body>\n");
        sb.Append("<h1>").Append(H(doc.Title)).Append("</h1>\n");
        sb.Append("<p class=\"muted\">생성: ").Append(doc.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("</p>\n");
        sb.Append("<h2>목차</h2>\n<ol>\n");
        for (var i = 0; i < doc.Tables.Count; i++)
        {
            var t = doc.Tables[i];
            sb.Append(CultureInfo.InvariantCulture, $"<li><a href=\"#t{i + 1}\">{H(Heading(t))}</a>");
            if (!string.IsNullOrWhiteSpace(t.Comment))
            {
                sb.Append(" <span class=\"muted\">— ").Append(H(t.Comment)).Append("</span>");
            }

            sb.Append("</li>\n");
        }

        sb.Append("</ol>\n");
        for (var i = 0; i < doc.Tables.Count; i++)
        {
            var t = doc.Tables[i];
            sb.Append(CultureInfo.InvariantCulture, $"<h2 id=\"t{i + 1}\">{H(Heading(t))}</h2>\n");
            if (!string.IsNullOrWhiteSpace(t.Comment))
            {
                sb.Append("<p>").Append(H(t.Comment)).Append("</p>\n");
            }

            HtmlTable(sb, ColumnHeaders, t.Columns.Select(ColumnRow));
            if (t.Indexes.Count > 0)
            {
                sb.Append("<h3>인덱스</h3>\n");
                HtmlTable(sb, IndexHeaders, t.Indexes.Select(IndexRow));
            }
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void HtmlTable(StringBuilder sb, string[] headers, IEnumerable<string[]> rows)
    {
        sb.Append("<table>\n<thead><tr>");
        foreach (var h in headers)
        {
            sb.Append("<th>").Append(H(h)).Append("</th>");
        }

        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row)
            {
                sb.Append("<td>").Append(H(cell)).Append("</td>");
            }

            sb.Append("</tr>\n");
        }

        sb.Append("</tbody>\n</table>\n");
    }

    private static string H(string text) => WebUtility.HtmlEncode(text);
}
