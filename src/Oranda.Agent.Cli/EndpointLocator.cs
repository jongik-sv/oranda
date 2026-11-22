using System.Globalization;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Agent.Cli;

/// <summary>연결 파일 하나와 그 파일의 상태(앱이 살아 있는가, 기한이 지났는가).</summary>
/// <param name="File">읽은 연결 파일.</param>
/// <param name="Alive">파일의 pid 가 살아 있다.</param>
/// <param name="Expired">기한(<c>expires_at</c>)이 지났다.</param>
internal sealed record EndpointCandidate(AgentEndpointFile File, bool Alive, bool Expired)
{
    /// <summary>붙을 수 있는 파일(살아 있고 기한 안).</summary>
    public bool Usable => Alive && !Expired;

    /// <summary>목록·오류에 보일 요약(토큰 없음).</summary>
    public JsonObject Summary() => new()
    {
        ["pid"] = File.Pid,
        ["label"] = File.Label,
        ["started_at"] = File.StartedAt.ToString("O", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// 외부 연결 파일(<c>&lt;데이터 폴더&gt;/run/agents/&lt;pid&gt;.json</c>)을 찾아 붙을 창 하나를 고른다(설계 §3.5, 결정 E-5).
/// 고르는 순서: <c>--instance</c>(pid 또는 label) → 환경 변수 <c>ORANDA_INSTANCE</c> → 붙을 수 있는 파일이 하나면 그것 → 여럿이면 <c>multiple_instances</c>.
/// 「마지막에 쓴 창」을 자동으로 고르지 않는다. 읽지 못하거나 틀린 파일·죽은 pid 의 파일은 건너뛰고 지우지 않는다(지우기는 앱 몫).
/// </summary>
internal sealed class EndpointLocator(string dataDirectory, Func<int, bool> isAlive, Func<DateTimeOffset> now)
{
    /// <summary>창을 고르는 환경 변수.</summary>
    public const string InstanceEnvironmentVariable = "ORANDA_INSTANCE";

    /// <summary>연결 파일 폴더.</summary>
    public string AgentsDirectory { get; } = AppDataLocation.AgentsDirectory(dataDirectory);

    /// <summary>읽을 수 있는 연결 파일 전부(pid 순). 죽은 pid·만료 파일도 상태와 함께 준다.</summary>
    public IReadOnlyList<EndpointCandidate> Scan()
    {
        if (!Directory.Exists(AgentsDirectory))
        {
            return [];
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(AgentsDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var at = now();
        var found = new List<EndpointCandidate>();
        foreach (var path in paths)
        {
            var pid = AppDataLocation.PidFromEndpointFileName(Path.GetFileName(path));
            if (pid is null || Read(path) is not { } file || file.Pid != pid)
            {
                continue;
            }

            found.Add(new EndpointCandidate(file, isAlive(file.Pid), file.IsExpired(at)));
        }

        found.Sort((a, b) => a.File.Pid.CompareTo(b.File.Pid));
        return found;
    }

    /// <summary>붙을 창 하나. 고르지 못하면 <see cref="BridgeException"/>(코드 = 계약 표, 후보 목록은 <see cref="BridgeException.Details"/>).</summary>
    /// <param name="instance"><c>--instance</c> 값 또는 <c>ORANDA_INSTANCE</c>. 없으면 null.</param>
    public AgentEndpointFile Choose(string? instance)
    {
        var all = Scan();
        var usable = all.Where(c => c.Usable).ToList();
        var pool = string.IsNullOrWhiteSpace(instance) ? usable : usable.Where(c => Matches(c, instance)).ToList();

        if (pool.Count == 1)
        {
            return pool[0].File;
        }

        if (pool.Count > 1)
        {
            throw new BridgeException(
                AgentErrorCodes.MultipleInstances,
                CliText.MultipleInstances,
                new JsonObject { ["instances"] = Summaries(pool) });
        }

        // 고를 것이 없다. 살아 있는데 기한만 지난 창이 있으면 그 사실을 알린다(연결 창에서 연장하면 된다).
        var expired = all.Where(c => c.Alive && c.Expired && (string.IsNullOrWhiteSpace(instance) || Matches(c, instance))).ToList();
        if (expired.Count > 0)
        {
            throw new BridgeException(AgentErrorCodes.GrantExpired, CliText.GrantExpired);
        }

        if (!string.IsNullOrWhiteSpace(instance))
        {
            throw new BridgeException(
                AgentErrorCodes.InstanceNotFound,
                CliText.InstanceNotFound(instance),
                new JsonObject { ["instances"] = Summaries(usable) });
        }

        throw new BridgeException(AgentErrorCodes.NoEndpoint, CliText.NoEndpoint);
    }

    /// <summary>pid 가 살아 있는가(지금 프로세스 표).</summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool Matches(EndpointCandidate candidate, string instance)
    {
        var text = instance.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return candidate.File.Pid == pid;
        }

        return string.Equals(candidate.File.Label, text, StringComparison.Ordinal);
    }

    private static JsonArray Summaries(IEnumerable<EndpointCandidate> candidates) => [.. candidates.Select(c => (JsonNode)c.Summary())];

    private static AgentEndpointFile? Read(string path)
    {
        try
        {
            return AgentEndpointFile.TryParse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
