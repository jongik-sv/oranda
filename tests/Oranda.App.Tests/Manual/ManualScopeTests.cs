using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell;

namespace Oranda.App.Tests.Manual;

/// <summary>
/// 매뉴얼 범위표(<c>site/scope.json</c>) 검사. 코드에서 뽑은 명령·도구 창·상태 표시줄 항목·대화상자·설정 탭이 모두 표에 있고,
/// 표의 모든 경로가 본문(<c>site/src/content/docs</c>)에 실제로 있어야 한다. 의도적 제외는 사유를 적어야 한다.
/// 이 표가 「모든 기능을 설명했다」 의 완성 판정 기준이다.
/// </summary>
public sealed partial class ManualScopeTests
{
    private static readonly string[] Kinds = ["commands", "panels", "statusBar", "dialogs", "dialogSources", "settings"];

    /// <summary>종류별 최소 개수. 스캔이 비어 표도 비어 있는 채로 통과하는 일을 막는다.</summary>
    private static readonly Dictionary<string, int> MinimumCounts = new(StringComparer.Ordinal)
    {
        ["commands"] = 200,
        ["panels"] = 20,
        ["statusBar"] = 5,
        ["dialogs"] = 15,
        ["dialogSources"] = 3,
        ["settings"] = 5,
    };

    /// <summary>실패 문구에 붙는 고치는 방법. 새 기능을 넣는 레인이 이 문구만 보고 범위표를 고칠 수 있어야 한다.</summary>
    private const string HowToFix = """

        고치는 방법: site/scope.json 의 해당 묶음(commands, panels, statusBar, dialogs, settings)에 한 줄을 더하거나 뺀다.
          항목 추가:   { "id": "<아래 목록의 id>", "page": "<site/src/content/docs 아래 쪽 경로, 확장자 뺌. 예: run/result-grid>" }
          의도적 제외: { "id": "<id>", "exclude": "<사유. 예: 개발 빌드 전용>" }
          배정할 쪽을 모를 때: "page": "start/unassigned" 로 둔다(manual-site 레인이 나중에 옮긴다).
          쪽 경로는 site/src/content/docs 에 실제로 있는 파일이어야 한다.
        """;

    [Fact]
    public void Scope_table_covers_every_scanned_item_and_nothing_else()
    {
        var table = LoadTable();
        var problems = new List<string>();
        foreach (var kind in Kinds)
        {
            var scanned = Scan(kind);
            Assert.True(scanned.Count >= MinimumCounts[kind], $"[{kind}] 코드에서 뽑은 항목이 {scanned.Count}개뿐이다(최소 {MinimumCounts[kind]}개). 스캔 방법이 깨졌다.");
            var listed = table[kind].Keys.ToHashSet(StringComparer.Ordinal);
            problems.AddRange(scanned.Except(listed).Order().Select(id => $"[{kind}] 범위표에 없음 → \"{id}\""));
            problems.AddRange(listed.Except(scanned).Order().Select(id => $"[{kind}] 코드에 없는 항목이 테이블에 있음(지워진 기능이면 줄을 뺀다) → \"{id}\""));
        }

        Assert.True(problems.Count == 0, "매뉴얼 범위표(site/scope.json)가 코드와 맞지 않는다." + Environment.NewLine + string.Join(Environment.NewLine, problems) + HowToFix);
    }

    [Fact]
    public void Every_entry_has_an_existing_page_or_a_reason_for_exclusion()
    {
        var problems = new List<string>();
        foreach (var (kind, entries) in LoadTable())
        {
            foreach (var (id, entry) in entries)
            {
                if (entry.Exclude is not null && entry.Page is not null)
                {
                    problems.Add($"{kind}: {id} 에 page 와 exclude 가 함께 있음(하나만 둔다)");
                    continue;
                }

                if (entry.Exclude is not null)
                {
                    if (string.IsNullOrWhiteSpace(entry.Exclude))
                    {
                        problems.Add($"{kind}: {id} 제외 사유가 비어 있음");
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Page))
                {
                    problems.Add($"{kind}: {id} 경로도 제외 사유도 없음");
                    continue;
                }

                var file = ManualSite.FindPage(entry.Page);
                if (file is null)
                {
                    problems.Add($"{kind}: {id} → 본문에 없는 경로 {entry.Page}");
                }
                else if (!ManualSite.AllowDraftPages && ManualSite.IsDraft(file))
                {
                    problems.Add($"{kind}: {id} → 초안 상태인 쪽 {entry.Page}");
                }
            }
        }

        Assert.True(problems.Count == 0, "매뉴얼 범위표(site/scope.json)의 항목이 잘못됐다." + Environment.NewLine + string.Join(Environment.NewLine, problems) + HowToFix);
    }

