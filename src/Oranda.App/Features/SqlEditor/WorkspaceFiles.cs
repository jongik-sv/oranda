using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.SqlEditor;

/// <summary>작업 공간 파일 안의 탭 하나: 탭 이름과 SQL. <see cref="Connection"/> 은 접속 프로필 ID(없으면 null), <see cref="Binds"/> 는 파일이 싣고 있는 바인드 값(없으면 null).</summary>
public sealed record WorkspaceFileTab(string Title, string Text, string? Connection = null, IReadOnlyList<WorkspaceBind>? Binds = null);

/// <summary>읽은 작업 공간 파일. <see cref="Notes"/> 는 읽으면서 바꾼 것에 대한 알림(MyBatis 동적 태그 단순화 등).</summary>
public sealed record WorkspaceFile(string Name, IReadOnlyList<WorkspaceFileTab> Tabs, IReadOnlyList<string> Notes);

/// <summary>
/// 작업 공간 파일(Orange SQL Tool 의 Workspace 파일에 해당). 탭 여러 개를 파일 하나로 저장·연다.
/// 저장 형식은 사람이 읽고 차이 보기 쉬운 Markdown 이고, MyBatis 매퍼 XML 은 읽기만 한다(문장 하나가 탭 하나).
/// </summary>
public static partial class WorkspaceFiles
{
    /// <summary>Markdown 파일 둘째 줄의 표지. 읽을 때 꼭 있어야 하지는 않다(손으로 쓴 Markdown 도 연다).</summary>
    public const string Marker = "<!-- oranda-workspace 1 -->";

    /// <summary>
    /// 작업 공간 파일의 확장자(D-028). 이름만 보고 작업 공간이면서 Markdown 인 것을 알 수 있게 <c>이름.workspace.md</c> 로 쓴다.
    /// 예전에 <c>이름.md</c> 로 저장한 파일도 그대로 연다.
    /// </summary>
    public const string Extension = ".workspace.md";

    /// <summary>파일 경로에서 작업 공간 이름: <c>.workspace.md</c> 면 그것을, 아니면 마지막 확장자만 뗀다.</summary>
    public static string NameOf(string path)
    {
        var file = Path.GetFileName(path);
        return file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? file[..^Extension.Length] : Path.GetFileNameWithoutExtension(file);
    }

