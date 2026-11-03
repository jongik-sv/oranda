using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 도구를 뺀 Codex 모델 카탈로그. 모델 카탈로그의 <c>tool_mode</c>·<c>apply_patch_tool_type</c>·<c>multi_agent_version</c> 이 기능 플래그보다 우선해서,
/// 카탈로그 없이 돌리면 코드 모드(exec)와 apply_patch 가 되살아난다. 그래서 <c>codex debug models</c> 출력의 각 모델에 값을 덮어쓴 파일을 실행마다 만든다.
/// 출력 형식이 예상과 다르면(필드 이름이 바뀌면 덮어쓰기가 조용히 효과를 잃는다) 만들지 않고 실패한다 — 실행하지 않고 닫는다(fail-closed).
/// </summary>
internal static class CodexModelCatalog
{
    private static readonly TimeSpan DebugModelsTimeout = TimeSpan.FromSeconds(30);

    /// <summary>덮어쓸 키와 값. 키는 목록의 모델 중 적어도 하나에 있어야 한다(<see cref="Lock"/>).</summary>
    private static readonly (string Key, Func<JsonNode?> Value)[] Overrides =
    [
        ("tool_mode", () => null),
        ("apply_patch_tool_type", () => null),
        ("multi_agent_version", () => null),
        ("experimental_supported_tools", () => new JsonArray()),
        ("supports_search_tool", () => JsonValue.Create(false)),
    ];

    /// <summary><c>codex debug models</c> 출력(JSON 글)을 잠근 카탈로그 글로 바꾼다. 실패하면 오류 사유.</summary>
    public static (string? Json, string? Error) Lock(string debugModelsOutput)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(debugModelsOutput);
        }
        catch (JsonException)
        {
            return (null, "모델 목록이 JSON 이 아닙니다.");
        }

        if (root is not JsonObject { } obj || obj["models"] is not JsonArray { Count: > 0 } models)
        {
            return (null, "모델 목록의 형식이 예상과 다릅니다(models 배열이 없거나 비어 있습니다).");
        }

        var objects = new List<JsonObject>();
        foreach (var node in models)
        {
            if (node is not JsonObject model)
            {
                return (null, "모델 목록의 항목이 객체가 아닙니다.");
            }

            objects.Add(model);
        }

        // 값이 없는 필드는 키를 생략하는 모델이 있다(예: 번들 카탈로그의 gpt-5.5 는 tool_mode 가 없다). 한 모델에서 키가 없는 것은 「이미 값 없음」이다.
        // 그러나 어느 모델에도 키가 없으면 필드 이름이 바뀐 것일 수 있다(진짜 필드가 살아 있는 채 덮어쓰기가 효과를 잃는다). 그때는 닫는다.
        foreach (var (key, _) in Overrides)
        {
            if (!objects.Any(m => m.ContainsKey(key)))
            {
                return (null, $"모델 목록 어디에도 예상한 필드({key})가 없습니다. Codex 버전이 바뀌었을 수 있습니다.");
            }
        }

        foreach (var model in objects)
        {
            foreach (var (key, value) in Overrides)
            {
                model[key] = value();
            }
        }

        return (root.ToJsonString(), null);
    }

    /// <summary>실행 파일의 <c>debug models</c> 를 돌려 출력 글을 얻는다. 토큰 환경 변수는 주지 않는다.</summary>
    public static async Task<(string? Output, string? Error)> RunDebugModelsAsync(string executable, string workingDirectory, CancellationToken ct)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        info.ArgumentList.Add("debug");
        info.ArgumentList.Add("models");
        SystemClaudeProcessLauncher.ApplyEnvironment(info.Environment, new ClaudeLaunch(executable, [], workingDirectory, string.Empty));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DebugModelsTimeout);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("프로세스를 시작하지 못했습니다.");
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(limit.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? (output, null) : (null, $"codex debug models 가 실패했습니다(종료 코드 {process.ExitCode}).");
            }
            catch
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 이미 끝났다.
                }

                _ = stderr.Exception;
                throw;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "codex debug models 가 제한 시간 안에 끝나지 않았습니다.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (null, $"codex debug models 를 실행하지 못했습니다: {ex.Message}");
        }
    }
}
