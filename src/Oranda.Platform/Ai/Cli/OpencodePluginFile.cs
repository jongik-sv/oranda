using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using Oranda.Platform.Ai.Bridge;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 실행 전용 설정 폴더(<c>OPENCODE_CONFIG_DIR</c>)의 <c>plugins/oranda.js</c> 를 쓴다. opencode 는 MCP 도구를 코드 모드 <c>execute</c> 로만 노출하고
/// 그 안의 <c>fetch</c> 가 외부 HTTP 에 열려 있어 잠글 수 없으므로, 플러그인이 <c>ctx.tool.transform</c> 으로 도구 레지스트리를 직접 편집한다:
/// 모든 기본 도구를 지우고 앱 도구(게이트가 이 실행에 내주는 목록)와 구조 결과 도구만 직접 도구(<c>codemode:false</c>)로 등록한다.
/// 도구 호출은 플러그인이 브리지(<c>AgentBridgeHost</c>)에 직접 붙어 <c>oranda.hello</c>(실행 토큰) → <c>oranda.callTool</c> 로 보낸다:
/// 그러므로 orandactl 과 같은 AgentGate·감사·취소 경로를 지난다. 연결은 첫 호출 때 맺는다.
/// <para>
/// 이 파일에는 토큰을 쓰지 않는다. 토큰은 프로세스 환경 변수(<c>ORANDA_RUN_TOKEN</c>)로만 받아 플러그인이 읽는다.
/// 파일은 실행 폴더(권한 0700) 안에 만들고 실행이 끝나면 지운다.
/// </para>
/// <para>
/// 로드 확인: 플러그인은 등록을 마치면 설정 폴더에 <see cref="ReadyFileName"/> 을 남기고(등록한 도구 이름 목록), 실패하면 <see cref="FailedFileName"/> 을 남긴다.
/// 드라이버는 첫 스트림 사건에서 이 표식을 기대 목록과 대조해 어긋나면 닫는다.
/// </para>
/// </summary>
internal static class OpencodePluginFile
{
    public const string ReadyFileName = "oranda-ready.json";

    public const string FailedFileName = "oranda-failed.txt";

    /// <summary>설정 폴더 안 플러그인 파일의 상대 경로.</summary>
    public static readonly string RelativePath = Path.Combine("plugins", "oranda.js");

    /// <summary>플러그인 파일을 쓴다. 설정 폴더가 없으면 만든다(권한 0700). 파일은 실행 사용자만 읽고 쓴다.</summary>
    public static string Write(string configDirectory, string pipeName, IReadOnlyList<AgentToolInfo> tools, JsonElement? finishSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        PrivateDirectory.Ensure(configDirectory);
        var pluginsDirectory = Path.Combine(configDirectory, "plugins");
        PrivateDirectory.Ensure(pluginsDirectory);

        var path = Path.Combine(configDirectory, RelativePath);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(Render(configDirectory, pipeName, tools, finishSchema));
        return path;
    }

