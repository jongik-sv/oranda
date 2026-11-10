using System.Text.Json.Serialization;
using Oranda.Core.Execution;

namespace Oranda.Core.Settings;

public enum Theme
{
    System,
    Light,
    Dark,
}

/// <summary><c>settings.json</c> 루트(data-model 2.1). 섹션 이름(camelCase)은 <c>SettingsChanged.Section</c> 값과 같다.</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = SchemaVersions.Settings;

    public UiSettings Ui { get; init; } = new();

    public EditorSettings Editor { get; init; } = new();

    public string KeymapId { get; init; } = "default";

    public string FormatterProfileId { get; init; } = "oranda-default";

    public ExecutionSettings Execution { get; init; } = new();

    public GridSettings Grid { get; init; } = new();

    public HistorySettings History { get; init; } = new();

    public MetadataCacheSettings MetadataCache { get; init; } = new();

    public MonitorSettings Monitor { get; init; } = new();

    public WorkspaceSettings Workspace { get; init; } = new();

    public SchemaBrowserSettings SchemaBrowser { get; init; } = new();

    public UpdateSettings Update { get; init; } = new();

    /// <summary>테이블 레이아웃 팝업(객체 정보 보기)의 붙여 넣기 옵션(TSK-13-32).</summary>
    public TableLayoutSettings TableLayout { get; init; } = new();

    /// <summary>MyBatis 작업 공간(docs/oranda1, TSK-03-03).</summary>
    public MyBatisSettings MyBatis { get; init; } = new();

    /// <summary>Role·기능 구성(PRD 12, TSK-07-01). 키가 없는 옛 파일 = 모든 기능 켬. 새 섹션이라 스키마 버전은 그대로.</summary>
    public FeaturesSettings Features { get => _features; init => _features = value ?? new(); }

    // "features": null 인 손으로 고친 파일도 기본값으로 받는다.
    private readonly FeaturesSettings _features = new();

    /// <summary>AI 튜닝 설정(설계 §2.3). 새 섹션이라 스키마 버전은 그대로, 키가 없는 옛 파일 = 드라이버 없음.</summary>
    public AiSettings Ai { get => _ai; init => _ai = value ?? new(); }

    // "ai": null 인 손으로 고친 파일도 기본값으로 받는다.
    private readonly AiSettings _ai = new();

    /// <summary>DBMS 드라이버 위치(Tibero 드라이버 지정, D-046 결정25). 새 섹션이라 스키마 버전은 그대로, 키가 없는 옛 파일 = 지정 안 함.</summary>
    public DriverSettings Drivers { get => _drivers; init => _drivers = value ?? new(); }

    // "drivers": null 인 손으로 고친 파일도 기본값으로 받는다.
    private readonly DriverSettings _drivers = new();
}

/// <summary>
/// DBMS 드라이버 위치. 드라이버는 라이선스 때문에 앱에 들어 있지 않아 사용자가 위치를 지정한다(docs/design/tibero.md 1장).
/// JSON: <c>"drivers": { "tiberoPath": "…", "sqlLoaderPath": "…", "tbLoaderPath": "…" }</c>. 환경 변수 <c>ORANDA_TIBERO_DRIVER</c> 가 이 값보다 먼저다(로더 몫).
/// </summary>
public sealed record DriverSettings
{
    /// <summary>Tibero.DataAccess.dll 의 위치: dll 파일, 그 dll 이 든 폴더, 또는 <c>.nupkg</c>. null·빈 값 = 지정 안 함.</summary>
    public string? TiberoPath { get; init; }

    /// <summary>
    /// 외부 적재 도구 SQL*Loader(<c>sqlldr</c>, Oracle 대상, XL1·XL3)의 위치: 실행 파일 또는 그것이 든 폴더. null·빈 값 = 찾기(PATH·ORACLE_HOME 등).
    /// 새 키라 스키마 버전은 그대로, 키가 없는 옛 파일 = 찾기.
    /// </summary>
    public string? SqlLoaderPath { get; init; }

    /// <summary>
    /// 외부 적재 도구 tbLoader(<c>tbloader</c>, Tibero 대상, XL1·XL3)의 위치: 실행 파일 또는 그것이 든 폴더. null·빈 값 = 찾기(PATH·TB_HOME/client/bin 등).
    /// 새 키라 스키마 버전은 그대로, 키가 없는 옛 파일 = 찾기.
    /// </summary>
    public string? TbLoaderPath { get; init; }

    /// <summary>외부 적재 도구 경로(앞뒤 공백을 걷어 내고 빈 값은 null = 찾기). 메서드라 설정 파일에는 쓰이지 않는다.</summary>
    public Oranda.Core.Transfer.Copy.ExternalLoad.ExternalToolPaths ToExternalToolPaths() =>
        new(Normalize(SqlLoaderPath), Normalize(TbLoaderPath));

    private static string? Normalize(string? path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}

/// <summary>AI 호출 방식(설계 §2). 새 값은 맨 끝에 추가한다.</summary>
public enum AiDriverKind
{
    /// <summary>선택 안 함. AI 단추는 설정을 안내한다.</summary>
    None,

