using System.Runtime.CompilerServices;
using System.Text;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Security;
using Oranda.Core.Settings;
using Oranda.Core.Storage;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Ai;

/// <summary>
/// 라우터가 한 번 찾아 드라이버에 넘기는 전송 대상. 드라이버는 설정과 자격 증명 저장소를 다시 읽지 않고 이 값만 쓴다
/// (읽는 곳이 둘이면 경고 창에 적힌 곳과 실제 전송처가 어긋날 수 있다).
/// <see cref="Profile"/> 이 null 이면 시스템 CLI 의 Claude Code(구독 인증)다.
/// </summary>
/// <param name="Profile">활성 프로필. null = Claude Code.</param>
/// <param name="ApiKey">프로필의 API 키. 없으면 null. 로그·메시지·명령줄에 넣지 않는다.</param>
internal sealed record AiRoute(AiLlmProfile? Profile, string? ApiKey)
{
    /// <summary>시스템 CLI 의 Claude Code 경로.</summary>
    public static AiRoute ClaudeCode { get; } = new(null, null);

    // 기본 출력(ToString·로그·디버거)에 키가 나오지 않게 한다.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Profile = ").Append(Profile?.Name ?? "(Claude Code)").Append(", ApiKey = ").Append(string.IsNullOrEmpty(ApiKey) ? "(none)" : "***");
        return true;
    }
}

/// <summary>
/// API 경로(OpenAI 형식 프로필) 드라이버가 구현하는 표식. 구현 클래스는 <c>[OrandaService(typeof(IApiAiDriver))]</c> 로 등록하며,
/// <see cref="RoutingAiDriver"/> 가 실행마다 이 형식을 컨테이너에서 찾아 <see cref="RunAsync(AiRequest, AiRoute, CancellationToken)"/> 로 부른다.
/// 등록이 없으면 <c>api_driver_pending</c> 오류가 된다. <see cref="IAiDriver"/> 자체로 등록하면 라우터와 겹쳐 훑는 순서로 승자가 정해지므로 쓰지 않는다.
/// </summary>
internal interface IApiAiDriver : IAiDriver
{
    /// <summary>라우터가 고른 프로필과 키로 실행한다. 설정·자격 증명 저장소를 다시 읽지 않는다.</summary>
    IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, AiRoute route, CancellationToken ct);
}

/// <summary>
/// 시스템 CLI(Claude Code 외: Codex·Antigravity 등) 드라이버가 구현하는 표식. <see cref="CliId"/> 로 구분하며,
/// <c>[OrandaService(typeof(ISystemCliAiDriver))]</c> 로 등록한다. <see cref="IAiDriver"/> 로 직접 등록하지 않는다(라우터와 겹친다).
/// 라우터는 <see cref="AiProfilePresets.SystemClis"/> 에서 사용 가능한 CLI 만 이 드라이버로 보내고, 등록이 없으면 <c>system_cli_unavailable</c> 로 닫는다.
/// <see cref="RunAsync"/> 는 Claude 전용 값(<see cref="AiRequest.Model"/>·<see cref="AiRequest.Effort"/>·실행 파일 경로)을 받지 않는다:
/// 라우터가 요청에서 모델·노력을 비워 넘기고 경로는 아예 넘기지 않는다.
/// 구현은 <c>CliDriver</c> 와 같은 도구 통제 의무를 진다: <see cref="AiRequest.Session"/> 으로 토큰을 발급해 프로세스가 끝나면 폐기하고,
/// 실행 중 <c>Session.IsRevoked</c> 를 확인하며, 모든 도구 호출을 <c>AgentGate</c> 로만 보내고 그 CLI 자체의 셸·파일 도구는 끈다.
/// </summary>
internal interface ISystemCliAiDriver
{
    /// <summary>담당하는 시스템 CLI id(<see cref="AiProfilePresets.SystemClis"/> 의 <c>Id</c>).</summary>
    string CliId { get; }

    /// <summary>모델·노력이 비워진 요청으로 실행한다. 설정·자격 증명 저장소를 다시 읽지 않는다.</summary>
    IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, CancellationToken ct);
}

