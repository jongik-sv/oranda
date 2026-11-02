# AI 대화 이미지 입력과 LLM 모델 프로필 설계

작성일: 2026-11-02. 상태: 시안·결정 사항 사용자 확인(2026-11-02). 구현은 `/coordinator` 회차로 진행한다.

표기: **[사용자]** = 사용자가 결정함, **[설계]** = 작성자가 정함(사용자가 뒤집을 수 있음), **[구현]** = 구현자에게 맡김.

## 1. 의도

사용자는 두 가지를 하려고 한다.

1. 실행 계획 화면을 캡처해서 AI 대화에 그대로 붙여 넣는다.
2. Claude Code 대신 사내 LAN 서버의 Strata 가 올린 LLM 모델을 쓴다.

참고 화면 3장(요약 모델 카드, 프로필 드롭다운, LLM 프로필 관리 창)과 같은 방식으로 모델을 설정한다.

## 2. 목표

- AI 대화 입력에서 이미지를 붙여 넣기·끌어 놓기·파일 선택으로 첨부해 보낸다.
- 설정 「AI 모델」 을 「선택 안 함 / 직접 선택」 + 프로필 드롭다운 + 프로필 관리 창으로 바꾼다.
- 프로필 종류 7개를 모두 실제로 동작하게 한다 **[사용자]**: Anthropic, Z.AI, Google Gemini, OpenAI, Ollama, LM Studio, 직접 입력.
- 어느 프로필을 쓰든 도구 호출은 `AgentGate` 를 지난다. 허용 수준·범위 검사·리터럴 가림·감사 기록이 그대로 적용되어야 한다.
- 기존 설정 파일(`Driver=ClaudeCode`, `Model=opus`)은 그대로 Claude Code 로 동작한다.

## 3. 비목표

- Antigravity CLI, Codex CLI 실행. 드롭다운에 「준비 중」 비활성 줄로만 보인다 **[사용자]**.
- 튜닝과 무관한 독립 대화 시작. 대화 입력은 지금처럼 튜닝으로 시작한 대화에 이어 말할 때만 쓴다 **[설계]**.
- 튜닝 시작 확인 창에서 이미지 첨부 **[설계]**.
- 대화·이미지 저장. 대화 창은 지금처럼 닫으면 사라진다.
- 이미지 안의 값을 찾아 가리는 처리(OCR).

## 4. 실패로 보는 경우

기능이 동작해도 다음 중 하나면 실패다.

- 어떤 경로든 `AgentGate` 를 건너뛰는 도구 호출이 생긴다.
- Claude Code 구독 사용자의 동작이 나빠진다(인증 깨짐, MCP 커넥터 유입, 도구 호출 회귀).
- API 키가 설정 파일·로그·감사 기록·명령줄 인자에 평문으로 남는다.
- 실제 서버 주소가 리포(코드·시험·문서·커밋·레인 지시문)에 들어간다.

## 5. 서버 시험에서 확인한 사실 (2026-11-02)

사내 Strata 서버(Python BaseHTTP, 슬롯 1개, API 키 없음, n_ctx 262144)와 Ollama 0.40.11 에 직접 요청해 확인했다.

| 항목 | 결과 |
|---|---|
| Strata `/v1/models` | `architecture.input_modalities` 에 `image` 표시 |
| Strata `/v1/chat/completions` | 글·스트리밍·이미지·`tool_calls`·`response_format: json_schema` 통과 |
| Strata `/v1/messages`(Anthropic 형식) | 글·스트리밍·이미지·`tool_use` 통과 |
| Ollama `/v1/messages` | 비스트리밍 글 통과. 그 밖은 미확인 |
| Ollama `/api/tags` | 모델별 `capabilities` 에 `vision`·`tools` 표시 |
| `claude -p` + `ANTHROPIC_BASE_URL` → Strata | MCP 도구 왕복, `--resume`, `--json-schema`, `--effort`, 표준 입력 이미지 통과 |
| 같은 실행에서 이미지 + 도구 + 다음 턴 `--resume` | 통과(이어 간 턴에서 앞 이미지 내용을 답함) |
| 구독 인증 + `--input-format stream-json` + 이미지 | 통과, stderr 없음 |

claude 2.1.316 기준이다. 서버 연결 시 stderr 에 `claude.ai connectors are disabled…` 와 `[claude-code:unrecognized_model]` 이 나오지만 stdout 은 깨끗하다. `ClaudeProcess` 는 stderr 를 따로 받으므로 영향이 없다.