    /// <summary>
    /// 시스템 CLI 의 Claude Code(<c>claude -p</c>). <see cref="AiSettings.ActiveProfileId"/> 가 있으면 그 프로필로 나간다.
    /// 프로필 종류는 이 enum 에 값을 더하지 않고 <see cref="AiSettings.Profiles"/> 로 표현한다:
    /// 옛 빌드가 모르는 enum 값을 읽으면 <c>JsonDocumentFile</c> 이 파일 전체를 손상으로 보고 모든 설정을 초기화하기 때문이다.
    /// </summary>
    ClaudeCode,
}

/// <summary>LLM 프로필의 제공자 종류(프리셋). 설정 파일에는 문자열로 저장된다. 모르는 값은 <see cref="Unknown"/> 으로 읽는다.</summary>
public enum AiProfilePreset
{
    /// <summary>이 빌드가 모르는 프리셋(다음 버전이 더한 값 등). 프로필 목록에는 남지만 실행할 수 없고(<c>profile_invalid</c>) 화면의 프리셋 목록에는 나오지 않는다.</summary>
    Unknown = -1,

    Anthropic,
    ZAi,
    Gemini,
    OpenAi,
    Ollama,
    LmStudio,

    /// <summary>직접 입력. <see cref="AiLlmProfile.Format"/> 을 사용자가 고른다.</summary>
    Custom,
}

/// <summary>프로필 서버의 API 형식. 형식이 실행 경로를 정한다(Anthropic = <c>claude</c> CLI, OpenAI = 앱 안 API 드라이버).</summary>
public enum AiApiFormat
{
    /// <summary>이 빌드가 모르는 형식. 실행할 수 없다(<c>profile_invalid</c>).</summary>
    Unknown = -1,

    Anthropic,
    OpenAi,
}

/// <summary>AI 가 쓰는 SQL 의 조인 문법. 파일에 키가 없으면 기본값 0 이 되므로 <see cref="Oracle"/> 을 0 으로 둔다.</summary>
public enum AiJoinSyntax
{
    /// <summary>쉼표로 나열한 FROM, WHERE 의 조인 조건, 외부 조인의 <c>(+)</c>.</summary>
    Oracle,

    /// <summary><c>JOIN … ON</c>, <c>LEFT OUTER JOIN</c> 등.</summary>
    Ansi,
}

/// <summary>
/// 사용자가 만든 LLM 접속 프로필. API 키는 이 레코드에 없고 <c>ICredentialStore</c> 의 <c>oranda:ai:{guid}</c> 키에 저장한다.
/// </summary>
public sealed record AiLlmProfile
{
    public const int DefaultMaxInputTokens = 200000;

    public const int DefaultMaxOutputTokens = 10000;

    // 손으로 고친 파일의 null·빠진 칸·0 이하 값이 그대로 들어오지 않게 칸마다 기본값으로 보정한다.
    // 배열 원소는 파일 병합(JsonDocumentFile.OverDefaults)이 기본값을 깔아 주지 않으므로 속성 쪽에서 막는다.
    private readonly string _name = "";
    private readonly string _baseUrl = "";
    private readonly string _model = "";
    private readonly int _maxInputTokens = DefaultMaxInputTokens;
    private readonly int _maxOutputTokens = DefaultMaxOutputTokens;

    public Guid Id { get; init; }

    public string Name { get => _name; init => _name = value ?? ""; }

    /// <summary>모르는 값은 <see cref="AiProfilePreset.Unknown"/> 으로 읽는다.</summary>
    [JsonConverter(typeof(TolerantEnumConverter<AiProfilePreset>))]
    public AiProfilePreset Preset { get; init; }

    /// <summary>모르는 값은 <see cref="AiApiFormat.Unknown"/> 으로 읽는다.</summary>
    [JsonConverter(typeof(TolerantEnumConverter<AiApiFormat>))]
    public AiApiFormat Format { get; init; }

    /// <summary>사용자가 입력한 그대로의 기준 URL. 요청마다 쓰는 형태는 <c>AiProfileUrl</c> 이 형식에 맞게 만든다.</summary>
    public string BaseUrl { get => _baseUrl; init => _baseUrl = value ?? ""; }

    public string Model { get => _model; init => _model = value ?? ""; }

    /// <summary>API 경로에서 보낸 입력 토큰이 이 값을 넘으면 이어 말하기를 막는다. CLI 경로는 쓰지 않는다. 0 이하는 기본값.</summary>
    public int MaxInputTokens { get => _maxInputTokens; init => _maxInputTokens = value > 0 ? value : DefaultMaxInputTokens; }

    /// <summary>API 경로는 <c>max_tokens</c>, CLI 경로는 <c>CLAUDE_CODE_MAX_OUTPUT_TOKENS</c> 로 넘긴다. 0 이하는 기본값.</summary>
    public int MaxOutputTokens { get => _maxOutputTokens; init => _maxOutputTokens = value > 0 ? value : DefaultMaxOutputTokens; }

    /// <summary>이미지 입력 지원. null = 모름(보내 보고 서버 오류를 보여 준다).</summary>
    public bool? SupportsImages { get; init; }

