using System.Text.Json;

namespace Oranda.Perf.Common;

/// <summary>
/// 측정용 기능 구성(TSK-07-07, TRD 10.10). <c>--features</c> 값을 읽어 데이터 폴더 <c>settings.json</c> 의 <c>features</c> 섹션을 쓴다.
/// 값 형식: <c>all</c>(설정 없음 = 모든 기능, V0) · Role 쉼표 목록(<c>sqlDev</c>, <c>tuner,dba</c>, <c>viewer</c>, 별칭 r1~r6) ·
/// <c>none</c>(Role 을 비움 = 공통만) · 뒤에 <c>+기능</c>(직접 켬)·<c>-기능</c>(직접 끔)을 덧붙일 수 있다(<c>sqlDev,-blockerAlert</c>, <c>all,-blockerAlert</c>).
/// Oranda.Core 를 참조하지 않으려고 id 표를 여기에 복사해 둔다(카탈로그가 바뀌면 FeaturePresetTests 의 표 비교 시험이 알려 준다).
/// </summary>
public sealed record FeaturePreset(string Label, IReadOnlyList<string>? Roles, IReadOnlyList<string> Enabled, IReadOnlyList<string> Disabled)
{
    /// <summary>Role id 6개(RoleIds, PRD 12.1).</summary>
    public static readonly string[] RoleIdList = ["sqlDev", "plsqlDev", "myBatisDev", "tuner", "dba", "viewer"];

    /// <summary>기능 id 25개(FeatureIds.All, PRD 12.2).</summary>
    public static readonly string[] FeatureIdList =
    [
        "gridEdit", "lobRowView", "dataExport", "dataImport", "dedicatedSession", "flashback", "changeJournal", "tableSpec", "myBatisLog",
        "plsqlEditor", "plsqlWorkspace", "plsqlRun",
        "myBatis",
        "planAdvanced", "sqlMonitor", "tuningAssistant", "childCursors", "tableStats", "trace", "tuningReport",
        "sessionMonitor", "lockMonitor", "blockerAlert", "dbTools", "columnReorder",
    ];

    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    /// <summary>features 섹션을 쓰지 않는 구성(V0: 설정 없음 = 모든 기능).</summary>
    public bool IsDefault => Roles is null && Enabled.Count == 0 && Disabled.Count == 0;

    public static FeaturePreset Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("--features 값이 비었다. all, Role 쉼표 목록, none 중 하나여야 한다.");
        }

        var roles = new List<string>();
        var enabled = new List<string>();
        var disabled = new List<string>();
        var sawAll = false;
        var sawNone = false;
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('+') || raw.StartsWith('-'))
            {
                var feature = FindFeature(raw[1..]) ?? throw new ArgumentException($"모르는 기능 id: {raw[1..]} (--features {text})");
                (raw[0] == '+' ? enabled : disabled).Add(feature);
                continue;
            }

            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                sawAll = true;
            }
            else if (raw.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                sawNone = true;
            }
            else if (Aliases.TryGetValue(raw, out var role))
            {
                if (!roles.Contains(role))
                {
                    roles.Add(role);
                }
            }
            else
            {
                throw new ArgumentException($"모르는 Role id: {raw}. 쓸 수 있는 것: all, none, {string.Join(", ", RoleIdList)} (또는 r1~r6)");
            }
        }

        if ((sawAll && (sawNone || roles.Count > 0)) || (sawNone && roles.Count > 0))
        {
            throw new ArgumentException($"all, none, Role 목록은 함께 쓸 수 없다: {text}");
        }

        if (!sawAll && !sawNone && roles.Count == 0)
        {
            throw new ArgumentException($"Role 이나 all/none 이 있어야 한다(+/- 만으로는 안 된다): {text}");
        }

        IReadOnlyList<string>? resolved = sawAll ? null : roles;
        return new FeaturePreset(text.Trim(), resolved, enabled, disabled);
    }

    /// <summary>settings.json 전체 글. features 섹션이 필요 없으면(<see cref="IsDefault"/>) null.</summary>
    public string? ToSettingsJson()
    {
        if (IsDefault)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteStartObject("features");
            if (Roles is null)
            {
                w.WriteNull("roles");
            }
            else
            {
                WriteList(w, "roles", Roles);
            }

            WriteList(w, "enabled", Enabled);
            WriteList(w, "disabled", Disabled);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 데이터 폴더에 구성을 쓴다. V0 이면 기존 settings.json 을 지운다(앱이 이전 실행에서 쓴 features 가 남지 않게).
    /// 폴더가 없으면 만든다. 사용자의 실제 설정 폴더가 아니라 측정용 임시 폴더만 넘길 것.
    /// </summary>
    public void WriteSettings(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        Directory.CreateDirectory(dataDir);
        var path = Path.Combine(dataDir, "settings.json");
        var json = ToSettingsJson();
        if (json is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        File.WriteAllText(path, json);
    }

    private static void WriteList(Utf8JsonWriter w, string name, IEnumerable<string> items)
    {
        w.WriteStartArray(name);
        foreach (var i in items)
        {
            w.WriteStringValue(i);
        }

        w.WriteEndArray();
    }

    private static string? FindFeature(string id) =>
        FeatureIdList.FirstOrDefault(f => f.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < RoleIdList.Length; i++)
        {
            map[RoleIdList[i]] = RoleIdList[i];
            map["r" + (i + 1)] = RoleIdList[i];
        }

        return map;
    }
}

