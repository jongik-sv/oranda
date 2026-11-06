using System.Text;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 실패 행 목록을 CSV 로 쓴다(테이블, 키, 오류). 사용자가 「CSV로 저장」을 눌렀을 때만 부른다. 행 값은 디스크에 남기지 않는 원칙(계획 Global Constraints)에 따라
/// 실패 행의 키 열 값만 나간다. UTF-8(BOM 포함)이라 Excel 에서 한글이 깨지지 않는다.
/// </summary>
public static class FailedRowsCsv
{
    public static async Task WriteAsync(Stream stream, IReadOnlyList<FailedRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rows);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await writer.WriteAsync("table,key,error\r\n".AsMemory(), ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var key = string.Join(';', row.Key.Select(k => $"{k.Key}={k.Value ?? "NULL"}"));
            await writer.WriteAsync($"{Quote(row.Table)},{Quote(key)},{Quote(row.Error)}\r\n".AsMemory(), ct).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    // 쉼표·따옴표·줄바꿈이 있으면 큰따옴표로 감싸고 안의 큰따옴표는 두 번 쓴다(RFC 4180).
    // 엑셀이 수식으로 읽는 시작 글자(= + @)는 작은따옴표를 앞에 붙여 막는다.
    internal static string Quote(string value)
    {
        var text = value.Length > 0 && value[0] is '=' or '+' or '@' ? "'" + value : value;
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