    /// <summary>
    /// 사용자가 「로컬 AI」 로 지정할 때의 서버 주소(D-045). 지금 <see cref="BaseUrl"/> 과 같을 때만 효력이 있어, 주소를 고치면 지정이 풀린다.
    /// 주소나 사설 대역으로 자동 판단하지 않는다. null = 지정하지 않음(기본).
    /// </summary>
    public string? LocalAiConfirmedUrl { get; init; }

    /// <summary>이 프로필이 지금 주소로 「로컬 AI」 로 지정되어 있다: 값을 가리지 않고 보낸다(<c>AiAccessPolicy.MaskLiterals</c>).</summary>
    [JsonIgnore]
    public bool IsLocalAi =>
        !string.IsNullOrWhiteSpace(LocalAiConfirmedUrl) && string.Equals(LocalAiConfirmedUrl.Trim(), BaseUrl.Trim(), StringComparison.Ordinal);
}

/// <summary>
/// 사용자가 이름과 실행 명령을 등록한 「기타 CLI」 한 줄. DB 도구와 실행 토큰 없이 글만 주고받는다.
/// 명령 문자열은 설정 파일에 평문으로 남으므로 비밀 값을 넣지 않도록 화면이 안내한다. 칸이 비었거나 읽을 수 없으면 빈 문자열이고, 빈 명령은 실행하지 않는다.
/// </summary>
public sealed record AiCustomCli
{
    private readonly string _id = "";
    private readonly string _name = "";
    private readonly string _command = "";

    /// <summary>안정 id. 이름·명령을 고쳐도 바뀌지 않는다. 선택은 <c>custom:</c> 접두어를 붙여 <see cref="AiSettings.SystemCliId"/> 에 저장한다.</summary>
    public string Id { get => _id; init => _id = value ?? ""; }

    /// <summary>화면에 보일 이름.</summary>
    public string Name { get => _name; init => _name = value ?? ""; }

    /// <summary>실행 명령 문자열(셸 없이 인자로 나눠 실행한다).</summary>
    public string Command { get => _command; init => _command = value ?? ""; }
}

/// <summary>설정 <c>ai</c> 섹션.</summary>
public sealed record AiSettings
{
    public AiDriverKind Driver { get; init; } = AiDriverKind.ClaudeCode;

    /// <summary>모델 이름(<c>claude --model</c> 값). null = CLI 기본.</summary>
    public string? Model { get; init; }

    /// <summary>추론 노력(<c>claude --effort</c> 값, <see cref="EffortLevels"/> 중 하나). null = CLI 기본.</summary>
    public string? Effort { get; init; }

    /// <summary>CLI 실행 파일 경로. null = PATH 에서 찾는다.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// SQL 글의 리터럴 값을 <c>:v1</c> 로 바꿔 보낸다(연구서 §7.1). 운영 접속은 이 값과 관계없이 가리고, 「로컬 AI」 프로필로 보낼 때는 이 값과 운영 여부에 관계없이 가리지 않는다(<c>AiAccessPolicy.MaskLiterals</c>, D-045).
    /// </summary>
    public bool MaskLiterals { get; init; }

    /// <summary>운영 벤치마크 제한 시간(초)의 기본값.</summary>
    public const int DefaultBenchmarkTimeoutSeconds = 30;

    /// <summary>운영 벤치마크 제한 시간(초)의 허용 범위 아래쪽 끝(0 은 제한 없음이라 따로 허용한다).</summary>
    public const int MinBenchmarkTimeoutSeconds = 5;

    /// <summary>운영 벤치마크 제한 시간(초)의 허용 범위 위쪽 끝.</summary>
    public const int MaxBenchmarkTimeoutSeconds = 3600;

    /// <summary>제한 시간(초)으로 쓸 수 있는 값인가: 0(제한 없음) 또는 5~3600.</summary>
    public static bool IsValidBenchmarkTimeout(int seconds) =>
        seconds == 0 || seconds is >= MinBenchmarkTimeoutSeconds and <= MaxBenchmarkTimeoutSeconds;

    /// <summary>범위 밖 값을 기본값(30초)으로 바꾼다. 제한 없음(0)은 명시한 값일 때만 남는다.</summary>
    public static int NormalizeBenchmarkTimeout(int seconds) => IsValidBenchmarkTimeout(seconds) ? seconds : DefaultBenchmarkTimeoutSeconds;

    /// <summary>
    /// 운영 접속의 벤치마크가 문장 하나(반복 전체)에 허용하는 시간(초). 튜닝 시작 창이 이번 실행에만 바꿀 수 있고, 그 값은 설정에 저장하지 않는다.
    /// 0 은 제한 없음(사용자가 중지할 때까지). 범위(0 또는 5~3600) 밖이거나 숫자가 아닌 값, 파일에 키가 없는 경우는 기본값 30 이다.
    /// 파일에서는 <see cref="BenchmarkTimeoutStored"/> 로 읽고 쓴다.
    /// </summary>
    [JsonIgnore]
    public int BenchmarkTimeoutSeconds
    {
        get => _benchmarkTimeoutSeconds ?? DefaultBenchmarkTimeoutSeconds;
        init => _benchmarkTimeoutSeconds = NormalizeBenchmarkTimeout(value);
    }