기록 프록시로 추가 확인한 사실:

- 빈 `HOME`·`CLAUDE_CONFIG_DIR` 에서도 `ANTHROPIC_AUTH_TOKEN` 만으로 실행된다. 프로필 경로에는 claude 로그인이 필요 없고 실행 파일만 있으면 된다(macOS 키체인 항목까지 없는 PC 는 미확인).
- `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` 상태에서 실행 1회의 요청은 `/v1/messages` 1건이고 모델은 프로필 모델뿐이다. 배경 호출은 없었다.
- `ANTHROPIC_AUTH_TOKEN` 은 `Authorization` 헤더로, `ANTHROPIC_API_KEY` 는 `x-api-key` 헤더로 나간다.

미검증: Anthropic API·Z.AI·Gemini·OpenAI·LM Studio 실제 호출(키·서버 없음). 가짜 서버 시험으로 검증하고 그대로 활성화하며, 미검증 항목은 릴리스 노트에 적는다 **[사용자]**.

## 6. 구조

경로는 2개다 **[설계]**.

| 경로 | 드라이버 | 프로필 | 필요 조건 |
|---|---|---|---|
| CLI | `CliDriver` 재사용 | Claude Code, Anthropic, Z.AI, 직접 입력(Anthropic 형식) | PC 에 `claude` 실행 파일 |
| API | 새 `OpenAiApiDriver` | OpenAI, Gemini, Ollama, LM Studio, 직접 입력(OpenAI 형식) | 없음 |

`IAiDriver` 의 DI 등록은 새 `RoutingAiDriver` 로 바꾼다. 실행마다 설정을 읽어 활성 프로필에 맞는 경로로 넘긴다. `AiTuningFlow` 는 이 구조 때문에 바뀌지 않으며, 바뀌는 곳은 6.6 의 이미지 전달과 경계 확인뿐이다.

Anthropic 형식 프로필은 `claude` 실행 파일이 필요하다. 없으면 기존 `cli_not_found` 오류에 「OpenAI 형식을 쓰면 없이도 연결할 수 있다」 는 안내를 덧붙이고, 연결 테스트에서도 같은 내용을 보인다. Anthropic 형식 도구 반복은 만들지 않는다 **[설계]**.

CLI 경로를 남기는 이유: 이미 검증된 MCP 경로(claude → orandactl → 파이프 → `AgentGate`)와 `ValidateInit` 검사를 그대로 쓸 수 있고, Anthropic 형식 도구 반복을 따로 만들 필요가 없다.

사내 Strata 는 두 형식을 모두 지원하므로 「직접 입력」 으로 어느 쪽이든 고를 수 있다.

### 6.1 CLI 경로 변경

- **환경 변수.** `ClaudeLaunch` 에 환경 변수 목록을 추가한다. Claude Code 프로필은 아무것도 더하지 않는다(구독 인증 유지). 그 밖의 프로필은 다음을 넣는다.
  - `ANTHROPIC_BASE_URL` = 프로필 URL(끝의 `/v1`·`/` 제거)
  - 키 전달은 프리셋별로 다르다.
    - Anthropic 프리셋: `ANTHROPIC_API_KEY` = 키, `ANTHROPIC_AUTH_TOKEN` 제거(`x-api-key` 헤더).
    - Z.AI·직접 입력: `ANTHROPIC_AUTH_TOKEN` = 키, 키가 없으면 임의의 값 `local`, `ANTHROPIC_API_KEY` = 빈 값(`Authorization` 헤더).
  - `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` = `1`
- **배경 호출.** 확인 결과 없다(5절). 이 상태를 지키도록 가짜 HTTP 서버 시험으로 요청의 모델을 검사한다.
- **입력 형식.** 모든 실행을 `--input-format stream-json` 으로 바꾼다 **[설계]**. 표준 입력에 `{"type":"user","message":{"role":"user","content":[…]}}` 한 줄을 쓰고 닫는다. 글·이미지 경로를 하나로 만들기 위해서다.
- **모델.** `--model` 에 프로필의 모델 이름을 그대로 넘긴다. 요청의 `Model` 이 비어 있거나(`default`) 설정의 CLI 모델(`opus` 등)이 남아 있어도 프로필이면 프로필 모델을 쓴다: 프로필 서버로 `--model opus` 가 나가면 안 된다. 설정 화면의 CLI 모델 목록(opus·sonnet·haiku)은 Claude Code 프로필에서만 보인다.
- **비용.** Claude Code 프로필이 아니면 `AiUsage.CostUsd` 를 null 로 낸다(CLI 가 내는 값은 실제 비용이 아니다).

