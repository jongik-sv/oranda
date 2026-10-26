using System.Reflection;

namespace Oranda.App.Features.Update;

/// <summary>앱에 포함된 릴리스 노트 한 편. <see cref="Version"/> 은 파일 이름(확장자 뺀 값)이다.</summary>
public sealed record ReleaseNoteEntry(string Version, string Markdown);

/// <summary>
/// 앱에 임베디드 리소스로 포함된 릴리스 노트(<c>docs/release-notes/&lt;버전&gt;.md</c>, 논리 이름 <c>Oranda.App.ReleaseNotes.&lt;버전&gt;.md</c>)를 읽는다.
/// 오프라인에서도 보이도록 피드를 거치지 않는다. 리소스는 창을 열 때만 읽으며 시작 경로에서는 부르지 않는다.
/// </summary>
public static class ReleaseNotesCatalog
{
    private const string Prefix = "Oranda.App.ReleaseNotes.";
    private const string Suffix = ".md";

    /// <summary>업데이트 피드가 GitHub 주소가 아닐 때 「모든 릴리스 보기」 가 여는 저장소(<c>Directory.Build.props</c> 의 <c>PackageProjectUrl</c> 과 같다).</summary>
    public const string DefaultRepository = "https://github.com/jongik-sv/oranda";

    /// <summary>포함된 노트를 새 버전부터 읽는다.</summary>
    public static IReadOnlyList<ReleaseNoteEntry> Load(Assembly? assembly = null)
    {
        assembly ??= typeof(ReleaseNotesCatalog).Assembly;
        var entries = new List<ReleaseNoteEntry>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            entries.Add(new ReleaseNoteEntry(name[Prefix.Length..^Suffix.Length], reader.ReadToEnd()));
        }

        return Sort(entries);
    }

    /// <summary>새 버전부터 정렬한다. 버전으로 읽을 수 없는 이름은 뒤에 이름순으로 둔다.</summary>
    internal static List<ReleaseNoteEntry> Sort(IEnumerable<ReleaseNoteEntry> entries) =>
        [.. entries
            .OrderByDescending(e => Parse(e.Version) is not null)
            .ThenByDescending(e => Parse(e.Version))
            .ThenBy(e => e.Version, StringComparer.Ordinal)];

    /// <summary>
    /// 실행 중인 버전의 노트를 맨 앞에 두고, 그보다 이전 버전의 노트를 새 버전부터 잇는다. 실행 중인 버전의 노트가 없으면(로컬 빌드)
    /// <c>Current</c> 는 null 이고 포함된 노트 전체를 새 버전부터 둔다.
    /// </summary>
    public static ReleaseNotesSelection Select(IReadOnlyList<ReleaseNoteEntry> entries, string runningVersion)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sorted = Sort(entries);
        var running = Parse(runningVersion);
        var current = running is null ? null : sorted.FirstOrDefault(e => Parse(e.Version) == running);
        if (current is null)
        {
            return new ReleaseNotesSelection(null, sorted);
        }

        var older = sorted.Where(e => Parse(e.Version) is { } v && running is { } r && v < r);
        return new ReleaseNotesSelection(current, [current, .. older]);
    }

    /// <summary>「모든 릴리스 보기」 주소: GitHub 피드면 그 저장소, 아니면 <see cref="DefaultRepository"/> 의 <c>/releases</c>.</summary>
    public static string ReleasesUrl(UpdateFeed? feed) =>
        (feed is { Kind: UpdateFeedKind.GitHub } ? feed.Location.TrimEnd('/') : DefaultRepository) + "/releases";

    /// <summary><c>0.1.2</c>·<c>0.1.2-beta</c>·<c>0.1.2+abc</c>·<c>0.1.2.0</c> 를 같은 <c>0.1.2</c> 로 읽는다. 읽을 수 없으면 null.</summary>
    internal static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim();
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            core = core[..cut];
        }

        if (!Version.TryParse(core, out var v))
        {
            return null;
        }

        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}

/// <summary><see cref="ReleaseNotesCatalog.Select"/> 결과. <see cref="Current"/> 가 null 이면 실행 중인 버전의 노트가 포함되지 않았다.</summary>
public sealed record ReleaseNotesSelection(ReleaseNoteEntry? Current, IReadOnlyList<ReleaseNoteEntry> Ordered);
