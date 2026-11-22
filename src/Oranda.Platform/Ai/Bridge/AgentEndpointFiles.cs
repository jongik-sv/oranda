using System.Diagnostics;
using System.Text;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Storage;

namespace Oranda.Platform.Ai.Bridge;

/// <summary>
/// 외부 연결 파일 쓰기(설계 agent-cli-control §3.3, A4). <c>&lt;데이터 폴더&gt;/run/agents/&lt;pid&gt;.json</c> 을 폴더 0700·파일 0600 으로 쓴다
/// (<c>McpConfigWriter</c> 와 같은 방식). 다른 이름으로 다 쓴 뒤 바꿔 넣어, 읽는 쪽(<c>orandactl</c>)이 반쯤 쓴 파일을 보지 않게 한다.
/// 허가 저장소(<see cref="IAgentGrantSessions.AttachWriter"/>)가 만들기·바뀜·끝남 때 부른다.
/// </summary>
internal sealed class AgentEndpointFiles(string dataDirectory, AgentEndpoint endpoint, string appVersion, int pid) : IAgentEndpointWriter
{
    private readonly Lock _gate = new();

    /// <summary>이 앱의 연결 파일 경로.</summary>
    public string FilePath { get; } = AppDataLocation.EndpointFile(dataDirectory, pid);

    public void Publish(AgentGrantIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var grant = issue.Grant;
        var file = new AgentEndpointFile(
            AgentEndpointFile.CurrentVersion,
            pid,
            grant.CreatedAt.ToLocalTime(),
            appVersion,
            grant.Label,
            endpoint,
            issue.Token,
            grant.ExpiresAt?.ToLocalTime(),
            Connections(grant));
        lock (_gate)
        {
            Write(file);
        }
    }

    public void Update(AgentGrantView grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        lock (_gate)
        {
            // 토큰은 메모리에 없으므로 이미 쓴 파일에서 읽어 나머지만 고친다. 파일이 없으면(지워졌으면) 되살리지 않는다.
            if (!File.Exists(FilePath) || AgentEndpointFile.TryParse(File.ReadAllText(FilePath, Encoding.UTF8)) is not { } current)
            {
                return;
            }

            Write(current with
            {
                Label = grant.Label,
                ExpiresAt = grant.ExpiresAt?.ToLocalTime(),
                Connections = Connections(grant),
            });
        }
    }

    public void Remove(AgentGrantView grant)
    {
        lock (_gate)
        {
            File.Delete(FilePath);
        }
    }

    /// <summary>
    /// 앱이 열 때 한 번: 이 pid 의 지난 파일(앞 실행이 비정상 종료해 같은 pid 를 받은 경우)과 살아 있지 않은 pid 의 파일, 쓰다 남은 임시 파일을 지운다.
    /// 이름이 <c>&lt;pid&gt;.json</c> 꼴이 아닌 파일은 건드리지 않는다. 지운 수를 돌려준다.
    /// </summary>
    public static int CleanStale(string dataDirectory, int currentPid, Func<int, bool> isAlive)
    {
        ArgumentNullException.ThrowIfNull(isAlive);
        var directory = AppDataLocation.AgentsDirectory(dataDirectory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            var stale = name.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase)
                || (AppDataLocation.PidFromEndpointFileName(name) is { } pid && (pid == currentPid || !isAlive(pid)));
            if (!stale)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 다른 앱이 쓰는 중일 수 있다. 다음 기회에 지운다.
            }
        }

        return removed;
    }

    /// <summary>pid 의 프로세스가 살아 있는가.</summary>
    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static List<AgentEndpointConnection> Connections(AgentGrantView grant) =>
        [.. grant.Connections.Select(c => new AgentEndpointConnection(c.Alias, c.Environment, c.Level))];

    private void Write(AgentEndpointFile file)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        PrivateDirectory.Ensure(directory);
        var temp = FilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(file.Serialize());
        }

        File.Move(temp, FilePath, overwrite: true);
    }
}