    /// <summary>
    /// 파일 칸(<c>benchmarkTimeoutSeconds</c>). 원본 생성 역직렬화는 파일에 없는 init 칸에도 <c>default</c> 를 넣으므로, <c>int</c> 로 받으면 키가 없는 옛 파일이
    /// 0(제한 없음)으로 읽힌다. 그래서 null 허용으로 받아 없음(null)을 30 으로 닫는다. 쓸 때는 항상 실제 값을 쓴다.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("benchmarkTimeoutSeconds")]
    [JsonConverter(typeof(TolerantBenchmarkTimeoutConverter))]
    internal int? BenchmarkTimeoutStored
    {
        get => BenchmarkTimeoutSeconds;
        init => _benchmarkTimeoutSeconds = value is { } seconds ? NormalizeBenchmarkTimeout(seconds) : null;
    }

    private readonly int? _benchmarkTimeoutSeconds;

    /// <summary>AI 튜닝의 재작성 제안과 오류 고치기가 쓸 조인 문법. 프롬프트 지시일 뿐이며 결과를 검사하거나 바꾸지 않는다. 키가 없거나 읽을 수 없는 값은 Oracle 이다.</summary>
    [JsonConverter(typeof(TolerantJoinSyntaxConverter))]
    public AiJoinSyntax JoinSyntax { get; init; }

    /// <summary>저장된 LLM 프로필 목록. 키가 없거나 null 인 파일은 빈 목록이다.</summary>
    public IReadOnlyList<AiLlmProfile> Profiles { get => _profiles; init => _profiles = value is null ? [] : [.. value.Where(p => p is not null)]; }

    // "profiles": null 이나 null 원소가 든 손으로 고친 파일도 받는다(빈 목록, null 원소 제거).
    private readonly IReadOnlyList<AiLlmProfile> _profiles = [];

    /// <summary>
    /// 쓰는 프로필 id. <see cref="Driver"/> 가 <see cref="AiDriverKind.ClaudeCode"/> 일 때 null 이면 시스템 CLI 의 Claude Code,
    /// 값이 있으면 그 프로필이다. 목록에 없으면 <c>profile_missing</c> 오류가 된다. 있는데 읽을 수 없는 값은 <see cref="Guid.Empty"/> 로 읽혀 같은 오류가 된다.
    /// </summary>
    [JsonConverter(typeof(TolerantActiveProfileIdConverter))]
    public Guid? ActiveProfileId { get; init; }

    /// <summary>
    /// 쓰는 시스템 CLI id(<c>codex-cli</c> 등). null 이면 Claude Code 다. <see cref="ActiveProfileId"/> 가 있으면 프로필이 우선이라 이 값은 보지 않는다.
    /// 읽을 수 없는 값(숫자·객체)은 <see cref="UnreadableSystemCliId"/> 로 읽혀 목록에 없는 id 가 되고, 라우터가 <c>system_cli_unavailable</c> 로 닫는다(조용히 Claude Code 로 바뀌지 않는다).
    /// <see cref="Model"/>·<see cref="Effort"/>·<see cref="ExecutablePath"/> 는 Claude Code 전용이다.
    /// </summary>
    [JsonConverter(typeof(TolerantSystemCliIdConverter))]
    public string? SystemCliId { get; init; }

    /// <summary>
    /// 사용자가 등록한 기타 CLI 목록. 키가 없거나 null 인 파일은 빈 목록이다. 객체가 아닌 원소는 건너뛰고, 칸이 깨진 원소는 그 칸만 비워(빈 명령 = 실행하지 않음) 설정 전체가 손상으로 취급되지 않게 한다.
    /// 선택은 <see cref="SystemCliId"/> 에 <c>custom:&lt;id&gt;</c> 로 저장한다: 이 목록을 모르는 옛 빌드는 목록에 없는 id 로 읽어 <c>system_cli_unavailable</c> 로 닫는다.
    /// </summary>
    [JsonConverter(typeof(TolerantCustomCliListConverter))]
    public IReadOnlyList<AiCustomCli> CustomClis { get => _customClis; init => _customClis = value is null ? [] : [.. value.Where(c => c is not null)]; }

    // "customClis": null 이나 null 원소가 든 손으로 고친 파일도 받는다(빈 목록, null 원소 제거).
    private readonly IReadOnlyList<AiCustomCli> _customClis = [];

    /// <summary>읽을 수 없는 <c>systemCliId</c> 를 가리키는 id. 어떤 시스템 CLI 와도 겹치지 않는다.</summary>
    public const string UnreadableSystemCliId = "(unreadable)";

    /// <summary><c>claude --effort</c> 가 받는 값(낮은 것부터).</summary>
    public static IReadOnlyList<string> EffortLevels { get; } = ["low", "medium", "high", "xhigh", "max"];

    // 프로필 목록은 참조가 아니라 내용으로 비교한다(설정 구역별 변경 알림이 같은 값을 「바뀜」으로 보지 않게).
    public bool Equals(AiSettings? other) =>
        other is not null
        && Driver == other.Driver
        && Model == other.Model
        && Effort == other.Effort
        && ExecutablePath == other.ExecutablePath
        && MaskLiterals == other.MaskLiterals
        && BenchmarkTimeoutSeconds == other.BenchmarkTimeoutSeconds
        && JoinSyntax == other.JoinSyntax
        && ActiveProfileId == other.ActiveProfileId
        && string.Equals(SystemCliId, other.SystemCliId, StringComparison.Ordinal)
        && Profiles.SequenceEqual(other.Profiles)
        && CustomClis.SequenceEqual(other.CustomClis);

