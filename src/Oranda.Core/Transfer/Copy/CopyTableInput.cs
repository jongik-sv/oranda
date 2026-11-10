using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 표 하나를 더할 때 원본에서 풀린 실제 계정·표. <see cref="WrittenAs"/> 는 시노님을 거쳤을 때만 사용자가 적은 이름이다(표시용, <see cref="TableRule.WrittenAs"/>).
/// </summary>
public sealed record CopyTableName(string Owner, string Table, string? WrittenAs = null)
{
    public bool ViaSynonym => WrittenAs is not null;
}

/// <summary>풀이 결과. 성공이면 <see cref="Table"/>, 아니면 <see cref="Problem"/>(사용자에게 그대로 보이는 글).</summary>
public sealed record CopyTableResolution(CopyTableName? Table, string? Problem)
{
    public bool Found => Table is not null;

    public static CopyTableResolution Ok(CopyTableName table) => new(table, null);

    public static CopyTableResolution Fail(string problem) => new(null, problem);
}

/// <summary>
/// 복사 표 입력(<c>OWNER.NAME</c> 또는 <c>NAME</c>)을 원본 세션의 이름 풀이 한 단계(<see cref="IMetadataProvider.GetNameStepAsync"/>)로 실제 계정·표로 푼다(묶음 B).
/// 입력·스키마 브라우저에서 표를 더할 때 한 번 풀고 풀린 계정·표를 저장한다. 실행 때는 다시 풀지 않는다(사전 점검 문장과 실행 문장이 같아야 한다).
/// <list type="bullet">
/// <item><c>OWNER.NAME</c>: 적은 계정만 본다(PUBLIC 시노님을 보지 않는다).</item>
/// <item><c>NAME</c>: 기본 원본 계정 → (다르면) 원본 세션 사용자 → PUBLIC 시노님 순서로 처음 나오는 곳.</item>
/// <item>시노님은 <see cref="SynonymResolver"/> 로 따라간다(8단계·순환 방지). DB 링크 시노님은 더하지 않는다.</item>
/// </list>
/// 큰따옴표로 감싼 이름은 대소문자 그대로, 아니면 대문자로 바꾼다.
/// </summary>
public static class CopyTableInput
{
    /// <summary>입력을 (계정, 이름)으로 나눈다. 계정을 적지 않았으면 Owner 가 null. 쓸 수 없는 입력이면 Problem.</summary>
    public static (string? Owner, string Name, string? Problem) Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Trim();
        if (text.Length == 0)
        {
            return (null, string.Empty, "테이블 이름을 입력하세요.");
        }

        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var wasQuoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                quoted = !quoted;
                wasQuoted = true;
                continue;
            }

            if (!quoted && c == '@')
            {
                return (null, string.Empty, $"{text}: DB 링크(@)가 붙은 이름은 다른 DB 의 표입니다. 그 DB 를 원본 접속으로 고르세요.");
            }

            if (!quoted && c == '.')
            {
                parts.Add(Finish(current, wasQuoted));
                current.Clear();
                wasQuoted = false;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                return (null, string.Empty, $"{text}: 이름에 공백이 있습니다. 공백이 든 이름은 큰따옴표로 감싸세요.");
            }

            current.Append(c);
        }

        if (quoted)
        {
            return (null, string.Empty, $"{text}: 닫히지 않은 큰따옴표가 있습니다.");
        }

        parts.Add(Finish(current, wasQuoted));
        if (parts.Count > 2 || parts.Any(p => p.Length == 0))
        {
            return (null, string.Empty, $"{text}: 테이블 이름은 「테이블」 또는 「계정.테이블」 꼴로 적으세요.");
        }

        return parts.Count == 2 ? (parts[0], parts[1], null) : (null, parts[0], null);
    }

    /// <summary>(계정, 이름)을 다시 입력 글로(필요할 때만 큰따옴표). <see cref="Parse"/> 와 왕복한다.</summary>
    public static string Format(string? owner, string name)
        => owner is null ? SqlIdentifier.Quote(name) : SqlIdentifier.Quote(owner) + "." + SqlIdentifier.Quote(name);

    /// <summary>
    /// 입력을 푼다. <paramref name="step"/> 은 원본 세션의 이름 풀이 한 단계(사전에 없으면 null).
    /// <paramref name="defaultSourceOwner"/> 는 작업의 기본 원본 계정, <paramref name="sessionUser"/> 는 원본 세션 사용자(모르면 null).
    /// </summary>
    public static async Task<CopyTableResolution> ResolveAsync(Func<string, string, Task<NameStep?>> step, string input,
                                                               string? defaultSourceOwner, string? sessionUser)
    {
        ArgumentNullException.ThrowIfNull(step);
        var (owner, name, problem) = Parse(input);
        if (problem is not null)
        {
            return CopyTableResolution.Fail(problem);
        }

        // 같은 (계정, 이름)을 두 번 묻지 않는다(첫 단계를 찾은 뒤 풀이기가 같은 단계를 다시 묻는다).
        var seen = new Dictionary<(string, string), NameStep?>();
        async Task<NameStep?> Cached(string o, string n)
        {
            if (!seen.TryGetValue((o, n), out var hit))
            {
                hit = await step(o, n).ConfigureAwait(false);
                seen[(o, n)] = hit;
            }

            return hit;
        }

        var written = Format(owner, name);
        var candidates = owner is not null
            ? [owner]
            : new[] { defaultSourceOwner, sessionUser, "PUBLIC" }.Where(o => !string.IsNullOrEmpty(o)).Distinct(StringComparer.Ordinal).Cast<string>().ToList();
        string? start = null;
        foreach (var candidate in candidates)
        {
            if (await Cached(candidate, name).ConfigureAwait(false) is not null)
            {
                start = candidate;
                break;
            }
        }

        if (start is null)
        {
            return CopyTableResolution.Fail(owner is not null
                ? $"{written}: 원본에서 찾을 수 없습니다(없거나 권한이 없습니다)."
                : $"{written}: 원본에서 찾을 수 없습니다(기본 계정·접속 계정의 테이블·시노님과 PUBLIC 시노님을 봤습니다).");
        }

        DbObjectRef resolved;
        try
        {
            resolved = await SynonymResolver.ResolveAsync(Cached, new DbObjectRef(start, name, "TABLE"), ownerWritten: true,
                (synonym, link) => $"{written}: 시노님 {synonym} 은(는) DB 링크({link})를 거쳐 다른 DB 의 표입니다. 그 DB 를 원본 접속으로 고르세요.")
                .ConfigureAwait(false);
        }
        catch (SynonymResolutionException ex)
        {
            return CopyTableResolution.Fail(ex.Message);
        }

        var viaSynonym = !string.Equals(resolved.Owner, start, StringComparison.Ordinal) || !string.Equals(resolved.Name, name, StringComparison.Ordinal);
        if (!viaSynonym)
        {
            return CopyTableResolution.Ok(new CopyTableName(start, name));
        }

        // 시노님이 가리키는 표가 보이지 않으면(없거나 권한 없음) 더하지 않는다: 사전 점검에서야 알게 되면 고치기 어렵다.
        if (await Cached(resolved.Owner, resolved.Name).ConfigureAwait(false) is null)
        {
            return CopyTableResolution.Fail($"{written}: 시노님이 가리키는 {resolved.Owner}.{resolved.Name} 을(를) 원본에서 찾을 수 없습니다(없거나 권한이 없습니다).");
        }

        return CopyTableResolution.Ok(new CopyTableName(resolved.Owner, resolved.Name, written));
    }

    private static string Finish(System.Text.StringBuilder part, bool quoted)
        => quoted ? part.ToString() : part.ToString().ToUpperInvariant();
}