    /// <summary>
    /// 저장할 경로를 규칙에 맞춘다. 확장자가 없으면 <c>.workspace.md</c> 를 붙이고, 아직 없는 새 <c>.md</c>·<c>.markdown</c> 경로는
    /// <c>.workspace.md</c> 로 바꾼다. 이미 있는 파일(예전 <c>이름.md</c> 작업 공간)은 그 경로에 그대로 쓴다.
    /// </summary>
    public static string SavePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var ext = Path.GetExtension(path);
        var markdown = string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase);
        if (markdown && File.Exists(path))
        {
            return path;
        }

        return (markdown ? path[..^ext.Length] : path) + Extension;
    }

    /// <summary>작업 공간 원본 파일의 종류 표시: .sqd → sqd, .xml → MyBatis, 그 밖(.workspace.md·.md) → md. 경로가 없으면 null.</summary>
    public static string? KindOf(string? path) => path is null ? null : IsSqd(path) ? "sqd" : IsMyBatis(path) ? "MyBatis" : "md";

    /// <summary>일반 「파일 열기」로 골라도 작업 공간으로 여는 파일: Orange <c>.sqd</c>, Oranda <c>.workspace.md</c>.</summary>
    public static bool OpensAsWorkspace(string path) =>
        IsSqd(path) || path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsMyBatis(string path) =>
        string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase);

    public static bool IsSqd(string path) =>
        string.Equals(Path.GetExtension(path), ".sqd", StringComparison.OrdinalIgnoreCase);

    /// <summary>다른 도구가 만든 읽기 전용 원본(MyBatis .xml, Orange .sqd). 저장은 Markdown 작업 공간으로 간다.</summary>
    public static bool IsReadOnlySource(string path) => IsMyBatis(path) || IsSqd(path);

    /// <summary>파일을 읽는다: .xml 은 MyBatis 매퍼, .sqd 는 Orange 작업 공간, 나머지는 Markdown.</summary>
    public static WorkspaceFile Load(string path)
    {
        var name = NameOf(path);
        if (IsSqd(path))
        {
            return ReadSqd(File.ReadAllBytes(path), name);
        }

        var text = File.ReadAllText(path);
        return IsMyBatis(path) ? ReadMyBatis(text, name) : ReadMarkdown(text, name);
    }

    // ---- Orange .sqd ----------------------------------------------------------------------

    /// <summary>바이트로 읽는다: BOM 이 있으면 그 인코딩, 없으면 UTF-8 로 읽히는지 보고 아니면 CP949.</summary>
    public static WorkspaceFile ReadSqd(byte[] bytes, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return ReadSqd(FileEncoding.Decode(bytes).Text, fallbackName);
    }

    /// <summary>
    /// Orange 5.x 작업 공간(.sqd). <c>[WORKSPACE]</c> 구역에서 탭마다 <c>NAME=</c>, <c>STMT=줄 수</c>, 그 줄 수만큼 SQL,
    /// <c>VAR=개수</c>, 그 개수만큼 <c>:이름=타입,값</c>. SQL 은 줄 수로 잘라 읽으므로 본문 줄이 NAME= 처럼 보여도 키로 읽지 않는다.
    /// 깨진 곳은 읽은 만큼 열고 알림에 남긴다. 이름에 pass·pwd·secret·token 이 든 바인드는 값을 버린다.
    /// </summary>
    public static WorkspaceFile ReadSqd(string text, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inv = CultureInfo.InvariantCulture;
        var notes = new List<string>();
        var start = Array.FindIndex(lines, l => l.Trim().Equals("[WORKSPACE]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            throw new FormatException("Orange 작업 공간 파일이 아니다([WORKSPACE] 구역이 없다).");
        }

        var i = start + 1;
        int? count = null;
        if (i < lines.Length && lines[i].StartsWith("COUNT=", StringComparison.OrdinalIgnoreCase))
        {
            count = int.TryParse(lines[i][6..].Trim(), NumberStyles.None, inv, out var c) ? c : null;
            i++;
        }

        var tabs = new List<WorkspaceFileTab>();
        var unknownTypes = new SortedSet<string>(StringComparer.Ordinal);
        var skipped = false;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("NAME=", StringComparison.Ordinal))
            {
                if (lines[i].Trim().Length > 0)
                {
                    skipped = true;
                }

                i++;
                continue;
            }

            var title = lines[i][5..].Trim();
            if (title.Length == 0)
            {
                title = "SQL " + (tabs.Count + 1).ToString(inv);
            }

            i++;
            var label = $"탭 {(tabs.Count + 1).ToString(inv)}({title})";
            var body = new List<string>();
            if (i < lines.Length && lines[i].StartsWith("STMT=", StringComparison.Ordinal))
            {
                if (!int.TryParse(lines[i][5..].Trim(), NumberStyles.None, inv, out var stmt))
                {
                    notes.Add($"{label}: STMT 값을 읽지 못해 본문을 비웠다.");
                    stmt = 0;
                }

                i++;
                var take = Math.Min(stmt, lines.Length - i);
                for (var k = 0; k < take; k++)
                {
                    body.Add(lines[i + k]);
                }

                i += take;
                if (take < stmt)
                {
                    notes.Add($"{label}: 본문이 {stmt.ToString(inv)}줄이어야 하는데 {take.ToString(inv)}줄만 읽었다.");
                }
            }
            else
            {
                notes.Add($"{label}: STMT 줄이 없어 본문을 비웠다.");
            }

            var binds = new List<WorkspaceBind>();
            if (i < lines.Length && lines[i].StartsWith("VAR=", StringComparison.Ordinal))
            {
                int.TryParse(lines[i][4..].Trim(), NumberStyles.None, inv, out var vars);
                i++;
                var take = Math.Min(vars, lines.Length - i);
                for (var k = 0; k < take; k++)
                {
                    if (ParseSqdBind(lines[i + k], unknownTypes) is { } bind)
                    {
                        binds.Add(bind);
                    }
                    else
                    {
                        notes.Add($"{label}: 바인드 줄을 읽지 못했다.");
                    }
                }

                i += take;
                if (take < vars)
                {
                    notes.Add($"{label}: 바인드가 {vars.ToString(inv)}개여야 하는데 {take.ToString(inv)}개만 읽었다.");
                }
            }

            tabs.Add(new WorkspaceFileTab(title, string.Join('\n', body), null, binds.Count > 0 ? binds : null));
        }

        if (skipped)
        {
            notes.Add("탭 사이에 읽을 수 없는 줄이 있어 건너뛰었다.");
        }

        if (count is { } expected && expected != tabs.Count)
        {
            notes.Add($"COUNT 는 {expected.ToString(inv)}인데 실제 탭은 {tabs.Count.ToString(inv)}개다. 읽은 만큼 열었다.");
        }

        if (count is null)
        {
            notes.Add("COUNT 를 읽지 못했다. 읽은 만큼 열었다.");
        }

        if (unknownTypes.Count > 0)
        {
            notes.Add("모르는 바인드 타입 코드(" + string.Join(", ", unknownTypes) + ")는 문자열로 읽었다.");
        }

        if (tabs.Count == 0)
        {
            notes.Add("탭이 없다.");
        }

        return new WorkspaceFile(fallbackName, tabs, notes);
    }

    /// <summary><c>:이름=타입,값</c>. 값은 첫 쉼표 뒤 전부(비어 있을 수 있다).</summary>
    private static WorkspaceBind? ParseSqdBind(string line, SortedSet<string> unknownTypes)
    {
        var eq = line.IndexOf('=', StringComparison.Ordinal);
        var name = (eq < 0 ? line : line[..eq]).Trim().TrimStart(':');
        if (eq < 0 || name.Length == 0)
        {
            return null;
        }

        var rest = line[(eq + 1)..];
        var comma = rest.IndexOf(',', StringComparison.Ordinal);
        var type = comma < 0 ? rest.Trim() : rest[..comma].Trim();
        var value = comma < 0 ? string.Empty : rest[(comma + 1)..];
        if (type != "0")
        {
            unknownTypes.Add(type);
        }

        if (SensitiveBindNames.IsSensitive(name))
        {
            value = string.Empty;
        }

        return new WorkspaceBind { Name = name, Type = BindDataType.Varchar2, Direction = BindDirection.In, Value = value, IsNull = value.Length == 0 };
    }

    // ---- Markdown -------------------------------------------------------------------------

    /// <summary>
    /// 탭마다 <c>## 탭 이름</c> 과 <c>```sql</c> 코드 블록. SQL 안에 백틱이 이어 나오면 울타리를 그보다 길게 만든다.
    /// </summary>
    public static string WriteMarkdown(string name, IEnumerable<WorkspaceFileTab> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(OneLine(name));
        sb.AppendLine();
        sb.AppendLine(Marker);
        foreach (var tab in tabs)
        {
            sb.AppendLine();
            sb.Append("## ").AppendLine(OneLine(tab.Title));
            sb.AppendLine();
            if (tab.Connection is { Length: > 0 } conn)
            {
                sb.Append("<!-- connection: ").Append(OneLine(conn)).AppendLine(" -->");
                sb.AppendLine();
            }

            var fence = new string('`', Math.Max(3, LongestRun(tab.Text, '`') + 1));
            sb.Append(fence).AppendLine("sql");
            sb.Append(tab.Text.TrimEnd('\r', '\n')).AppendLine();
            sb.AppendLine(fence);
        }

        return sb.ToString();
    }

    /// <summary>
    /// <c>## 제목</c> 아래 첫 코드 블록이 그 탭의 SQL 이다. 제목이 없는 코드 블록은 「SQL n」 탭이 되고,
    /// 코드 블록이 하나도 없으면 파일 전체를 탭 하나로 연다.
    /// </summary>
    public static WorkspaceFile ReadMarkdown(string text, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var name = fallbackName;
        var tabs = new List<WorkspaceFileTab>();
        string? title = null;
        string? connection = null;
        var tookBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("# ", StringComparison.Ordinal) && tabs.Count == 0 && title is null)
            {
                name = line[2..].Trim();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                title = line[3..].Trim();
                connection = null;
                tookBlock = false;
                continue;
            }

            var conn = Regex.Match(line, @"^<!--\s*connection:\s*(.+?)\s*-->\s*$");
            if (conn.Success)
            {
                connection = conn.Groups[1].Value;
                continue;
            }

            var open = Regex.Match(line, @"^(`{3,}|~{3,})\s*([A-Za-z/]*)\s*$");
            if (!open.Success)
            {
                continue;
            }

            var fence = open.Groups[1].Value;
            var body = new List<string>();
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                var close = lines[j].TrimEnd();
                if (close.Length >= fence.Length && close.All(c => c == fence[0]))
                {
                    break;
                }

                body.Add(lines[j]);
            }

            i = j;
            if (tookBlock)
            {
                continue; // 한 제목 아래 두 번째 코드 블록은 설명용으로 보고 건너뛴다.
            }

            var tabTitle = title ?? "SQL " + (tabs.Count + 1).ToString(CultureInfo.InvariantCulture);
            tabs.Add(new WorkspaceFileTab(tabTitle, string.Join('\n', body), connection));
            tookBlock = title is not null;
        }

        if (tabs.Count == 0)
        {
            tabs.Add(new WorkspaceFileTab(fallbackName, text));
        }

        return new WorkspaceFile(name, tabs, []);
    }

    // ---- 공통 -----------------------------------------------------------------------------

    /// <summary>공통 들여쓰기를 걷어 낸다. 빈 줄은 연속이면 하나로, <paramref name="keepBlankLines"/> 가 거짓이면 모두 뺀다.</summary>
    private static string Dedent(string text, bool keepBlankLines = true)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var indent = lines.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        var result = new List<string>();
        foreach (var l in lines)
        {
            if (l.Length == 0 && (!keepBlankLines || (result.Count > 0 && result[^1].Length == 0)))
            {
                continue;
            }

            result.Add(l.Length >= indent ? l[indent..] : l.TrimStart());
        }

        return string.Join('\n', result);
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text)
        {
            run = ch == c ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }
}