    public override int GetHashCode() => HashCode.Combine(Driver, Model, Effort, ExecutablePath, MaskLiterals, ActiveProfileId, Profiles.Count, HashCode.Combine(SystemCliId, BenchmarkTimeoutSeconds, JoinSyntax, CustomClis.Count));
}

/// <summary>MyBatis 작업 공간 설정. 설정 창에는 없고 「바뀌는 부분」 창의 확인란이 바꾼다.</summary>
public sealed record MyBatisSettings
{
    /// <summary>매퍼에 저장할 때마다 「바뀌는 부분」 창을 먼저 보인다. 끄면 검사가 통과할 때 바로 쓴다.</summary>
    public bool ShowSaveDiffEveryTime { get; init; } = true;

    /// <summary>쿼리 탭 안 「펼친 SQL」 분할 방향(TSK-03-10). false = 세로 분할(편집기 오른쪽, 기본), true = 가로 분할(편집기 아래).</summary>
    public bool ExpandedHorizontal { get; init; }

    /// <summary>쿼리 탭 안에 「펼친 SQL」 을 보인다(분할 머리 줄의 숨김 토글).</summary>
    public bool ShowExpanded { get; init; } = true;

    /// <summary>원문 편집기가 차지하는 비율(0.15~0.85, 기본 0.5). 분할선을 끌어 놓으면 바뀐다.</summary>
    public double ExpandedRatio { get; init; } = 0.5;

    /// <summary>원문 편집기와 펼친 SQL 의 세로 스크롤을 서로 맞춘다(분할 단추 줄의 「스크롤 맞춤」 토글, 기본 켬).</summary>
    public bool SyncScroll { get; init; } = true;
}

/// <summary>테이블 레이아웃 팝업의 확인란 4개. 다음에 연 팝업에도 유지한다. 별칭은 표마다 달라 저장하지 않는다.</summary>
public sealed record TableLayoutSettings
{
    public bool InsertComma { get; init; }

    public bool LowerCase { get; init; }

    public bool ShowPk { get; init; }

    public bool ShowComment { get; init; }

    /// <summary>「감사 컬럼 빼기」 확인란(TSK-13-40). 켜면 <see cref="AuditColumns"/> 에 맞는 컬럼을 표에서 숨긴다.</summary>
    public bool HideAudit { get; init; }

    /// <summary>감사 컬럼 이름 목록(쉼표·줄로 나눔, 와일드카드 *·? 허용, 대소문자 무시). 설정 창 편집기 탭에서 정한다. 기본 비움.</summary>
    public string AuditColumns { get; init; } = string.Empty;
}

public sealed record UiSettings
{
    public Theme Theme { get; init; } = Theme.System;

    /// <summary><c>system</c> | <c>ko</c> | <c>en</c>.</summary>
    public string Language { get; init; } = "system";

    /// <summary>화면 글자 배율(0.8~2.5, 기본 1.0). 메뉴·대화상자·패널 글자가 함께 커진다(TSK-10-02).</summary>
    public double UiScale { get; init; } = 1.0;

    /// <summary>
    /// 패널·표·보조 글자 크기(pt, 8~32). 계획 트리·AI 대화·모니터·도우미 같은 편집기 밖 영역과 작은 글자 전체가 따른다.
    /// null 이면 기본 크기에 <see cref="UiScale"/> 을 곱한 값. 값이 있으면 화면 글자 배율을 곱하지 않는다(그리드 글자 크기와 같은 방식).
    /// </summary>
    public double? PanelFontSize { get; init; }

    /// <summary>고대비 색 사용(OS 고대비 설정이 켜져 있으면 이 값과 상관없이 고대비로 그린다, TSK-10-02).</summary>
    public bool HighContrast { get; init; }
}

public sealed record EditorSettings
{
    /// <summary>null = 플랫폼 기본 고정폭.</summary>
    public string? FontFamily { get; init; }

    public double FontSize { get; init; } = 13;

    public int TabSize { get; init; } = 4;

    /// <summary>실행 전에 흔한 SQL 실수를 경고 밑줄로 알린다(SQL 점검 lint, TSK-13-04).</summary>
    public bool LintEnabled { get; init; } = true;
}

public sealed record ExecutionSettings
{
    public bool AutoCommit { get; init; }

    public bool DbmsOutput { get; init; } = true;

    public ScriptErrorMode ScriptErrorMode { get; init; } = ScriptErrorMode.Ask;

    public bool SplitOnBlankLine { get; init; }

    /// <summary>
    /// 실제 계획(Ctrl+Shift+E)에 A-Rows 가 없으면 묻지 않고 <c>GATHER_PLAN_STATISTICS</c> 힌트 사본으로 다시 실행한다(TSK-06-04).
    /// 기본은 켬: 서버 statistics_level 이 TYPICAL 이어도 A-Rows 가 자동으로 보이게 한다. 저장된 설정에 명시적으로 꺼 둔 값(false)이 있으면 그 값을 따른다.
    /// </summary>
    public bool GatherPlanStatisticsHint { get; init; } = true;
}

public sealed record GridSettings
{
    public int PageSize { get; init; } = 500;

