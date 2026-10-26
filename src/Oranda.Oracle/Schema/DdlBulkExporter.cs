using System.Text;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Oracle.Schema;

public enum DdlExtractLayout
{
    /// <summary>스키마 전체를 파일 하나로.</summary>
    SingleFile,

    /// <summary><c>유형/이름.sql</c> 로 객체마다 파일 하나.</summary>
    FilePerObject,
}

/// <summary>이름 바꾸기 한 쌍. DDL 안의 따옴표 식별자(<c>"FROM"</c>)를 <c>"TO"</c> 로 바꾼다(스키마 이름도 같다).</summary>
public sealed record DdlRename(string From, string To);

/// <summary>DDL 일괄 추출 요청. 저장 속성(저장·세그먼트·테이블스페이스·종결자·종속 객체)은 <see cref="Ddl"/>.</summary>
public sealed record DdlExtractRequest
{
    public required string Owner { get; init; }

    /// <summary>빈 목록 = <see cref="DdlBulkExporter.DefaultTypes"/>.</summary>
    public IReadOnlyList<string> ObjectTypes { get; init; } = [];

    /// <summary>SQL LIKE(이스케이프 <c>\</c>) 이름 패턴. null = 전부.</summary>
    public string? NamePattern { get; init; }

    public bool InvalidOnly { get; init; }

    /// <summary>
    /// 추출할 객체를 화면이 직접 정한 목록(대상 미리보기에서 체크한 것). null 이면 위 조건(스키마·유형·이름·INVALID)으로 목록을 조회하고,
    /// 값이 있으면 조회하지 않고 이 목록만 추출한다(빈 목록 = 추출할 것 없음).
    /// </summary>
    public IReadOnlyList<DbObjectRef>? Objects { get; init; }

    public DdlOptions Ddl { get; init; } = new();

    public IReadOnlyList<DdlRename> Renames { get; init; } = [];

    public required string OutputDirectory { get; init; }

    public DdlExtractLayout Layout { get; init; } = DdlExtractLayout.SingleFile;
}

public sealed record DdlExtractItem(DbObjectRef Ref, bool Succeeded, string? Error);

public sealed record DdlExtractResult(IReadOnlyList<DdlExtractItem> Items, IReadOnlyList<string> Files)
{
    public int SucceededCount => Items.Count(i => i.Succeeded);

    public int FailedCount => Items.Count - SucceededCount;
}

/// <summary>
/// DDL 일괄 추출(SCH-10). 객체 목록은 <see cref="IMetadataProvider.ListObjectsAsync"/>, DDL 은 <see cref="IMetadataProvider.GetDdlAsync"/> 를
/// 객체마다 부른다. 한 객체가 실패해도 나머지는 계속하고 결과 목록에 오류를 남긴다. 상태를 바꾸지 않는 읽기 작업이다.
/// </summary>
public static class DdlBulkExporter
{
    /// <summary>의존 순서(앞 유형이 먼저 만들어져야 하는 순). 파일에도 이 순서로 쓴다.</summary>
    public static IReadOnlyList<string> DefaultTypes { get; } =
    [
        "SEQUENCE", "TYPE", "TABLE", "VIEW", "MATERIALIZED VIEW", "SYNONYM", "FUNCTION", "PROCEDURE",
        "PACKAGE", "PACKAGE BODY", "TYPE BODY", "TRIGGER",
    ];

    public static async Task<DdlExtractResult> ExtractAsync(IMetadataProvider meta, ISession session, DdlExtractRequest request,
                                                            IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var types = request.ObjectTypes.Count == 0 ? DefaultTypes : request.ObjectTypes;
        var listed = request.Objects ?? await meta.ListObjectsAsync(session, new ObjectQuery
        {
            Owner = request.Owner,
            ObjectTypes = [.. types],
            NamePattern = request.NamePattern,
            InvalidOnly = request.InvalidOnly,
        }, ct);

        var order = types.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i, StringComparer.Ordinal);
        var objects = listed
            .OrderBy(o => order.GetValueOrDefault(o.ObjectType, int.MaxValue))
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

        var items = new List<DdlExtractItem>();
        var ddls = new List<(DbObjectRef Ref, string Ddl)>();
        for (var i = 0; i < objects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var obj = objects[i];
            try
            {
                var ddl = await meta.GetDdlAsync(session, obj, request.Ddl, ct);
                ddls.Add((obj, ApplyRenames(ddl, request.Renames)));
                items.Add(new DdlExtractItem(obj, true, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                items.Add(new DdlExtractItem(obj, false, ex.Message.ReplaceLineEndings(" ")));
            }

            progress?.Report((i + 1, objects.Count));
        }

        var files = await WriteAsync(request, ddls, ct);
        return new DdlExtractResult(items, files);
    }

    /// <summary>따옴표 식별자 이름 바꾸기. 긴 이름부터 바꿔 겹침을 피한다.</summary>
    public static string ApplyRenames(string ddl, IReadOnlyList<DdlRename> renames)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        if (renames is null || renames.Count == 0)
        {
            return ddl;
        }

        // 한 번에 바꾸려고 자리 표시 문자열을 거치지 않고 정규식으로 한 번만 훑는다(바꾼 결과가 다시 바뀌지 않게).
        var map = renames.Where(r => !string.IsNullOrEmpty(r.From)).GroupBy(r => r.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().To, StringComparer.Ordinal);
        if (map.Count == 0)
        {
            return ddl;
        }

        var pattern = "\"(" + string.Join('|', map.Keys.OrderByDescending(k => k.Length).Select(System.Text.RegularExpressions.Regex.Escape)) + ")\"";
        return System.Text.RegularExpressions.Regex.Replace(ddl, pattern, m => "\"" + map[m.Groups[1].Value] + "\"");
    }

    /// <summary>파일 이름으로 못 쓰는 글자를 <c>_</c> 로.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static async Task<IReadOnlyList<string>> WriteAsync(DdlExtractRequest request, List<(DbObjectRef Ref, string Ddl)> ddls, CancellationToken ct)
    {
        if (ddls.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(request.OutputDirectory);
        var utf8 = new UTF8Encoding(false);
        var files = new List<string>();

        if (request.Layout == DdlExtractLayout.SingleFile)
        {
            var owner = Rename(request.Owner, request.Renames);
            var path = Path.Combine(request.OutputDirectory, SafeFileName(owner) + ".sql");
            var sb = new StringBuilder();
            sb.Append("-- DDL extract: ").Append(owner).Append(", ").Append(ddls.Count).AppendLine(" objects");
            foreach (var (obj, ddl) in ddls)
            {
                sb.AppendLine().Append("-- ").Append(obj.ObjectType).Append(' ').AppendLine(Rename(obj.Name, request.Renames)).AppendLine(ddl);
            }

            await File.WriteAllTextAsync(path, sb.ToString(), utf8, ct);
            files.Add(path);
            return files;
        }

        foreach (var (obj, ddl) in ddls)
        {
            var dir = Path.Combine(request.OutputDirectory, SafeFileName(obj.ObjectType.ToLowerInvariant().Replace(' ', '_')));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SafeFileName(Rename(obj.Name, request.Renames)) + ".sql");
            await File.WriteAllTextAsync(path, ddl + Environment.NewLine, utf8, ct);
            files.Add(path);
        }

        return files;
    }

    private static string Rename(string name, IReadOnlyList<DdlRename> renames)
        => renames.LastOrDefault(r => string.Equals(r.From, name, StringComparison.Ordinal))?.To ?? name;
}
