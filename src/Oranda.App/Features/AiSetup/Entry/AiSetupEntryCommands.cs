using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Shell;

namespace Oranda.App.Features.AiSetup.Entry;

/// <summary>
/// 「도구 → AI 연결 도우미…」(명령 <c>oranda.ai.setup.open</c>, 외부 에이전트 연결 바로 위). 도우미 창을 연다.
/// 네임스페이스가 <c>Features.AiSetup</c> 이라 aiTuning 이 꺼져 있어도 메뉴에 보인다. 런처(<see cref="IAiSetupLauncher"/>)가 없으면 아무것도 안 한다.
/// </summary>
[OrandaCommand(AiSetupIds.OpenCommand, CommandCategory.Tools, Title = AiSetupEntryText.MenuTitle, Placements = ["menu:tools/externalAgent@44"])]
public sealed class AiSetupOpenCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public AiSetupOpenCommandHandler()
    {
    }

    public AiSetupOpenCommandHandler(IServiceProvider services) => _services = services;

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        AiSetupOpener.OpenAsync(_services, AiSetupEntry.Menu, reason: null, cancellationToken);
}

/// <summary>진입점이 도우미를 여는 길(한 곳). 런처가 없으면(창 틀이 아직 없음·시험) 아무것도 안 한다.</summary>
public static class AiSetupOpener
{
    /// <summary>시험용: 앱 서비스 대신 쓸 공급자. null 이면 앱의 서비스.</summary>
    internal static IServiceProvider? ServicesOverride { get; set; }

    /// <summary>열었으면(런처가 있었으면) true.</summary>
    public static async Task<bool> OpenAsync(IServiceProvider? services, AiSetupEntry entry, AiSetupErrorKind? reason, CancellationToken ct)
    {
        if ((services ?? AppServices())?.GetRegisteredOrDefault<IAiSetupLauncher>() is not { } launcher)
        {
            return false;
        }

        await launcher.OpenAsync(entry, reason, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>앱 서비스에서 런처를 찾아 연다(실패 띠·설정 링크처럼 공급자를 받지 않는 곳).</summary>
    public static Task<bool> OpenFromAppAsync(AiSetupEntry entry, AiSetupErrorKind? reason) => OpenAsync(null, entry, reason, CancellationToken.None);

    /// <summary>앱 설정의 그 등급 실행 대상 종류(실패 띠 판단용). 설정 서비스가 없으면 null.</summary>
    public static AiActiveKind? CurrentActiveKind(AiTier tier = AiTier.Main) =>
        AppServices()?.GetRegisteredOrDefault<SettingsService>() is { } settings ? AiActiveProfile.Find(settings.Current.Ai, tier).Kind : null;

    private static IServiceProvider? AppServices() => ServicesOverride ?? (Avalonia.Application.Current as App)?.Services;
}

/// <summary>
/// 실패 띠에 「AI 연결 도우미 열기」 단추를 보일지(판단은 여기 한 곳). AI 모델 「선택 안 함」·Claude Code 를 못 찾음·로그인 안 됨일 때만 보인다.
/// <c>cli_not_found</c> 는 다른 시스템 CLI·기타 CLI 에서도 나오므로 지금 대상이 Claude Code 일 때만 본다.
/// </summary>
public static class AiSetupOffer
{
    /// <summary>라우터가 「선택 안 함」 으로 막을 때의 코드.</summary>
    public const string DriverNoneCode = "driver_none";

    /// <summary>단추를 보이는가. <paramref name="activeKind"/> = 지금 설정의 실행 대상(모르면 null — 코드만으로 판단).</summary>
    public static bool ShouldOffer(string? errorCode, AiActiveKind? activeKind) =>
        (activeKind == AiActiveKind.None && errorCode is null)
        || errorCode == DriverNoneCode
        || ((activeKind is AiActiveKind.ClaudeCode or null) && (errorCode is AiSetupErrorCodes.CliNotFound or AiSetupErrorCodes.CliNotLoggedIn));

    /// <summary>도우미에 넘길 까닭(로그인 안 됨이면 로그인 단계로 바로 갈 수 있게). 「선택 안 함」 등은 null.</summary>
    public static AiSetupErrorKind? ReasonFor(string? errorCode) =>
        errorCode is AiSetupErrorCodes.CliNotFound or AiSetupErrorCodes.CliNotLoggedIn ? AiSetupErrorCodes.KindFor(errorCode) : null;
}