    public int MemoryLimitMb { get; init; } = 200;

    public int InitialLobFetchSize { get; init; } = 4000;

    /// <summary>결과 그리드 글꼴. null 이면 기본 고정폭 글꼴 목록.</summary>
    public string? FontFamily { get; init; }

    /// <summary>결과 그리드 글자 크기(pt, 8~32). 화면 글자 배율을 곱하지 않는다.</summary>
    public double FontSize { get; init; } = 13;

    /// <summary>
    /// 머리글 정렬을 이 PC 에서 바로 하는 행 수의 문턱(<see cref="DefaultLocalSortMaxRows"/>). 이 수에 닿으면 「서버에서 ORDER BY 로 다시 조회」 등을 묻는다.
    /// </summary>
    public int LocalSortMaxRows { get; init; } = DefaultLocalSortMaxRows;

    public const int DefaultLocalSortMaxRows = 100_000;

    /// <summary>
    /// 편집기 결과 탭의 열 필터(머리글 ▾·깔때기)를 쓴다(결과 탭 줄의 필터 단추, <c>oranda.grid.toggleColumnFilter</c>).
    /// 마지막으로 고른 상태를 기억해 새 결과가 그대로 시작한다. 끄면 머리글 클릭이 열 선택이 된다.
    /// </summary>
    public bool ColumnFilterEnabled { get; init; } = true;

    /// <summary>
    /// 결과 그리드 「플래시백 ▸ 이 행 변경 이력」(변경 추적하기, TSK-13-44)을 쓴다. 끄면 그 명령만 메뉴·우클릭·팔레트·단축키에서 숨는다.
    /// </summary>
    public bool FlashbackTrackEnabled { get; init; } = true;

    /// <summary>변경 추적 질의가 읽는 기간: 최근 N일(<see cref="FlashbackTrackDays"/>) 또는 가능한 전체 기간.</summary>
    public FlashbackTrackRange FlashbackTrackRange { get; init; } = FlashbackTrackRange.RecentDays;

    /// <summary>「최근 N일」 의 N(소수 허용, <see cref="MinFlashbackTrackDays"/>~<see cref="MaxFlashbackTrackDays"/>).</summary>
    public double FlashbackTrackDays { get; init; } = DefaultFlashbackTrackDays;

    public const double DefaultFlashbackTrackDays = 1.5;

    public const double MinFlashbackTrackDays = 0.01;

    public const double MaxFlashbackTrackDays = 365;
}

/// <summary>변경 추적 질의 기간(<c>grid.flashbackTrackRange</c>).</summary>
public enum FlashbackTrackRange
{
    /// <summary><c>VERSIONS BETWEEN TIMESTAMP SYSDATE - N AND MAXVALUE</c>.</summary>
    RecentDays,

    /// <summary><c>VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE</c>(DB 에 따라 ORA-08180).</summary>
    All,
}

public sealed record HistorySettings
{
    public bool Enabled { get; init; } = true;

    public bool SaveBindValues { get; init; }

    public int MaxEntries { get; init; } = 50000;

    public int MaxAgeDays { get; init; } = 180;
}

public sealed record MetadataCacheSettings
{
    public int RetentionDays { get; init; } = 30;

    /// <summary>
    /// 객체 목록의 최대 유효기간(일, <c>metadataCache.objectListMaxAgeDays</c>). 서버의 전체 목록을 받은 지(<c>refreshed_at</c>) 이보다 오래면
    /// 캐시로 답할 때 뒤에서 전체 목록을 다시 받는다. 0 = 기한 없음. 선택 필드라 스키마 버전은 그대로.
    /// </summary>
    public int ObjectListMaxAgeDays { get; init; } = 1;
}

public sealed record MonitorSettings
{
    /// <summary>2~60.</summary>
    public int RefreshSeconds { get; init; } = 5;

    /// <summary>Lock 모니터 이력·임계치 알림(oranda1 TSK-05-04). 선택 필드라 스키마 버전은 그대로.</summary>
    public LockAlertSettings LockAlert { get; init; } = new();
}

/// <summary>Lock 모니터 「알림 설정」 플라이아웃 값. 화면이 범위를 맞춘다(대기 1~86400 초, 대기자 1~1000 명, 보관 5~720 분).</summary>
public sealed record LockAlertSettings
{
    /// <summary>임계치 알림 켜기(기본 켬).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>가장 오래 기다린 세션이 이 초 이상이면 알림.</summary>
    public int WaitSeconds { get; init; } = 120;

    /// <summary>한 보유자 뒤 대기자가 이 수 이상이면 알림.</summary>
    public int Waiters { get; init; } = 5;

    /// <summary>알림 때 보유자·대기자 SQL 스냅샷을 앱 데이터 폴더 lock-history 에 텍스트로 남긴다.</summary>
    public bool SaveSnapshot { get; init; } = true;