/// <summary>앱 Trace 로그(<c>logs/oranda-yyyyMMdd.log</c>)에서 시작 작업·상태 칸 개수를 읽는다(TSK-07-03 이 남기는 줄).</summary>
/// <param name="TasksRan">「시작 작업: 돈 N개, 건너뜀 M개」 의 N. 줄이 없으면 null.</param>
/// <param name="TasksSkipped">같은 줄의 M.</param>
/// <param name="StatusShown">「상태 칸: N개, 건너뜀 M개」 의 N. 이 줄은 건너뛴 칸이 있을 때만 남으므로 없으면 null(= 건너뜀 0).</param>
/// <param name="StatusSkipped">「상태 칸 건너뜀: 형」 줄 수.</param>
public sealed record LogCounts(int? TasksRan, int? TasksSkipped, int? StatusShown, int StatusSkipped)
{
    public static LogCounts Parse(string? text)
    {
        int? ran = null, skipped = null, shown = null;
        var statusSkipped = 0;
        if (text is null)
        {
            return new LogCounts(null, null, null, 0);
        }

        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (TryTwo(l, "시작 작업: 돈 ", out var a, out var b))
            {
                (ran, skipped) = (a, b);
            }
            else if (TryTwo(l, "상태 칸: ", out var c, out _))
            {
                shown = c;
            }
            else if (l.Contains("상태 칸 건너뜀: ", StringComparison.Ordinal))
            {
                statusSkipped++;
            }
        }

        return new LogCounts(ran, skipped, shown, statusSkipped);
    }

    // "<머리><N>개, 건너뜀 <M>개" 에서 N, M 을 읽는다. 머리는 줄 안 어디에 있어도 된다.
    private static bool TryTwo(string line, string head, out int first, out int second)
    {
        first = second = 0;
        var i = line.IndexOf(head, StringComparison.Ordinal);
        if (i < 0)
        {
            return false;
        }

        var rest = line[(i + head.Length)..];
        var end = rest.IndexOf("개, 건너뜀 ", StringComparison.Ordinal);
        if (end < 0 || !int.TryParse(rest[..end], out first))
        {
            return false;
        }

        var tail = rest[(end + "개, 건너뜀 ".Length)..];
        var j = tail.IndexOf('개');
        return j > 0 && int.TryParse(tail[..j], out second);
    }
}