    [Fact]
    public void Non_draft_pages_only_reference_screen_images_that_exist()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ManualSite.DocsDir, "*.md*", SearchOption.AllDirectories))
        {
            if (ManualSite.IsDraft(file))
            {
                continue; // 초안은 게시 빌드에서 빠지므로 이미지가 아직 없어도 된다.
            }

            foreach (var image in ManualSite.ScreenImages(file))
            {
                if (!File.Exists(Path.Combine(ManualSite.RepoRoot, "site", "src", "assets", "screens", image)))
                {
                    problems.Add($"{Path.GetRelativePath(ManualSite.DocsDir, file)} → 없는 이미지 {image}");
                }
            }
        }

        Assert.True(problems.Count == 0, "게시되는 쪽이 없는 화면 이미지를 참조한다(캡처를 만들거나 쪽을 초안으로 되돌린다)." + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Dump_scanned_items_when_requested()
    {
        var path = Environment.GetEnvironmentVariable("ORANDA_MANUAL_SCOPE_DUMP");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var kind in Kinds)
        {
            foreach (var id in Scan(kind).Order(StringComparer.Ordinal))
            {
                sb.Append(kind).Append('\t').Append(id).Append('\n');
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static SortedSet<string> Scan(string kind)
    {
        var contributions = ShellTestHelpers.AppContributions;
        var ids = kind switch
        {
            "commands" => contributions.Commands.Select(c => c.Definition.Id),
            "panels" => contributions.Panels.Select(p => p.Id),
            "statusBar" => contributions.StatusBarItems.Select(i => i.Id),
            "dialogs" => typeof(ShellContributions).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(Window)))
                .Select(t => t.Name),
            "dialogSources" => DialogSources(),
            "settings" => SettingsTabs(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new SortedSet<string>(ids, StringComparer.Ordinal);
    }

    /// <summary>
    /// Window 파생 클래스 없이 창을 코드에서 직접 만드는(<c>new Window</c>, <c>DialogShell.</c>) 소스 파일: 확인·입력·안내 창 같은 인라인 대화상자.
    /// 항목 id 는 <c>src/Oranda.App</c> 아래 상대 경로다.
    /// </summary>
    private static IEnumerable<string> DialogSources()
    {
        var root = Path.Combine(ManualSite.RepoRoot, "src", "Oranda.App");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (InlineWindowPattern().IsMatch(text) && !WindowClassPattern().IsMatch(text))
            {
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
            }
        }
    }

    [GeneratedRegex(@"new Window\b|DialogShell\.")]
    private static partial Regex InlineWindowPattern();

    [GeneratedRegex(@"class\s+\w+[^{]*:\s*(?:[\w.]+,\s*)*Window\b")]
    private static partial Regex WindowClassPattern();

    /// <summary>설정 창(<c>SettingsWindow.axaml</c>)의 탭: 문구 키(<c>tab.*</c>)와 기능 탭.</summary>
    private static IEnumerable<string> SettingsTabs()
    {
        var axaml = File.ReadAllText(Path.Combine(ManualSite.RepoRoot, "src", "Oranda.App", "Shell", "Settings", "SettingsWindow.axaml"));
        var keys = TabKeyPattern().Matches(axaml).Select(m => m.Groups[1].Value).ToList();
        Assert.True(keys.Count >= 5, "설정 창 탭 문구 키(Strings[tab.*])를 찾지 못했다. SettingsWindow.axaml 의 표기가 바뀌었으면 이 검출을 고친다.");
        return axaml.Contains("Features.TabTitle", StringComparison.Ordinal) ? keys.Append("tab.features") : keys;
    }

    [GeneratedRegex(@"Strings\[(tab\.\w+)\]")]
    private static partial Regex TabKeyPattern();

    private sealed record Entry(string? Page, string? Exclude);

    private static Dictionary<string, Dictionary<string, Entry>> LoadTable()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ManualSite.RepoRoot, "site", "scope.json")));
        var table = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
        foreach (var kind in Kinds)
        {
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.GetProperty(kind).EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var added = entries.TryAdd(id, new Entry(
                    item.TryGetProperty("page", out var page) ? page.GetString() : null,
                    item.TryGetProperty("exclude", out var exclude) ? exclude.GetString() : null));
                Assert.True(added, $"{kind}: {id} 가 두 번 나옴");
            }

            table[kind] = entries;
        }

        return table;
    }
}
