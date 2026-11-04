using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

/// <summary>생성하는 플러그인: 토큰이 없고, 도구 목록·파이프·표식 경로가 JSON 상수로 들어가며, 파일 권한이 실행 사용자 전용이다.</summary>
public sealed class OpencodePluginFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-oc-plugin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private static List<AgentToolInfo> Tools() =>
        [new("describe_object", "설명 \"따옴표\"와 역슬래시 \\ 가 있는 설명", JsonNode.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}""")!)];

    /// <summary>소스의 <c>const RUN = JSON.parse("…");</c> 문자열 리터럴을 풀어 JSON 으로 읽는다.</summary>
    private static JsonDocument ReadRun(string source)
    {
        const string Prefix = "const RUN = JSON.parse(";
        var start = source.IndexOf(Prefix, StringComparison.Ordinal) + Prefix.Length;
        var end = source.IndexOf(");\n", start, StringComparison.Ordinal);
        return JsonDocument.Parse(JsonSerializer.Deserialize<string>(source[start..end])!);
    }

    [Fact]
    public void A_proto_key_in_a_schema_stays_an_own_property_because_the_constant_is_parsed_from_a_string()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"__proto__":{"type":"string"}}}""");

        var source = OpencodePluginFile.Render(_dir, "p", Tools(), schema.RootElement);

        Assert.DoesNotContain("const RUN = {", source, StringComparison.Ordinal); // 객체 리터럴이면 __proto__ 가 프로토타입을 바꾼다
        using var run = ReadRun(source);
        Assert.True(run.RootElement.GetProperty("finish").GetProperty("schema").GetProperty("properties").TryGetProperty("__proto__", out _));
    }

    [Fact]
    public void The_source_never_contains_a_token_and_reads_it_only_from_the_environment()
    {
        var source = OpencodePluginFile.Render(_dir, "/run/agent.sock", Tools(), null);

        Assert.Contains("process.env.ORANDA_RUN_TOKEN", source, StringComparison.Ordinal);
        Assert.DoesNotContain("__RUN__", source, StringComparison.Ordinal);
        // 도구 호출은 브리지(게이트 경로)로만 간다: hello 다음 callTool.
        Assert.Contains("oranda.hello", source, StringComparison.Ordinal);
        Assert.Contains("oranda.callTool", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_pipe_and_marker_paths_are_embedded_as_one_json_constant()
    {
        var source = OpencodePluginFile.Render(_dir, "/run/agent.sock", Tools(), null);

        using var run = ReadRun(source);
        var root = run.RootElement;

        Assert.Equal(OperatingSystem.IsWindows() ? @"\\.\pipe\/run/agent.sock" : "/run/agent.sock", root.GetProperty("pipe").GetString());
        Assert.Equal(Path.Combine(_dir, OpencodePluginFile.ReadyFileName), root.GetProperty("readyPath").GetString());
        Assert.Equal(Path.Combine(_dir, OpencodePluginFile.FailedFileName), root.GetProperty("failedPath").GetString());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("describe_object", tool.GetProperty("name").GetString());
        Assert.Equal("설명 \"따옴표\"와 역슬래시 \\ 가 있는 설명", tool.GetProperty("description").GetString());
        Assert.False(root.TryGetProperty("finish", out _));
    }

    [Fact]
    public void The_finish_tool_exists_only_for_a_structured_run()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"answer":{"type":"string"}}}""");

        var source = OpencodePluginFile.Render(_dir, "p", Tools(), schema.RootElement);

        using var run = ReadRun(source);
        var finish = run.RootElement.GetProperty("finish");
        Assert.Equal(OpencodeArguments.FinishToolName, finish.GetProperty("name").GetString());
        Assert.Equal("object", finish.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void The_folder_and_the_file_are_private_to_the_user()
    {
        var path = OpencodePluginFile.Write(Path.Combine(_dir, "config"), "p", Tools(), null);

        Assert.True(File.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(_dir, "config")));
        }
    }

    [Fact]
    public void The_generated_source_is_valid_javascript()
    {
        // node 가 없는 환경에서는 건너뛴다(구문 검사만 하고 실행하지 않는다).
        var path = OpencodePluginFile.Write(Path.Combine(_dir, "config"), "p", Tools(), null);
        var start = new ProcessStartInfo("node", ["--check", path]) { RedirectStandardError = true, RedirectStandardOutput = true };
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;
        }

        using (process)
        {
            Assert.NotNull(process);
            process.WaitForExit(30_000);
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
    }
}