    /// <summary>Lock 모니터 탭이 뒤에 있어도 창 오른쪽 아래 알림을 띄운다.</summary>
    public bool NotifyWhenHidden { get; init; } = true;

    /// <summary>이력 띠 보관 분(앱 메모리).</summary>
    public int HistoryMinutes { get; init; } = 30;

    /// <summary>이력 점을 lock-history 폴더 CSV 로도 남긴다(기본 끔).</summary>
    public bool SaveHistoryFile { get; init; }
}

public sealed record WorkspaceSettings
{
    /// <summary>1~10.</summary>
    public int AutosaveSeconds { get; init; } = 10;

    public bool RestoreOnStartup { get; init; } = true;

    /// <summary>마지막으로 보던(활성) 작업 공간.</summary>
    public string LastWorkspaceId { get; init; } = "default";

    /// <summary>종료할 때 열려 있던 작업 공간들(열린 순서). 다음 시작 때 모두 다시 연다.</summary>
    public IReadOnlyList<string> OpenWorkspaceIds { get; init; } = ["default"];

    /// <summary>
    /// 워크스페이스마다 새 세션(TSK-14-01, 기본 켬): 같은 DB 를 쓰는 작업 공간이 여럿이면 첫 실행 때 각자 세션을 열어 서로 기다리지 않는다.
    /// 끄면 지금처럼 접속의 세션 하나를 함께 쓴다. 작업 공간마다 고른 값(작업 공간 파일)이 있으면 그것이 먼저다.
    /// </summary>
    public bool SessionPerWorkspace { get; init; } = true;

    // 목록은 참조가 아니라 내용으로 비교한다(설정 구역별 변경 알림이 같은 값을 「바뀜」으로 보지 않게).
    public bool Equals(WorkspaceSettings? other) =>
        other is not null
        && AutosaveSeconds == other.AutosaveSeconds
        && RestoreOnStartup == other.RestoreOnStartup
        && SessionPerWorkspace == other.SessionPerWorkspace
        && LastWorkspaceId == other.LastWorkspaceId
        && OpenWorkspaceIds.SequenceEqual(other.OpenWorkspaceIds);

    public override int GetHashCode() => HashCode.Combine(AutosaveSeconds, RestoreOnStartup, LastWorkspaceId, OpenWorkspaceIds.Count);
}

public sealed record SchemaBrowserSettings
{
    /// <summary>객체를 한 번 누르면(방향키로 고르고 잠깐 멈추면) 미리보기 문서로 연다. 끄면 두 번 누르기·Enter 로만 연다.</summary>
    public bool SingleClickPreview { get; init; } = true;

    /// <summary>
    /// 트리의 표·뷰 줄 이름 옆 테이블 명(주석)을 숨긴다(패널의 「테이블 명」 체크를 끈 것). 숨기면 주석을 읽지도 않는다.
    /// 「숨김」 으로 두는 까닭: 이 키가 없는 옛 settings.json 을 소스 생성 직렬화기가 false 로 읽어도 기본(보임)이 되게.
    /// </summary>
    public bool HideTableComments { get; init; }

    /// <summary>이름 붙여 저장한 트리 필터(오렌지 Schema Browser Filter, TSK-13-10). 모든 접속이 같이 쓴다.</summary>
    public IReadOnlyList<SchemaBrowserFilter> Filters { get => _filters; init => _filters = value ?? []; }

    /// <summary>접속 프로필 id → 그 접속에서 마지막으로 고른 필터 id. 없으면 필터 없음.</summary>
    public IReadOnlyDictionary<string, string> ActiveFilters { get => _activeFilters; init => _activeFilters = value ?? EmptyMap; }

    /// <summary>
    /// 접속 프로필 id → 그 접속의 트리에 보일 객체 유형(<c>|</c> 로 이음, TSK-14-25). 없으면 모든 유형.
    /// </summary>
    public IReadOnlyDictionary<string, string> TypeChoices { get => _typeChoices; init => _typeChoices = value ?? EmptyMap; }

    /// <summary><see cref="TypeChoices"/> 의 값으로 잇는다.</summary>
    public static string JoinTypes(IEnumerable<string> types) => string.Join('|', types);

    /// <summary><see cref="TypeChoices"/> 의 값을 유형 목록으로 나눈다.</summary>
    public static IReadOnlyList<string> SplitTypes(string? joined) =>
        string.IsNullOrEmpty(joined) ? [] : joined.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // 이 키가 생기기 전의 settings.json(구역은 있고 키는 없음)은 소스 생성 직렬화기가 null 을 넣으므로 빈 값으로 받는다.
    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>();
    private readonly IReadOnlyList<SchemaBrowserFilter> _filters = [];
    private readonly IReadOnlyDictionary<string, string> _activeFilters = EmptyMap;
    private readonly IReadOnlyDictionary<string, string> _typeChoices = EmptyMap;

    // 목록·사전은 참조가 아니라 내용으로 비교한다(설정 구역별 변경 알림이 같은 값을 「바뀜」으로 보지 않게).
    public bool Equals(SchemaBrowserSettings? other) =>
        other is not null
        && SingleClickPreview == other.SingleClickPreview
        && HideTableComments == other.HideTableComments
        && Filters.SequenceEqual(other.Filters)
        && ContentEquality.SameEntries(ActiveFilters, other.ActiveFilters)
        && ContentEquality.SameEntries(TypeChoices, other.TypeChoices);