### 6.2 API 경로(`OpenAiApiDriver`)

- **프로토콜.** `POST {base}/chat/completions`, `stream: true`. 키가 있으면 `Authorization: Bearer`.
- **도구 반복.**
  1. `IAgentGate.ListTools(session)` 을 OpenAI `tools` 로 바꿔 보낸다.
  2. 스트림에서 글과 `tool_calls` 조각을 모은다.
  3. 도구 호출마다 `IAgentGate.CallToolAsync(session, …)` 를 부르고 결과를 `role: tool` 로 붙인다.
  4. 도구 호출이 없는 응답이 오거나 구조 결과를 받으면 끝낸다.
- **상한.** 한 번의 실행에서 모델 호출 40회, 도구 호출 60회. 넘으면 `AiError(turn_limit)` **[설계]**. 게이트에는 도구 예산이 없고 프롬프트의 도구 예산(`ToolBudget`)은 글일 뿐이므로 드라이버에 상한을 둔다. 게이트·CLI 동작은 바꾸지 않는다.
- **게이트.** 프로세스 안에서 직접 부르므로 실행 토큰과 파이프가 필요 없다. `session.IsRevoked` 는 매 호출 전에 확인한다. `IAiDriver.cs` 의 `Session` 주석을 이에 맞게 고친다.
- **assistant 메시지 보존.** 도구 반복에서 assistant 메시지를 되돌려 보낼 때 받은 JSON 을 그대로 쓴다. 모르는 칸(`reasoning_content`, Gemini 의 `extra_content` 서명)을 버리면 일부 제공자가 거부하기 때문이다.
- **구조 결과.** `OutputSchema` 가 있으면 합성 도구 `StructuredOutput` 을 함께 보낸다 **[설계]**. CLI 와 같은 방식이며 `response_format` 과 도구를 함께 쓸 때 생기는 제공자 차이를 피한다. 모델이 이 도구를 부르지 않고 끝내면 한 번 더 요구하고, 그래도 없으면 `AiError(structured_output_missing)`. 입력 인자를 `AiFinal.Result` 로 내고 `AiFinal.Text` 는 null 로 둔다(CLI 와 같다. 대화 창이 `Summary` 를 넣으므로 글을 함께 내면 두 번 보인다). 합성 도구는 도구 카드에 보이지 않는다.
- **감사 기록.** 파이프의 hello 가 없으므로 드라이버가 `session.Client` 를 직접 채운다. 비우면 감사 기록의 client 칸이 비게 된다.
- **이어 말하기.** 드라이버가 `ResumeId → 메시지 목록` 을 메모리에 둔다(대화 창도 저장하지 않으므로 충분하다). 앱을 다시 시작하면 사라지며, 모르는 id 는 `AiError(resume_unknown)` 이다. 대화당 메시지 수와 이미지를 포함한 총량에 상한을 둔다 **[구현]**.
- **동시 실행.** 슬롯 1개 서버에서는 요청이 서버 쪽에서 대기한다. 드라이버가 따로 줄을 세우지 않는다 **[설계]**.
- **시스템 프롬프트.** CLI 는 Claude Code 자체 시스템 프롬프트가 있지만 API 경로에는 없다. 도구만 쓰고 추측하지 말라는 짧은 시스템 프롬프트를 드라이버가 넣는다 **[구현: 문구]**.
- **사건 변환.** `AiStarted` → (`AiTextDelta`·`AiToolCall`·`AiToolResult`)… → `AiUsage(CostUsd: null)` → `AiFinal`. `AiToolResult` 의 판단은 게이트 응답의 `Judgement`·`Note` 를 그대로 쓴다.
- **오류.** HTTP 오류·연결 실패·시간 초과·잘못된 응답은 `AiError` 로 내고 키는 메시지에 넣지 않는다. 취소는 진행 중 요청을 끊고 `OperationCanceledException` 으로 끝낸다.
- **effort.** API 경로에서는 쓰지 않는다. 화면에서 effort 줄을 끈다 **[설계]**.

### 6.3 계약 변경 (Core)

```csharp
public sealed record AiImageAttachment(string MediaType, byte[] Data);

// AiRequest 끝에 추가
IReadOnlyList<AiImageAttachment>? Images = null

// AiDriverCapabilities 끝에 추가
bool Images = false
```

