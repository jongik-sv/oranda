using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Controls;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;
using Oranda.Core.Features;
using Oranda.Core.Settings;

namespace Oranda.App.Features.AiSetup.Entry;

/// <summary>
/// 첫 실행 한 번 묻기(시작 작업, 유휴 때). 조건이 맞으면 창이 뜬 뒤 「AI 를 연결할까요?」 [AI 연결 도우미 열기][나중에] 를 한 번 묻는다.
/// 어느 쪽이든 <c>ai.setup.firstRunPromptClosed</c> 를 참으로 저장해 다시 묻지 않는다. 묻지 못했으면(주 창 없음 — 시험·Headless) 저장하지 않는다.
/// claude 찾기는 화면 스레드 밖에서 하고, 대화상자는 화면 스레드에 넘기기만 하고 기다리지 않는다(뒤 시작 작업을 막지 않음).
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class AiSetupFirstRunPrompt(IServiceProvider services) : IStartupTask
{
    public StartupPhase Phase => StartupPhase.Idle;

    /// <summary>묻는 창(영어 여부 → 열기 = true, 나중에·닫기 = false, 묻지 못함 = null). 시험이 바꾼다.</summary>
    internal Func<bool, Task<bool?>> Ask { get; init; } = AiSetupAskDialog.ShowAsync;

    /// <summary>
    /// 물을 주 창이 떠 있다(화면 스레드에서 본다). 거짓이면(시험·Headless) claude 찾기(실제 <c>claude --version</c> 실행)도 하지 않는다. 시험이 바꾼다.
    /// </summary>
    internal Func<bool> CanAsk { get; init; } = AiSetupAskDialog.HasOwner;

    /// <summary>
    /// 물어도 되는가(claude 찾기 전 조건): aiTuning 켬 · 대상이 Claude Code(프로필·다른 시스템 CLI·기타 CLI 안 고름) · 아직 닫지 않음.
    /// 드라이버 「선택 안 함」 도 묻지 않는다(사용자가 고른 값).
    /// </summary>
    public static bool MayAsk(bool aiTuningOn, AiSettings ai)
    {
        ArgumentNullException.ThrowIfNull(ai);
        return aiTuningOn && !ai.Setup.FirstRunPromptClosed && AiActiveProfile.Find(ai).Kind == AiActiveKind.ClaudeCode;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (services.GetRegisteredOrDefault<SettingsService>() is not { } settings
            || services.GetRegisteredOrDefault<IClaudeSetupService>() is not { } claude)
        {
            return; // 찾기 서비스가 없으면 claude 가 없는지 알 수 없다: 묻지 않는다
        }

        var aiTuningOn = services.GetService(typeof(IFeatureGate)) is IFeatureGate gate && gate.IsEnabled(FeatureIds.AiTuning);
        if (!MayAsk(aiTuningOn, settings.Current.Ai))
        {
            return;
        }

        if (!(Dispatcher.UIThread.CheckAccess() ? CanAsk() : await Dispatcher.UIThread.InvokeAsync(CanAsk)))
        {
            return; // 물을 창이 없다(시험·Headless. 실제 앱은 유휴 시작 작업 때 주 창이 이미 떠 있다): 찾지도 않는다, 다음 실행에 다시 본다
        }

        ClaudeInstallation? found;
        try
        {
            found = await Task.Run(() => claude.FindAsync(ct), ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 시작 작업이다: 찾기가 실패하면 묻지 않고 넘어간다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Trace.TraceWarning("AI 연결 묻기: claude 찾기 실패 {0}", ex.GetType().Name);
            return;
        }

        if (found is not null)
        {
            return;
        }

        // 사용자가 답할 때까지 기다리면 뒤의 시작 작업이 멈춘다: 화면 스레드에 넘기고 바로 돌아간다.
        Dispatcher.UIThread.Post(() => _ = AskOnceSafeAsync(settings));
    }

    private async Task AskOnceSafeAsync(SettingsService settings)
    {
        try
        {
            await AskOnceAsync(settings, CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 화면 스레드에 넘긴 일이다: 예외를 밖으로 내면 앱 전역 미처리 예외가 된다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning("AI 연결 묻기 실패: {0}", ex.GetType().Name);
        }
    }

    /// <summary>묻고 「닫음」 을 저장한 뒤, 열기를 골랐으면 도우미를 연다. 화면 스레드에서 부른다.</summary>
    internal async Task AskOnceAsync(SettingsService settings, CancellationToken ct)
    {
        if (settings.Current.Ai.Setup.FirstRunPromptClosed)
        {
            return; // 이미 물었다(같은 실행에서 두 번 불린 경우)
        }

        if (await Ask(AiSetupText.CurrentIsEnglish).ConfigureAwait(true) is not { } open)
        {
            return; // 묻지 못함: 닫음을 저장하지 않는다(다음 실행에 묻는다)
        }

        await settings.UpdateAsync(s => s with { Ai = s.Ai with { Setup = s.Ai.Setup with { FirstRunPromptClosed = true } } }, ct).ConfigureAwait(true);
        if (open)
        {
            await AiSetupOpener.OpenAsync(services, AiSetupEntry.FirstRun, reason: AiSetupErrorKind.CliNotFound, ct).ConfigureAwait(true);
        }
    }
}

/// <summary>첫 실행 묻기 작은 대화상자(주 창 위). 보이는 주 창이 없으면(시험·Headless) 묻지 못함(null).</summary>
internal static class AiSetupAskDialog
{
    /// <summary>보이는 주 창이 있다(화면 스레드에서 부른다).</summary>
    public static bool HasOwner() => DialogShell.MainWindow is { IsVisible: true };

    public static async Task<bool?> ShowAsync(bool english)
    {
        if (DialogShell.MainWindow is not { IsVisible: true } owner)
        {
            return null;
        }

        var open = false;
        var dialog = new Window
        {
            Title = AiSetupEntryText.AskTitle(english),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            MinWidth = 320,
            ShowInTaskbar = false,
        };
        var yes = new Button { Content = AiSetupEntryText.OpenButton(english), MinWidth = 80, IsDefault = true, Classes = { "accent" } };
        var later = new Button { Content = AiSetupEntryText.Later(english), MinWidth = 80, IsCancel = true };
        yes.Click += (_, _) =>
        {
            open = true;
            dialog.Close();
        };
        later.Click += (_, _) => dialog.Close();
        var message = new TextBlock { Text = AiSetupEntryText.AskMessage(english), TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        dialog.Content = DialogShell.Framed(message, DialogShell.Buttons(yes, later));
        dialog.Opened += (_, _) => yes.Focus();
        await DialogShell.ShowAsync(dialog, owner).ConfigureAwait(true);
        return open;
    }
}
