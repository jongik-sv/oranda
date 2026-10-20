using System.Text;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 클립보드 TSV 읽기(「행 붙여넣기」). 「복사」·Excel 과 같은 규칙이다: 탭으로 칸, 줄바꿈(LF·CRLF·CR)으로 줄을 나누고,
/// 큰따옴표로 감싼 칸은 그 안의 탭·줄바꿈을 글자로 읽으며 <c>""</c> 는 <c>"</c> 하나다. 끝의 줄바꿈과 빈 줄은 줄로 치지 않는다.
/// </summary>
public static class TsvReader
{
    public static List<List<string>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var fieldStart = true;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (fieldStart && ch == '"')
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    field.Append(text[i++]);
                }

                fieldStart = false;
                continue;
            }

            if (ch == '\t')
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStart = true;
                i++;
                continue;
            }

            if (ch is '\r' or '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                EndRow(rows, row);
                row = [];
                fieldStart = true;
                i += ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }

            field.Append(ch);
            fieldStart = false;
            i++;
        }

        if (row.Count > 0 || field.Length > 0 || !fieldStart)
        {
            row.Add(field.ToString());
            EndRow(rows, row);
        }

        return rows;
    }

    private static void EndRow(List<List<string>> rows, List<string> row)
    {
        if (row is not [""])
        {
            rows.Add(row); // 빈 줄(칸 하나가 빈 글자)은 버린다
        }
    }
}