`AiDriverKind` 는 바꾸지 않는다. `AiSettings` 에 다음을 추가한다.

```csharp
public IReadOnlyList<AiLlmProfile> Profiles { get; init; } = [];
public Guid? ActiveProfileId { get; init; }

public sealed record AiLlmProfile
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public AiProfilePreset Preset { get; init; }   // Anthropic, ZAi, Gemini, OpenAi, Ollama, LmStudio, Custom
    public AiApiFormat Format { get; init; }       // Anthropic, OpenAi
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public int MaxInputTokens { get; init; } = 200000;
    public int MaxOutputTokens { get; init; } = 10000;
    public bool? SupportsImages { get; init; }     // null = 모름(보내 보고 서버 오류를 보여 준다)
}
```

- API 키는 `ICredentialStore` 에 `oranda:ai:{guid}` 키로 저장한다. 설정 파일에는 넣지 않는다 **[설계]**. 프로필을 지우면 키도 지운다.
- `Driver=None` 이면 AI 를 쓰지 않는다. `Driver=ClaudeCode` 이고 `ActiveProfileId` 가 null 이면 「시스템 CLI: Claude Code」, 값이 있으면 그 프로필을 쓴다. 기존 파일은 이관 없이 그대로 읽힌다.
- 새 enum 값을 넣지 않는 이유 **[설계]**: 옛 빌드가 모르는 enum 값을 읽으면 `JsonDocumentFile` 이 파일 전체를 손상으로 처리해 모든 설정을 초기화한다. 옛 빌드는 위 방식의 파일을 Claude Code 로 읽는다. 옛 빌드가 저장하면 프로필 칸이 사라지는 것은 감수한다.
- 프로필이 지워져 `ActiveProfileId` 를 찾지 못하면 `AiError(profile_missing)` 로 알린다.

### 6.4 프리셋 **[설계]**

| 프리셋 | 형식 | 기본 URL | 키 |
|---|---|---|---|
| Anthropic | Anthropic | `https://api.anthropic.com` | 필요 |
| Z.AI | Anthropic | `https://api.z.ai/api/anthropic` | 필요 |
| Google Gemini | OpenAI | `https://generativelanguage.googleapis.com/v1beta/openai` | 필요 |
| OpenAI | OpenAI | `https://api.openai.com/v1` | 필요 |
| Ollama | OpenAI | `http://localhost:11434/v1` | 불필요 |
| LM Studio | OpenAI | `http://localhost:1234/v1` | 불필요 |
| 직접 입력 | 선택(기본 Anthropic) | 없음 | 선택 |

참고 화면에 없는 「API 형식」 선택을 「직접 입력」 에만 추가한다. 호환 서버의 형식은 URL 로 판별할 수 없기 때문이다.

URL 입력은 끝의 `/`·`/v1` 유무와 무관하게 받아서 형식에 맞게 정규화한다.

### 6.5 설정 화면

참고 화면을 따르되 문구는 Oranda 에 맞춘다(「회의록 요약」 → 「AI 튜닝과 대화」). 승인된 시안: https://claude.ai/artifact/L7XCNAg8wJkbgjEX5Se1B4 (비공개 캔버스 8장, 2026-11-02 사용자 확인). 문서와 시안이 다르면 화면 배치는 시안을 따른다.

- **설정 「AI 모델」 쪽.** 「선택 안 함 / 직접 선택」 카드와 「프로필 관리」 단추.
- **프로필 드롭다운.**
  - 「시스템 CLI」 묶음: Claude Code, Antigravity CLI(준비 중), Codex CLI(준비 중).
  - 「내 프로필」 묶음: 저장한 프로필과 `+ 새 프로필 만들기…`(프로필 관리 창을 연다).
