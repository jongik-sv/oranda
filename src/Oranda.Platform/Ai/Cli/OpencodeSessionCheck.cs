using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 이어 말하기 전에 대화에 저장된 권한 규칙이 없는지 확인한다. opencode 는 대화에 저장된 권한 규칙을 에이전트 규칙 <b>뒤</b>에 이어 붙여 마지막에 맞는 규칙을 따르므로
/// (2.0.32 가짜 LLM 으로 확인: 대화에 allow <c>*</c> 가 저장되면 이어 말하기에서 <c>execute</c> 가 되살아나 호출도 성공한다) 전용 에이전트의 deny 로는 막을 수 없다.
/// 대화에 권한을 저장하는 쪽은 사용자의 다른 opencode 화면이나 API 뿐이지만, 그런 대화는 이어 말하지 않는다.
/// <para>
/// 확인은 <c>opencode session export --standalone --sanitize &lt;id&gt;</c> 의 <c>info</c> 로 한다. 닫는 쪽으로만 판단한다:
/// 대화를 못 찾음(<c>--session</c> 은 없는 id 면 조용히 새 대화를 만든다), 출력 해석 불가, 시간 초과, <c>info.id</c> 불일치,
/// 전용 에이전트가 아닌 대화, 이름에 <c>permission</c> 이 들어간 필드가 비어 있지 않음 — 모두 실행하지 않는다.
/// 이름이 바뀌어도 <c>permission</c> 이 들어간 필드는 같이 본다. 남는 틈: 확인과 실행 사이에 다른 쪽이 권한을 저장하는 경우.
/// </para>
/// </summary>
internal static class OpencodeSessionCheck
{
    /// <summary>내보내기 시간 상한. 넘으면 읽지 못한 것으로 보고 닫는다.</summary>
    internal static readonly TimeSpan ExportTimeLimit = TimeSpan.FromSeconds(60);

    /// <summary>내보내기 출력 상한(글자 수). 넘으면 읽지 못한 것으로 보고 닫는다.</summary>
    internal const int MaxExportChars = 64 * 1024 * 1024;

    /// <summary>대화를 내보낸다: 실행 파일·대화 id·시간 상한을 받아 표준 출력 전체를 돌려준다. 종료 코드가 0 이 아니거나 시간 초과·상한 초과면 null.</summary>
    internal delegate Task<string?> Exporter(string executable, string sessionId, TimeSpan limit, CancellationToken ct);

    /// <summary>이어 말해도 되는 대화면 null, 아니면 실행하지 않을 이유.</summary>
    internal static async Task<AiError?> VerifyAsync(Exporter export, string executable, string sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(export);

        string? text;
        try
        {
            text = await export(executable, sessionId, ExportTimeLimit, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is IOException or InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            text = null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return NotVerified("대화를 찾지 못했거나 읽지 못했습니다");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object)
            {
                return NotVerified("대화 정보의 모양을 알 수 없습니다");
            }

            if (!info.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !string.Equals(id.GetString(), sessionId, StringComparison.Ordinal))
            {
                return NotVerified("내보낸 대화가 이어 말할 대화와 다릅니다");
            }

            if (!info.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.String || !string.Equals(agent.GetString(), OpencodeArguments.AgentName, StringComparison.Ordinal))
            {
                return NotVerified("Oranda 가 만든 대화가 아닙니다");
            }

            // 대화 정보 어디에든 이름에 permission 이 든 비어 있지 않은 값이 있거나, 다른 대화에서 이어받은 대화(parent·fork)면 규칙을 물려받을 수 있어 닫는다.
            if (HasStoredPermissions(info) || HasInheritance(info))
            {
                return new AiError(
                    "이어 말할 opencode 대화에 권한 규칙이 저장되어 있거나 다른 대화에서 이어받은 대화여서 잠금을 보장할 수 없으므로 실행하지 않았습니다. 새 대화로 다시 시작하세요.",
                    "resume_permissions_unexpected");
            }

            return null;
        }
        catch (JsonException)
        {
            return NotVerified("대화 정보를 읽을 수 없습니다");
        }
    }

    /// <summary>
    /// 실제 프로세스로 대화를 내보낸다. 실행 토큰 변수는 지우고(<see cref="OpencodeDriver.LaunchEnvironment"/>) 어느 폴더에서 불러도 대화 id 로 찾는다.
    /// 시간 상한을 넘으면 프로세스 트리를 끝내고 null. 호출 측 취소는 예외로 낸다.
    /// </summary>
    internal static async Task<string?> ExportAsync(string executable, string sessionId, TimeSpan limit, CancellationToken ct)
    {
        // 사용자 전역 설정·플러그인·MCP 를 이 프로세스에 싣지 않도록 비어 있는 전용 설정 폴더(권한 0700)를 준다.
        var configDirectory = Path.Combine(Path.GetTempPath(), "oranda-opencode-export-" + Guid.NewGuid().ToString("N"));
        var environment = OpencodeDriver.LaunchEnvironment(null);
        environment["OPENCODE_CONFIG_DIR"] = configDirectory;
        var launch = new ClaudeLaunch(
            executable,
            ["session", "export", "--standalone", "--sanitize", sessionId],
            Path.GetTempPath(),
            string.Empty,
            environment);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        Process? process = null;
        try
        {
            PrivateDirectory.Ensure(configDirectory);
            process = Process.Start(SystemClaudeProcessLauncher.CreateStartInfo(launch));
            if (process is null)
            {
                return null;
            }

            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = new StringBuilder();
            var buffer = new char[81920];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
            {
                output.Append(buffer, 0, read);
                if (output.Length > MaxExportChars)
                {
                    return null;
                }
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return process.ExitCode == 0 ? output.ToString() : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // 시간 상한
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
                {
                    // 이미 끝났거나 끝나는 중이다.
                }

                process.Dispose();
            }

            try
            {
                Directory.Delete(configDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 임시 폴더 정리 실패는 무시한다.
            }
        }
    }

    private static bool HasStoredPermissions(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.Name.Contains("permission", StringComparison.OrdinalIgnoreCase) && !IsEmpty(property.Value)) || HasStoredPermissions(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(HasStoredPermissions);
            default:
                return false;
        }
    }

    private static bool HasInheritance(JsonElement info)
        => info.EnumerateObject().Any(p =>
            (p.Name.StartsWith("parent", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("fork", StringComparison.OrdinalIgnoreCase)) && !IsEmpty(p.Value));

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Object => !value.EnumerateObject().Any(),
        _ => false,
    };

    private static AiError NotVerified(string reason)
        => new($"이어 말할 opencode 대화를 확인하지 못해 실행하지 않았습니다. {reason}. 새 대화로 다시 시작하세요.", "resume_not_verified");
}