/// <summary>
/// 사용자가 등록한 기타 CLI 드라이버가 구현하는 표식. 구현 클래스는 <c>[OrandaService(typeof(ICustomCliAiDriver))]</c> 로 등록하며,
/// <see cref="RoutingAiDriver"/> 가 실행마다 이 형식을 컨테이너에서 찾아 <see cref="RunAsync"/> 로 부른다. 등록이 없으면 <c>custom_cli_unavailable</c> 로 닫는다.
/// <see cref="ISystemCliAiDriver"/> 와 달리 DB 도구·실행 토큰·<see cref="AiRequest.Session"/> 을 쓰지 않는다: 글만 주고받고, 라우터가 요청의 세션을 비워 넘기므로 구현은 토큰을 발급하지 않는다.
/// 라우터가 가림 대상 요청(<see cref="AiRequest.MaskingRequired"/> 가 false 가 아님·가림 설정·운영 접속)을 걸러 낸 뒤에만 부른다.
/// </summary>
internal interface ICustomCliAiDriver
{
    /// <summary>모델·노력이 비워진 요청과 라우터가 찾은 등록 줄로 실행한다. 설정을 다시 읽지 않는다.</summary>
    IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, AiCustomCli cli, CancellationToken ct);
}

/// <summary>
/// <see cref="IAiDriver"/> 의 유일한 DI 등록(설계 §6). 실행마다 설정(<c>ai</c> 섹션)을 읽어 활성 프로필을 한 번 찾고, 맞는 경로로 넘긴다.
/// 보낼 곳이 확실하지 않으면 보내지 않고 오류로 닫는다(기본 주소·구독 인증으로 조용히 나가는 일이 없게).
/// <list type="bullet">
/// <item>설정이 손상 복구된 직후 첫 실행: <c>settings_reset</c>(다음 실행부터 통과).</item>
/// <item>선택 안 함: <c>driver_none</c>.</item>
/// <item>호출한 쪽이 기대한 프로필과 설정의 활성 프로필이 다름(<see cref="AiRequest.Expected"/>): <c>profile_changed</c>.</item>
/// <item>시스템 CLI 가 목록에 없거나 쓸 수 없음, 또는 드라이버 등록이 없음: <c>system_cli_unavailable</c>(Claude Code 로 넘기지 않는다).</item>
/// <item>기타 CLI(<c>custom:&lt;id&gt;</c>)가 목록에 없거나 명령이 비었거나 드라이버 등록이 없음: <c>custom_cli_unavailable</c>(Claude Code 로 넘기지 않는다).
/// 확인한 명령과 지금 명령이 다름: <c>profile_changed</c>. 가림 대상(<see cref="AiRequest.MaskingRequired"/> 가 false 가 아님·가림 설정 켬·운영 접속): <c>custom_cli_masking_blocked</c>. 이미지가 든 요청: <c>custom_cli_images_unsupported</c>.</item>
/// <item>활성 프로필이 목록에 없음: <c>profile_missing</c>. 프리셋·형식을 모름: <c>profile_invalid</c>.</item>
/// <item>주소가 올바르지 않음: <c>profile_invalid_url</c>. 키가 필요한데 없음: <c>profile_key_missing</c>(읽기 실패는 <c>profile_key_unreadable</c>).</item>
/// <item>Claude Code·Anthropic 형식 프로필: <see cref="CliDriver"/>. OpenAI 형식 프로필: <see cref="IApiAiDriver"/>(등록이 없으면 <c>api_driver_pending</c>).</item>
/// </list>
/// <see cref="Capabilities"/> 는 동기 속성이라 프로필별로 답하지 못하므로 고정 값이다(<see cref="AiDriverCapabilities.Images"/> 는 항상 true).
/// 프로필별 이미지 지원 여부는 화면이 <see cref="AiLlmProfile.SupportsImages"/> 로 판단한다.
/// </summary>
[OrandaService(typeof(IAiDriver))]
internal sealed class RoutingAiDriver : IAiDriver
{
    internal const string CustomCliMaskingBlockedCode = "custom_cli_masking_blocked";