- **아래 칸.** Claude Code 이면 CLI 모델(opus·sonnet·haiku)과 effort. 프로필이면 모델 이름 읽기 전용 표시. 리터럴 가림 확인란은 항상 유지한다.
- **프로필 관리 창.** 프리셋 카드 7개, API Base URL, API Key(보기 전환), 모델명(「모델 새로고침」), 최대 입력·출력 토큰, 프로필 이름, 「취소 / 연결 테스트 / 프로필 저장」. 프로필 목록·수정·삭제도 이 창에서 한다.
- **프로필 저장 시점.** 프로필 관리 창의 저장은 설정 창의 확인과 별개로 즉시 기록하고, `AiModelSettingsPage` 가 들고 있는 값도 갱신한다. 설정 창 확인이 옛 사본으로 프로필을 덮어쓰지 않게 하기 위해서다 **[설계]**.
- **모델 새로고침.** 정규화한 URL 의 `models` 를 `GET` 한다(Anthropic 형식 `{base}/v1/models`, OpenAI 형식 `{base}/models`). Anthropic 형식은 `x-api-key`·`anthropic-version` 헤더를 붙인다. 응답에 `architecture.input_modalities` 가 있으면 `SupportsImages` 를 채운다. Ollama 는 `/api/tags` 의 `capabilities` 로 채운다 **[구현: 그 밖의 제공자 판별]**. 저장 전 사용자가 바꿀 수 있는 확인란으로 보인다.
- **연결 테스트.** 고른 모델로 최대 출력이 작은 요청을 한 번 보내 성공·실패·걸린 시간을 표시한다. 저장 전에도 실행할 수 있다.
- **평문 경고.** URL 이 `http://` 이고 localhost 가 아니면 「SQL 과 이미지가 암호화 없이 전달됩니다」 안내 한 줄을 보인다 **[설계]**.
- **요약 글.** `AiModelSettingsPage.Summary` 속성 글을 `AI: <프로필 이름> · <모델>` 로 바꾼다. 이 속성은 지금 어떤 화면에도 바인딩되어 있지 않으며, 편집기 위에 새로 보이게 하지는 않는다(시안에 없음) **[설계]**.
- **변경 알림.** 프로필 관리 창 저장은 `SettingsChanged("ai")` 를 직접 발행한다(`SettingsStore` 는 `ai` 변경을 알리지 않는다). 대화 레인은 보낼 때 프로필을 다시 확인한다.
- **튜닝 시작 창.** 모델 줄은 활성 프로필에 맞춘다. 프로필이면 그 모델 하나만 보이고 effort 줄은 API 경로에서 끈다. `AiContextKeys` 의 `configured` 판단은 `Driver != None` 그대로 유효하다.

토큰 값의 쓰임:

- 최대 출력: API 경로는 `max_tokens`, CLI 경로는 `CLAUDE_CODE_MAX_OUTPUT_TOKENS` 환경 변수로 넘긴다.
- 최대 입력: API 경로에서 보낸 입력 토큰(`usage.prompt_tokens`)이 이 값을 넘으면 다음 이어 말하기를 막고 새 대화를 안내한다. CLI 경로는 CLI 가 자체 관리하므로 쓰지 않는다 **[설계]**.

### 6.6 대화 이미지 입력

- **입구.** 대화 입력칸의 붙여 넣기(Ctrl/Cmd+V), 끌어 놓기, 📎 단추(파일 선택). 파일 선택은 OS 별 클립보드 형식이 다를 때의 대비책이기도 하다. macOS·Windows 모두 클립보드 이미지 읽기를 먼저 시제품으로 확인한다.
- **입력칸 위 표시.** 축소 이미지와 ✕ 단추. 글 없이 이미지만으로도 보낼 수 있다.
- **제한 [설계].**
  - 한 번에 5장.
  - 긴 변이 4000px 를 넘으면 줄인다. 세로로 긴 실행 계획 캡처의 글자를 읽을 수 있도록 넉넉히 둔다.
  - 인코딩은 PNG 를 유지하고, 5 MB 를 넘으면 JPEG 로 다시 인코딩한다. 그래도 넘으면 첨부를 거절하고 이유를 보인다.
  - 형식은 PNG·JPEG·WebP·GIF.
- **보낸 줄.** `ChatUserEntry` 에 축소 이미지를 함께 보인다.
- **지원하지 않는 모델.** 활성 프로필의 `SupportsImages == false` 이면 입구를 끄고 이유를 툴팁으로 보인다. 판단은 `SettingsService.Current.Ai` 로 한다: `IAiDriver.Capabilities` 는 동기라 프로필별로 답할 수 없어 라우터는 고정 `Images=true` 로 둔다. 첨부한 뒤 지원하지 않는 프로필로 바꾸면 보내기를 막는다.
- **이어 말하기 경계.** 프로필을 바꾸면 앞 대화의 `ResumeId` 를 쓸 수 없다. `AiTuningFlow` 가 대화 시작 때 프로필 id·URL·형식·모델을 기억하고, 하나라도 달라지면 「모델이 바뀌어 이어 말할 수 없습니다. 「AI 튜닝」 으로 다시 시작하세요.」 로 안내한다. 같은 프로필의 URL 을 고친 경우도 포함한다: 앞서 보낸 이미지가 경고 없이 새로운 곳으로 가지 않게 하기 위해서다 **[설계]**.