    public override int GetHashCode() => HashCode.Combine(SingleClickPreview, HideTableComments, Filters.Count, ActiveFilters.Count, TypeChoices.Count);
}

/// <summary>저장 필터의 스키마 범위(오렌지 필터 Schema 탭).</summary>
public enum SchemaFilterScope
{
    /// <summary>모든 스키마.</summary>
    All,

    /// <summary>접속한 사용자의 스키마만.</summary>
    LogonSchema,

    /// <summary>객체를 하나 이상 가진 스키마만.</summary>
    SchemasWithObjects,

    /// <summary><see cref="SchemaBrowserFilter.Schemas"/> 에 고른 스키마만.</summary>
    Selected,

    /// <summary><see cref="SchemaBrowserFilter.SchemaPattern"/> 정규식에 맞는 스키마만(대소문자 무시, 일부 일치).</summary>
    Pattern,
}

/// <summary>스키마 브라우저 저장 필터 하나. 스키마 범위 · 숨길 객체 유형 · 유형별 이름 패턴 · 패턴 옵션.</summary>
public sealed record SchemaBrowserFilter
{
    /// <summary>바뀌지 않는 식별자(접속별 선택이 이름 바꾸기에 끊기지 않게).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "";

    public SchemaFilterScope Scope { get; init; } = SchemaFilterScope.All;

    /// <summary><see cref="SchemaFilterScope.Selected"/> 일 때 보일 스키마.</summary>
    public IReadOnlyList<string> Schemas { get => _schemas; init => _schemas = value ?? []; }

    /// <summary><see cref="SchemaFilterScope.Pattern"/> 일 때 스키마 이름에 거는 .NET 정규식. 옛 설정 파일에는 없어 null.</summary>
    public string? SchemaPattern { get; init; }

    /// <summary>트리에서 숨길 객체 유형(ALL_OBJECTS.OBJECT_TYPE). 목록에 없는 새 유형은 보인다.</summary>
    public IReadOnlyList<string> HiddenTypes { get => _hiddenTypes; init => _hiddenTypes = value ?? []; }

    /// <summary>객체 유형 → 이름 패턴(<c>*</c>·<c>%</c> 와일드카드). 없거나 비면 모두.</summary>
    public IReadOnlyDictionary<string, string> NamePatterns { get => _namePatterns; init => _namePatterns = value ?? new Dictionary<string, string>(); }

    /// <summary><c>_</c> 를 한 글자 와일드카드로 본다. 끄면 글자 그대로.</summary>
    public bool UnderscoreIsWildcard { get; init; }

    /// <summary>패턴에 와일드카드가 없으면 앞뒤에 <c>%</c> 를 붙여 포함 검색한다. 끄면 이름 전체가 같아야 한다.</summary>
    public bool WrapWithPercent { get; init; } = true;

    // 키가 빠진 JSON 에서 직렬화기가 넣는 null 은 빈 값으로 받는다.
    private readonly IReadOnlyList<string> _schemas = [];
    private readonly IReadOnlyList<string> _hiddenTypes = [];
    private readonly IReadOnlyDictionary<string, string> _namePatterns = new Dictionary<string, string>();

    public bool Equals(SchemaBrowserFilter? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Scope == other.Scope
        && Schemas.SequenceEqual(other.Schemas)
        && string.Equals(SchemaPattern, other.SchemaPattern, StringComparison.Ordinal)
        && HiddenTypes.SequenceEqual(other.HiddenTypes)
        && ContentEquality.SameEntries(NamePatterns, other.NamePatterns)
        && UnderscoreIsWildcard == other.UnderscoreIsWildcard
        && WrapWithPercent == other.WrapWithPercent;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Scope, Schemas.Count, HiddenTypes.Count, NamePatterns.Count);
}

internal static class ContentEquality
{
    public static bool SameEntries(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));
}

/// <summary>자동 업데이트(TSK-10-01). 확인은 원격 전송 동의 대상이 아니라서 끄는 길만 둔다(PRD 「원격 전송(업데이트 확인 제외)」).</summary>
public sealed record UpdateSettings
{
    /// <summary>메인 창이 뜬 뒤 백그라운드로 새 버전을 확인한다(24시간에 한 번).</summary>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary>새 버전을 찾으면 백그라운드로 내려받는다. 끄면 「있음」만 알린다.</summary>
    public bool AutoDownload { get; init; } = true;

    /// <summary>피드 주소: http(s) 주소, 폴더 경로(UNC 포함), <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;</c>. null·빈 값 = 빌드 때 넣은 기본값.</summary>
    public string? FeedUrl { get; init; }

    /// <summary>시작 확인에서 알리지 않을 버전(「이 버전 건너뛰기」).</summary>
    public string? SkippedVersion { get; init; }

    /// <summary>마지막으로 확인을 끝낸 시각(UTC). 시작 확인의 24시간 규칙에 쓴다.</summary>
    public DateTimeOffset? LastCheckedUtc { get; init; }
}