    /// <summary>플러그인 소스. 실행마다 달라지는 값(파이프 경로·도구 목록·표식 경로)은 JSON 상수 하나로 넣는다(JSON 이스케이프로 따옴표·역슬래시·비 ASCII 가 깨지지 않는다).</summary>
    internal static string Render(string configDirectory, string pipeName, IReadOnlyList<AgentToolInfo> tools, JsonElement? finishSchema)
    {
        var run = new JsonObject
        {
            ["pipe"] = OperatingSystem.IsWindows() ? @"\\.\pipe\" + pipeName : pipeName,
            ["readyPath"] = Path.Combine(configDirectory, ReadyFileName),
            ["failedPath"] = Path.Combine(configDirectory, FailedFileName),
            ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = JsonNode.Parse(t.InputSchema.ToJsonString()),
            })]),
        };

        if (finishSchema is { } schema)
        {
            run["finish"] = new JsonObject
            {
                ["name"] = OpencodeArguments.FinishToolName,
                ["description"] = "Call this tool exactly once, as your last action, with the final structured result. The arguments are the result; do not call it before you have finished using the other tools.",
                ["schema"] = JsonNode.Parse(schema.GetRawText()),
            };
        }

        // JSON 을 객체 리터럴로 직접 넣으면 "__proto__" 키가 자기 속성이 아닌 프로토타입을 바꾼다: 문자열 리터럴로 넣고 JSON.parse 로 읽는다.
        return PluginSource.Replace("__RUN__", "JSON.parse(" + JsonSerializer.Serialize(run.ToJsonString()) + ")", StringComparison.Ordinal);
    }

    private const string PluginSource = """
        // Oranda 가 실행마다 만드는 파일입니다. 실행 토큰은 들어 있지 않습니다(환경 변수 ORANDA_RUN_TOKEN 만 읽습니다).
        const RUN = __RUN__;
        const fs = await import('node:fs');
        const net = await import('node:net');

        const token = () => process.env.ORANDA_RUN_TOKEN || '';
        const scrub = (text) => { const t = token(); const s = String(text ?? ''); return t ? s.split(t).join('***') : s; };

        let socket = null;
        let buffer = '';
        let waiter = null;
        let nextId = 0;
        let chain = Promise.resolve();

        function onData(chunk) {
          buffer += chunk;
          let index;
          while ((index = buffer.indexOf('\n')) >= 0) {
            const line = buffer.slice(0, index).trim();
            buffer = buffer.slice(index + 1);
            if (!line || !waiter) continue;
            let message;
            try { message = JSON.parse(line); } catch { continue; }
            if (message.id !== waiter.id) continue;
            const { resolve, reject } = waiter;
            waiter = null;
            if (message.error) reject(new Error(`${message.error.code}: ${scrub(message.error.message)}`));
            else resolve(message.result ?? {});
          }
        }

        function onClose() {
          socket = null;
          if (waiter) { const { reject } = waiter; waiter = null; reject(new Error('Oranda 연결이 닫혔습니다.')); }
        }

        function exchange(method, params) {
          return new Promise((resolve, reject) => {
            const id = ++nextId;
            waiter = { id, resolve, reject };
            socket.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
          });
        }

        async function ensure() {
          if (socket && !socket.destroyed) return;
          const connected = await new Promise((resolve, reject) => {
            const s = net.connect(RUN.pipe);
            const timer = setTimeout(() => { s.destroy(); reject(new Error('Oranda 앱에 연결하지 못했습니다(시간 초과).')); }, 2000);
            s.once('connect', () => { clearTimeout(timer); resolve(s); });
            s.once('error', (e) => { clearTimeout(timer); reject(new Error('Oranda 앱에 연결하지 못했습니다: ' + scrub(e.message))); });
          });
          connected.setEncoding('utf8');
          connected.on('data', onData);
          connected.on('close', onClose);
          connected.on('error', () => {});
          socket = connected;
          buffer = '';
          try {
            await exchange('oranda.hello', { protocol: 1, runToken: token(), client: { name: 'opencode', version: '0' } });
          } catch (e) {
            // hello 가 실패한 연결을 남기면 다음 호출이 hello 없이 통과한다: 닫고 처음부터 다시 맺게 한다.
            socket = null;
            connected.destroy();
            throw e;
          }
        }

        // 호출은 한 번에 하나씩 보낸다(브리지 클라이언트와 같다). 연결이 끊기면 다음 호출이 hello 부터 다시 맺는다.
        function call(name, args, signal) {
          const run = chain.then(async () => {
            await ensure();
            const onAbort = () => { if (socket) socket.destroy(); };
            if (signal) { if (signal.aborted) onAbort(); else signal.addEventListener('abort', onAbort, { once: true }); }
            try {
              const result = await exchange('oranda.callTool', { name, arguments: args ?? {} });
              if (result.isError) throw new Error(scrub(result.text));
              return { content: [{ type: 'text', text: scrub(result.text) }] };
            } finally {
              if (signal) signal.removeEventListener('abort', onAbort);
            }
          });
          chain = run.catch(() => {});
          return run;
        }

        function fail(message) {
          try { fs.writeFileSync(RUN.failedPath, scrub(message), { mode: 0o600 }); } catch {}
        }

        const tools = RUN.tools.map((tool) => ({
          name: tool.name,
          id: tool.name,
          description: tool.description,
          options: { codemode: false },
          input: tool.inputSchema,
          execute: async (input, context) => call(tool.name, input, context && context.signal),
        }));
        if (RUN.finish) {
          tools.push({
            name: RUN.finish.name,
            id: RUN.finish.name,
            description: RUN.finish.description,
            options: { codemode: false },
            input: RUN.finish.schema,
            execute: async () => ({ content: [{ type: 'text', text: 'recorded' }] }),
          });
        }
        const names = tools.map((tool) => tool.name);

        export default {
          id: 'oranda',
          setup: async (ctx) => {
            try {
              // 변환은 레지스트리가 적용될 때마다 돈다(여러 번). 표식은 변환이 실제로 돈 뒤에 남긴다(플러그인이 불렸고 레지스트리 적용 단계까지 왔다는 뜻).
              // registry.list() 는 편집 전 상태를 돌려주므로 편집 결과는 여기서 확인할 수 없다: 모델에게 가는 도구 목록은 permissions 잠금이 한 번 더 막는다.
              ctx.tool.transform((registry) => {
                for (const entry of registry.list()) if (!names.includes(entry.name)) registry.remove(entry.name);
                for (const tool of tools) if (!registry.get(tool.name)) registry.add(tool);
                fs.writeFileSync(RUN.readyPath, JSON.stringify({ tools: names }), { mode: 0o600 });
              });
            } catch (error) {
              fail('플러그인 설정 실패: ' + (error && error.message));
            }
            return async () => { if (socket) socket.destroy(); };
          },
        };
        """;
}