**운영 접속 경고 [사용자].** 이미지는 가림을 적용할 수 없으므로 새 이미지를 보낼 때마다 확인 창을 띄운다. 「이번만 묻지 않기」 는 없다. 이미 보낸 이미지가 이어 말하기로 같은 곳에 다시 전송되는 경우와 글만 보내는 이어 말하기에는 경고하지 않는다.

- 조건: 그 대화가 리터럴을 가리는 중일 때(`_vault != null` 또는 `maskRequired(context)`). 운영 접속은 항상 해당하며, 개발 접속이라도 가림을 켠 사용자는 같은 보호를 받는다 **[설계]**.
- 문구에 받는 곳(프로필 이름·URL)을 표시한다. 취소하면 아무것도 보내지 않고 입력 내용은 그대로 둔다.
- 새 이미지를 보냈다는 사실(장수·크기·받는 곳)을 기존 `AgentAuditEntry` 에 합성 도구 이름으로 남긴다(저장소 변경 없음). 이미지 내용은 남기지 않는다 **[설계]**.

알려진 한계: CLI 경로에서는 claude 자체 세션 파일(`~/.claude/projects/…`)에 이미지가 base64 로 남는다. 지금도 글은 그렇게 남으며, `--resume` 에 필요하므로 끄지 않는다.

## 7. 제약

- 서버 IP·회사 호스트를 코드·시험·문서·커밋·레인 지시문에 넣지 않는다. 시험은 `http://llm.example:8080` 같은 자리표시자를 쓰고, 실서버 시험은 환경 변수(`ORANDA_AI_E2E_BASE_URL`, `ORANDA_AI_E2E_MODEL`)가 있을 때만 돈다. 구독 Claude 시험은 기존 `ORANDA_AI_E2E=1` 게이트를 그대로 쓴다.
- 새 NuGet 의존성 없이 `HttpClient`·`System.Text.Json` 으로 구현한다 **[설계]**.
- `AiToolJudgement` 값은 맨 끝에만 추가한다(기존 주석 규칙). `AiDriverKind` 에는 값을 추가하지 않는다(6.3).
- 설정 파일 호환: 새 칸이 없는 파일을 새 앱이 읽어야 하고, 새 앱이 쓴 파일을 옛 앱이 초기화 없이 읽어야 한다. `SchemaVersions.Settings` 는 올리지 않는다.

## 8. 검증

- **단위.** 설정 왕복·기존 파일 호환, URL 정규화, stream-json 입력 생성, 환경 변수 조합(Claude Code 는 아무것도 추가하지 않음), `RoutingAiDriver` 선택, 모델 목록 파싱.
- **API 드라이버.** 가짜 `HttpMessageHandler` 로 스트리밍 조각 조립, 도구 반복, 게이트 거부 전달, 구조 결과, 재요구, 턴 상한, 취소, HTTP 오류, 알 수 없는 칸 보존, 키 비노출.
- **CLI.** 기존 `CliDriverTests`·`ClaudeCliArgumentsTests`·`ClaudeEndToEndTests` 통과. 가짜 HTTP 서버로 배경 호출 확인.
- **실서버 시험.** 환경 변수가 있을 때: 두 경로 각각 이미지 + 도구 + 이어 말하기 + 구조 결과.
- **회귀.** 구독 인증 Claude Code 로 튜닝 한 번.
- **화면.** macOS 실제 앱에서 실행 계획을 캡처해 붙여 넣고 LLM 프로필로 답을 받는다. 운영 접속 경고를 확인한다. Windows 클립보드 붙여 넣기도 확인한다.

## 9. 문서

`docs/decisions.md` 에 결정 항목 추가, PRD·TRD 의 AI 절 갱신, `docs/release-notes` 의 다음 버전(미검증 프로필 명시), `docs/user` 안내(서버 주소 없이). 진행 관리는 조정자 회차가 맡는다.

## 10. 구현자에게 맡기는 사항

클립보드 읽기 구현, 축소 이미지 표시 방식, 드롭다운 묶음 머리 줄 구현, 시스템 프롬프트 문구, 스트림 파서 구조, 시간 제한 값, 대화 메시지 상한 값, 레인 분해.

레인 분해 시 계약(6.3)을 먼저 머지해야 한다. 드라이버·설정 화면·대화 화면이 모두 이 계약에 의존한다.