    internal const string CustomCliImagesUnsupportedCode = "custom_cli_images_unsupported";

    internal const string CustomCliImagesUnsupportedMessage = "기타 CLI 는 이미지를 받지 못합니다. 이미지를 빼고 다시 보내세요.";

    private static readonly AiDriverCapabilities DriverCapabilities =
        new(Skills: false, Tools: true, Resume: true, LocalOnly: false, Streaming: false, Images: true);

    private readonly ISettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly CliDriver _cli;
    private readonly IServiceProvider _services;

    public RoutingAiDriver(ISettingsStore settings, ICredentialStore credentials, CliDriver cli, IServiceProvider services)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public AiDriverCapabilities Capabilities => DriverCapabilities;

    /// <summary>시스템 CLI 목록. 기본은 지금 플랫폼의 표이고, 시험이 플랫폼별 목록을 넣을 수 있다.</summary>
    internal IReadOnlyList<AiSystemCliInfo> SystemClis { get; init; } = AiProfilePresets.SystemClis;

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var selected = await SelectAsync(request, ct).ConfigureAwait(false);
        if (selected.Error is not null)
        {
            yield return selected.Error;
            yield break;
        }

        var events = selected.CustomDriver is { } customDriver
            ? customDriver.RunAsync(request with { Model = null, Effort = null, Session = null }, selected.CustomCli!, ct) // 도구 세션은 넘기지 않는다(글만 주고받는다)
            : selected.SystemCli is { } systemCli
                ? systemCli.RunAsync(request with { Model = null, Effort = null }, ct)
                : selected.Api is { } api ? api.RunAsync(request, selected.Route!, ct) : _cli.RunAsync(request, selected.Route!, ct);
        await foreach (var e in events.ConfigureAwait(false))
        {
            yield return e;
        }
    }

    private async Task<Selection> SelectAsync(AiRequest request, CancellationToken ct)
    {
        AiSettings settings;
        try
        {
            settings = (await _settings.LoadAsync(ct).ConfigureAwait(false)).Value.Ai;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Selection.Fail($"설정을 읽지 못했습니다: {ex.Message}", "settings_unreadable");
        }

        // 손상 복구 직후 첫 실행: 활성 프로필 선택이 기본값으로 돌아가 있을 수 있으므로 한 번 멈추고 사용자가 확인하게 한다.
        if (_settings is IAiSettingsResetSignal reset && reset.TryTakeReset())
        {
            return Selection.Fail("설정 파일이 손상되어 기본값으로 복구되었습니다. 설정의 「AI 모델」 을 확인한 뒤 다시 실행하세요.", "settings_reset");
        }

        var active = AiActiveProfile.Find(settings, SystemClis);
        if (active.Kind == AiActiveKind.None)
        {
            return Selection.Fail("AI 모델이 선택되지 않았습니다. 설정의 「AI 모델」 에서 고르세요.", "driver_none");
        }

        if (request.Expected is { } expected && expected.ProfileId != settings.ActiveProfileId)
        {
            return Selection.Fail("AI 모델이 바뀌어 요청을 보내지 않았습니다. 설정을 확인한 뒤 다시 실행하세요.", "profile_changed");
        }

        // 프로필이 아닌 쪽(시스템 CLI)에서는 기대한 CLI 와 지금 설정의 CLI 도 같아야 한다(null 과 claude-code 는 같다).
        if (request.Expected is { } expectedCli && settings.ActiveProfileId is null
            && NormalizeCliId(expectedCli.SystemCliId) != NormalizeCliId(settings.SystemCliId))
        {
            return Selection.Fail("AI 모델이 바뀌어 요청을 보내지 않았습니다. 설정을 확인한 뒤 다시 실행하세요.", "profile_changed");
        }

        switch (active.Kind)
        {
            case AiActiveKind.ClaudeCode:
                return Selection.Cli(AiRoute.ClaudeCode);
            case AiActiveKind.SystemCliUnavailable:
                return SystemCliUnavailable(active.SystemCliId);
            case AiActiveKind.CustomCliUnavailable:
                return CustomCliUnavailable();
            case AiActiveKind.CustomCli:
                return SelectCustomCli(request, settings, active.CustomCli!);
            case AiActiveKind.SystemCli:
                var cliDriver = (_services.GetService(typeof(IEnumerable<ISystemCliAiDriver>)) as IEnumerable<ISystemCliAiDriver>)
                    ?.FirstOrDefault(d => string.Equals(d.CliId, active.SystemCliId, StringComparison.Ordinal));
                if (cliDriver is null)
                {
                    return SystemCliUnavailable(active.SystemCliId);
                }

                // 운영 접속도 막지 않는다(D-039). 게이트와 운영 리터럴 가림은 이 경로에도 그대로 적용된다.
                return Selection.ViaSystemCli(cliDriver);
            case AiActiveKind.ProfileMissing:
                return Selection.Fail("선택한 LLM 프로필을 찾지 못했습니다. 설정의 「AI 모델」 에서 프로필을 다시 고르세요.", AiActiveProfile.ProfileMissingCode);
            case AiActiveKind.ProfileInvalid:
                return Selection.Fail($"프로필 「{active.Profile!.Name}」 의 종류나 API 형식을 이 버전이 알지 못합니다. 프로필을 다시 만드세요.", AiActiveProfile.ProfileInvalidCode);
            default:
                if (request.Expected is { } check && !MatchesExpectation(check, active.Profile!))
                {
                    return Selection.Fail("AI 모델의 접속 정보가 바뀌어 요청을 보내지 않았습니다. 설정을 확인한 뒤 다시 실행하세요.", "profile_changed");
                }

                return await SelectProfileAsync(active.Profile!, ct).ConfigureAwait(false);
        }
    }

    private Selection SelectCustomCli(AiRequest request, AiSettings settings, AiCustomCli cli)
    {
        // 확인 창에 적은 명령과 지금 명령이 다르면 보내지 않는다. 확인한 명령이 없는 요청(기대값 자체가 null 이거나 해시가 null)도 같은 대상으로 보지 않는다.
        if (!string.Equals(request.Expected?.CustomCliCommandHash, AiCustomCliTarget.CommandHash(cli.Command), StringComparison.Ordinal))
        {
            return Selection.Fail("AI 모델이 바뀌어 요청을 보내지 않았습니다. 설정을 확인한 뒤 다시 실행하세요.", "profile_changed");
        }

        // 이미지는 드라이버까지 가지 않게 여기서도 막는다(흐름의 가드에만 기대면 새 호출 경로가 생길 때 뚫린다).
        if (request.Images is { Count: > 0 })
        {
            return Selection.Fail(CustomCliImagesUnsupportedMessage, CustomCliImagesUnsupportedCode);
        }

        // 기타 CLI 는 값을 가려 보낼 수 없다. 가림 대상(운영 표시·전역 가림 설정·세션의 리터럴 가림 요구)이거나 가림이 필요 없다고 확인되지 않은 요청은 접속이 없어도 보내지 않는다.
        if (request.MaskingRequired != false || settings.MaskLiterals || IsProduction(request) || request.Session?.Run.MaskLiteralsRequired == true)
        {
            return Selection.Fail("가림 대상 접속(운영 표시 또는 값 가림 설정)에서는 기타 CLI 로 보내지 않습니다. 요청을 보내지 않았으니 설정의 「AI 모델」 에서 다른 모델을 고르세요.", CustomCliMaskingBlockedCode);
        }

        return _services.GetService(typeof(ICustomCliAiDriver)) is ICustomCliAiDriver driver
            ? Selection.ViaCustomCli(driver, cli)
            : CustomCliUnavailable();
    }

    private static bool IsProduction(AiRequest request) =>
        request.Context.Environment == ConnectionEnvironment.Production
        || request.Session?.Run.Context.Environment == ConnectionEnvironment.Production;

    private static Selection CustomCliUnavailable() =>
        Selection.Fail("선택한 기타 CLI 를 쓸 수 없습니다. 목록에서 지워졌거나 실행 명령이 비어 있습니다. 설정의 「AI 모델」 에서 다시 고르세요.", AiActiveProfile.CustomCliUnavailableCode);

    private static string NormalizeCliId(string? id) => id ?? AiProfilePresets.ClaudeCodeId;

    private static Selection SystemCliUnavailable(string? cliId) =>
        Selection.Fail($"선택한 시스템 CLI({cliId})를 이 컴퓨터에서 쓸 수 없습니다. 설정의 「AI 모델」 에서 다른 모델을 고르세요.", AiActiveProfile.SystemCliUnavailableCode);

    private static bool MatchesExpectation(AiProfileExpectation expected, AiLlmProfile profile) =>
        (expected.BaseUrl is null || string.Equals(expected.BaseUrl.Trim(), profile.BaseUrl.Trim(), StringComparison.Ordinal))
        && (expected.Format is null || expected.Format == profile.Format)
        && (!expected.LocalAi || profile.IsLocalAi); // 값을 가리지 않은 요청은 지금도 「로컬 AI」 로 지정된 프로필로만 나간다(D-045)

    private async Task<Selection> SelectProfileAsync(AiLlmProfile profile, CancellationToken ct)
    {
        // 주소가 올바르지 않으면 기본 주소(구독 계정)나 엉뚱한 곳으로 나갈 수 있으므로 여기서 닫는다.
        if (AiProfileUrl.BaseFor(profile.Format, profile.BaseUrl) is null)
        {
            return Selection.Fail($"프로필 「{profile.Name}」 의 서버 주소가 올바르지 않습니다. http:// 또는 https:// 로 시작하는 주소를 입력하세요.", "profile_invalid_url");
        }

        string? key;
        try
        {
            var bytes = await _credentials.LoadAsync(AiProfileCredentials.KeyFor(profile.Id)).ConfigureAwait(false);
            key = bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes).Trim() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (AiProfilePresets.Get(profile.Preset).KeyRequirement == AiKeyRequirement.NotNeeded)
            {
                // 키가 필요 없는 프리셋(Ollama·LM Studio)은 키체인이 읽히지 않아도 키 없이 진행한다.
                key = null;
            }
            else
            {
                // 예외 메시지에 비밀 저장소 내용이 섞일 수 있어 종류만 알린다.
                return Selection.Fail($"프로필 「{profile.Name}」 의 API 키를 읽지 못했습니다({ex.GetType().Name}).", "profile_key_unreadable");
            }
        }

        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(key) && AiProfilePresets.Get(profile.Preset).KeyRequirement == AiKeyRequirement.Required)
        {
            return Selection.Fail($"프로필 「{profile.Name}」 에는 API 키가 필요합니다. 프로필 관리에서 키를 입력하세요.", "profile_key_missing");
        }

        var route = new AiRoute(profile, string.IsNullOrEmpty(key) ? null : key);
        if (profile.Format == AiApiFormat.Anthropic)
        {
            return Selection.Cli(route);
        }

        return _services.GetService(typeof(IApiAiDriver)) is IApiAiDriver api
            ? Selection.ViaApi(api, route)
            : Selection.Fail("OpenAI 형식 프로필의 연결은 아직 준비되지 않았습니다.", "api_driver_pending");
    }

    private sealed record Selection(AiRoute? Route, IApiAiDriver? Api, AiError? Error, ISystemCliAiDriver? SystemCli = null, ICustomCliAiDriver? CustomDriver = null, AiCustomCli? CustomCli = null)
    {
        public static Selection ViaCustomCli(ICustomCliAiDriver driver, AiCustomCli cli) => new(null, null, null, null, driver, cli);

        public static Selection Cli(AiRoute route) => new(route, null, null);

        public static Selection ViaApi(IApiAiDriver api, AiRoute route) => new(route, api, null);

        public static Selection ViaSystemCli(ISystemCliAiDriver driver) => new(null, null, null, driver);

        public static Selection Fail(string message, string code) => new(null, null, new AiError(message, code));
    }
}
