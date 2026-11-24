using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Update;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;
using Oranda.Core.Events;
using Oranda.Core.Security;

namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>
/// 도우미 창 열기(계약 <see cref="IAiSetupLauncher"/>). 창은 하나만: 이미 열려 있으면 앞으로 가져오고 그 창이 닫힐 때까지 기다린다.
/// 연 곳(메뉴·첫 실행·실패 띠)은 기록만 하고 첫 화면은 같다(실패 까닭만 이어 하기가 없을 때 시작 단계를 고른다).
/// <para>
/// AI 설정은 한곳(설정 「AI」 탭, ai-settings-one-place 2026-11-24): 앱에서는 도우미가 늘 설정 창 위의 자식 창으로 돈다.
/// <see cref="OpenAsync(AiSetupEntry, AiSetupErrorKind?, CancellationToken)"/>(첫 실행 묻기·실패 띠·옛 명령)는 설정 창을 「AI」 탭으로 열고 그 창 위에 도우미를 띄운다
/// (설정 창이 이미 열려 있으면 그 창 위에). 탭의 단추는 <see cref="OpenOwnedAsync"/> 로 바로 연다. 도우미가 닫히면 탭이 방금 고른 값을 다시 읽는다.
/// </para>
/// </summary>
[OrandaService(typeof(IAiSetupLauncher))]
public sealed class AiSetupLauncher : IAiSetupLauncher
{
    private readonly Func<Window?> _owner;
    private readonly Func<AiSetupEntry, AiSetupErrorKind?, AiSetupWindow?> _create;
    private AiSetupWindow? _window;
    private Task _shown = Task.CompletedTask;
    private readonly Func<AiSetupEntry, AiSetupErrorKind?, Task?>? _viaSettings;

    public AiSetupLauncher(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _owner = () => (services.GetService(typeof(ShellController)) as ShellController)?.MainWindow;
        _create = (entry, reason) => CreateWindow(services, entry, reason);
        _viaSettings = (entry, reason) => OpenThroughSettings(services, entry, reason);
    }

    /// <summary>시험용: 주인 창과 창 만들기를 준다.</summary>
    internal AiSetupLauncher(Func<Window?> owner, Func<AiSetupEntry, AiSetupErrorKind?, AiSetupWindow?> create)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    /// <summary>마지막으로 연 곳(기록).</summary>
    public AiSetupEntry? LastEntry { get; private set; }

    /// <summary>지금 열린 창(시험용). 없으면 null.</summary>
    internal AiSetupWindow? OpenWindow => _window;

    public Task OpenAsync(AiSetupEntry entry, AiSetupErrorKind? reason, CancellationToken ct)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.InvokeAsync(() => OpenAsync(entry, reason, ct));
        }

        LastEntry = entry;
        if (_window is { } open)
        {
            open.Activate();
            return _shown;
        }

        // 앱: 설정 「AI」 탭을 거친다(탭이 도우미를 띄우고, 닫히면 고른 값을 보인다). 설정 창을 못 여는 조립(시험)이면 주 창 위에 바로.
        if (_viaSettings?.Invoke(entry, reason) is { } throughSettings)
        {
            return throughSettings;
        }

        return Show(_owner(), entry, reason);
    }

    /// <summary>
    /// 주인 창을 정해 연다(설정 「AI」 탭의 단추). 주인이 없으면 주 창 위에. 이미 열려 있으면 앞으로 가져오고 그 창을 기다린다.
    /// </summary>
    public Task OpenOwnedAsync(Window? owner, AiSetupEntry entry, AiSetupErrorKind? reason)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.InvokeAsync(() => OpenOwnedAsync(owner, entry, reason));
        }

        LastEntry = entry;
        if (_window is { } open)
        {
            open.Activate();
            return _shown;
        }

        return Show(owner ?? _owner(), entry, reason);
    }

    /// <summary>
    /// 설정 창을 「AI」 탭으로 열고 그 위에 도우미를 띄운다. 설정 창이 이미 열려 있으면 그 창의 「AI」 탭에서. 셸이 없으면 null(호출한 쪽이 주 창 위에 연다).
    /// 돌려주는 일은 도우미가 닫히면 끝난다.
    /// </summary>
    private static Task? OpenThroughSettings(IServiceProvider services, AiSetupEntry entry, AiSetupErrorKind? reason)
    {
        if (SettingsWindow.Current is { DataContext: SettingsViewModel open })
        {
            open.SelectedTab = SettingsViewModel.AiTabIndex;
            return open.AiHub.RunWizardAsync(entry, reason);
        }

        if (services.GetService(typeof(ShellController)) is not ShellController { MainWindow: not null } shell)
        {
            return null;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.ShowSettings(vm =>
        {
            vm.SelectedTab = SettingsViewModel.AiTabIndex;
            vm.AiHub.QueueWizard(entry, reason, done);
        });
        return done.Task;
    }

    private Task Show(Window? owner, AiSetupEntry entry, AiSetupErrorKind? reason)
    {
        if (owner is null || _create(entry, reason) is not { } window)
        {
            return Task.CompletedTask;
        }

        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        try
        {
            _shown = window.ShowDialog(owner);
        }
        catch (Exception)
        {
            // 주인 창이 아직 안 보이는 등으로 못 띄우면 다음에 다시 만들 수 있게 비운다.
            _window = null;
            window.ViewModel.Dispose();
            _shown = Task.CompletedTask;
        }

        return _shown;
    }

    /// <summary>앱 서비스에서 맥락·단계 팩터리를 모아 창을 만든다. 설정 서비스가 없으면 null.</summary>
    internal static AiSetupWindow? CreateWindow(IServiceProvider services, AiSetupEntry entry, AiSetupErrorKind? reason)
    {
        if (services.GetService(typeof(SettingsService)) is not SettingsService settings)
        {
            return null;
        }

        AiSetupWindow? window = null;
        var context = CreateContext(services, settings, entry, reason, () => window);
        var factories = services.GetService(typeof(IEnumerable<IAiSetupStepFactory>)) as IEnumerable<IAiSetupStepFactory> ?? [];
        window = new AiSetupWindow(new AiSetupWizardViewModel(context, factories.ToList()));
        return window;
    }

    /// <summary>
    /// 도우미 맥락(단계들이 쓰는 앱 부품). 도우미 창과 설정 「AI」 탭의 「연결 시험」 이 함께 쓴다.
    /// <paramref name="owner"/> = 클립보드를 쓸 창(없으면 복사하지 않는다).
    /// </summary>
    internal static AiSetupContext CreateContext(IServiceProvider services, SettingsService settings, AiSetupEntry entry, AiSetupErrorKind? reason, Func<Window?>? owner)
    {
        return new AiSetupContext(settings)
        {
            Entry = entry,
            Reason = reason,
            Text = services.GetRegisteredOrDefault<IShellText>(),
            Connections = services.GetRegisteredOrDefault<IConnectionService>(),
            Claude = services.GetRegisteredOrDefault<IClaudeSetupService>(),
            Probe = services.GetRegisteredOrDefault<IAiProfileProbe>(),
            Credentials = services.GetRegisteredOrDefault<ICredentialStore>(),
            Bus = services.GetRegisteredOrDefault<IEventBus>(),
            Services = services,
            CopyText = async text =>
            {
                if (owner?.Invoke() is not { } window)
                {
                    return;
                }

                try
                {
                    await ClipboardText.SetAsync(window, text).ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // 다른 프로그램이 클립보드를 쥐고 있으면 복사만 안 된다.
                }
            },
            OpenUrl = FileLauncher.Open,
            AppVersion = AppVersion.Assembly,
        };
    }
}
