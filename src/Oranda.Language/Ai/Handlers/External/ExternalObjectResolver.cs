using System.Text;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>풀린 객체 하나. <see cref="Types"/> 는 같은 이름의 유형들(패키지면 <c>PACKAGE</c>·<c>PACKAGE BODY</c>), 동의어는 빼고 센다.</summary>
internal sealed record ExternalObject(string Owner, string Name, IReadOnlyList<string> Types, bool ViaSynonym)
{
    /// <summary>대표 유형(표·뷰·PL/SQL 단위 순으로, 본문 유형은 뒤).</summary>
    public string Type => ExternalObjectResolver.Primary(Types);

    public DbObjectRef Ref => new(Owner, Name, Type);
}

/// <summary>
/// 외부 메타 도구의 이름 풀기. 게이트의 <c>ResolveAsync</c>(범위 검사용)는 표·뷰와 동의어만 풀므로, 패키지·프로시저·시퀀스 같은 객체도 찾도록
/// 객체 목록(<see cref="IMetadataProvider.ListObjectsAsync"/>)과 이름 풀이 한 단계(<see cref="IMetadataProvider.GetNameStepAsync"/>)를 같이 쓴다.
/// 오라클 이름 풀이 순서: 그 스키마의 객체 → 그 스키마의 개인 동의어 → 스키마를 적지 않았을 때만 PUBLIC 동의어. DB 링크 동의어는 풀지 않는다(없음으로 본다).
/// </summary>
internal static class ExternalObjectResolver
{
    private const int MaxDepth = 5;

    private static readonly string[] Preference =
    [
        "TABLE", "VIEW", "MATERIALIZED VIEW", "PACKAGE", "PROCEDURE", "FUNCTION", "TYPE", "TRIGGER", "SEQUENCE", "INDEX",
    ];

    /// <summary><c>NAME</c>·<c>OWNER.NAME</c>(따옴표 이름은 대소문자 그대로)을 실제 객체로 푼다. 없거나 보이지 않으면 null.</summary>
    /// <param name="defaultSchema">소유자가 없을 때 쓰는 스키마. null 이면 접속 사용자.</param>
    public static async Task<ExternalObject?> ResolveAsync(IMetadataProvider metadata, ISession db, string name, string? defaultSchema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Trim().Split('.', 2, StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0))
        {
            return null;
        }

        var written = parts.Length == 2;
        var owner = written ? Unquote(parts[0]) : (string.IsNullOrWhiteSpace(defaultSchema) ? db.Profile.User : defaultSchema).ToUpperInvariant();
        var objectName = Unquote(parts[^1]);
        var via = false;
        var publicTried = written;
        for (var depth = 0; depth <= MaxDepth; depth++)
        {
            var types = await TypesOfAsync(metadata, db, owner, objectName, ct).ConfigureAwait(false);
            if (types.Count > 0)
            {
                return new ExternalObject(owner, objectName, types, via);
            }

            var step = await metadata.GetNameStepAsync(db, owner, objectName, ct).ConfigureAwait(false);
            if (step is not { IsSynonym: true } && !publicTried)
            {
                publicTried = true;
                step = await metadata.GetNameStepAsync(db, "PUBLIC", objectName, ct).ConfigureAwait(false);
            }

            if (step is not { IsSynonym: true, TargetName: { Length: > 0 } target } || !string.IsNullOrEmpty(step.DbLink))
            {
                return null;
            }

            owner = string.IsNullOrEmpty(step.TargetOwner) ? owner : step.TargetOwner;
            objectName = target;
            via = true;
            publicTried = true;
        }

        return null;
    }

    /// <summary>대표 유형을 고른다.</summary>
    public static string Primary(IReadOnlyList<string> types)
    {
        foreach (var preferred in Preference)
        {
            if (types.Contains(preferred, StringComparer.Ordinal))
            {
                return preferred;
            }
        }

        return types.FirstOrDefault(t => !t.EndsWith(" BODY", StringComparison.Ordinal)) ?? (types.Count > 0 ? types[0] : "TABLE");
    }

    /// <summary>LIKE 특수 글자(<c>%</c>·<c>_</c>·<c>\</c>)를 <c>\</c> 로 막는다(이름 하나만 고르게).</summary>
    public static string EscapeLike(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (c is '%' or '_' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>따옴표 이름은 그대로, 아니면 대문자.</summary>
    public static string Unquote(string part) =>
        part.Length >= 2 && part[0] == '"' && part[^1] == '"' ? part[1..^1] : part.ToUpperInvariant();

    private static async Task<IReadOnlyList<string>> TypesOfAsync(IMetadataProvider metadata, ISession db, string owner, string name, CancellationToken ct)
    {
        var found = await metadata.ListObjectsAsync(db, new ObjectQuery { Owner = owner, NamePattern = EscapeLike(name) }, ct).ConfigureAwait(false);
        return
        [
            .. found
                .Where(o => string.Equals(o.Owner, owner, StringComparison.Ordinal) && string.Equals(o.Name, name, StringComparison.Ordinal)
                    && !string.Equals(o.ObjectType, "SYNONYM", StringComparison.Ordinal))
                .Select(o => o.ObjectType)
                .Distinct(StringComparer.Ordinal),
        ];
    }
}
