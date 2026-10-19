using System.Text;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Metadata;

/// <summary>PL/SQL 소스·컴파일 오류. 소유: TSK-08-01.</summary>
internal sealed partial class OracleMetadataProvider
{
    /// <summary>
    /// 소스 본문(all_source 줄을 이은 글). 첫 줄은 <c>PACKAGE BODY name AS</c> 처럼 <c>CREATE OR REPLACE</c> 뒤부터 시작한다.
    /// 객체가 없거나 볼 권한이 없으면 빈 문자열 대신 <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        var rows = await QueryAsync(s, "meta.source", c => BindSourceObject(c, obj), ct);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"소스를 찾을 수 없다: {obj.Owner}.{obj.Name} ({obj.ObjectType})");
        }

        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            // all_source.text 는 줄끝(LF)을 이미 가진다. 마지막 줄만 없을 수 있다.
            sb.Append(Str(r[0]));
        }

        return sb.ToString();
    }

    /// <summary>서버 컴파일 오류·경고(줄·열 1부터, 서버 순서).</summary>
    public async Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(obj);

        var rows = await QueryAsync(s, "meta.compile_errors", c => BindSourceObject(c, obj), ct);
        return [.. rows.Select(r => new CompileError(
            Math.Max(1, Int(r[0]) ?? 1),
            Math.Max(1, Int(r[1]) ?? 1),
            (Str(r[2]) ?? string.Empty).Trim(),
            string.Equals(Str(r[3]), "WARNING", StringComparison.Ordinal))
        {
            MessageNumber = Int(r[4]) ?? 0,
        })];
    }

    private static void BindSourceObject(OracleCommand c, DbObjectRef obj)
    {
        c.Parameters.Add("owner", obj.Owner);
        c.Parameters.Add("name", obj.Name);
        c.Parameters.Add("type", obj.ObjectType);
    }
}
