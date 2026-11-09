using System.Text;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>PSM(PL/SQL) 소스·컴파일 오류 — Oracle 과 같은 규칙. Tibero 에는 Java 저장 객체가 없어 Java 분기는 두지 않는다.</summary>
internal sealed partial class TiberoMetadataProvider
{
    /// <summary>
    /// 소스 본문(*_source 줄을 이은 글). 첫 줄은 <c>PACKAGE BODY name AS</c> 처럼 <c>CREATE OR REPLACE</c> 뒤부터 시작한다.
    /// 객체가 없거나 볼 권한이 없어 줄이 없으면 빈 문자열 대신 <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        var rows = await RunAsync(s, "meta.source", ObjectBinds(obj.Owner, obj.Name, obj.ObjectType), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"소스를 찾을 수 없다: {obj.Owner}.{obj.Name} ({obj.ObjectType})");
        }

        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            // Oracle 의 all_source.text 는 줄끝(LF)을 가진다. Tibero 는 없을 수 있어 LF 로 끝나지 않는 줄에는 붙인다(줄이 서로 붙지 않게).
            var line = Str(r[0]) ?? string.Empty;
            sb.Append(line);
            if (!line.EndsWith('\n'))
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>서버 컴파일 오류·경고(줄·열 1부터, 서버 순서). 열: line, position, text, attribute, message_number.</summary>
    public async Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        var rows = await RunAsync(s, "meta.compile_errors", ObjectBinds(obj.Owner, obj.Name, obj.ObjectType), ct).ConfigureAwait(false);
        return [.. rows.Select(r => new CompileError(
            Math.Max(1, Int(r[0]) ?? 1),
            Math.Max(1, Int(r[1]) ?? 1),
            (Str(r[2]) ?? string.Empty).Trim(),
            string.Equals(Str(r[3]), "WARNING", StringComparison.Ordinal))
        {
            MessageNumber = Int(r[4]) ?? 0,
        })];
    }
}
