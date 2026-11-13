using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.GridEdit;
using Oranda.App.Shell;
using Oranda.Core.Rollback;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Rollback;

/// <summary>
/// 원복 스크립트 명령(되돌리기 지점 설계 11.3, 13.4). <see cref="RollbackCommandIds.OpenScript"/> 는 변경 일지(RB-42)가 부르고,
/// <see cref="RollbackCommandIds.RunScript"/> 는 활성 원복 스크립트 탭의 「원복 실행 (커밋 안 함)」 이다. 메뉴 자리는 없고 명령 팔레트로도 부를 수 있다. 서비스는 실행할 때 꺼낸다.
/// </summary>
[OrandaCommand(RollbackCommandIds.OpenScript, CommandCategory.Tools, Title = "원복 스크립트 열기")]
[OrandaCommand(RollbackCommandIds.RunScript, CommandCategory.Tools, Title = "원복 실행 (커밋 안 함)")]
public sealed class RollbackCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public RollbackCommandHandler()
    {
    }

    public RollbackCommandHandler(IServiceProvider services) => _services = services;

    /// <summary>시험용: 문서 탭을 여는 방법(기본 = 셸 도킹의 <c>OpenDocument</c>).</summary>
    internal Func<RollbackScriptViewModel, bool>? OpenDocument { get; set; }

    /// <summary>시험용: 활성 원복 스크립트 탭(기본 = 도킹의 활성 문서).</summary>
    internal Func<RollbackScriptViewModel?>? ActiveScript { get; set; }

    /// <summary>시험용: 안내를 받는 곳(기본 = 상태 표시줄 알림).</summary>
    internal Action<string, bool>? Notify { get; set; }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (context.CommandId)
        {
            case RollbackCommandIds.OpenScript when context.Parameter is RollbackScriptArgs args:
                await OpenAsync(args.GroupId, cancellationToken).ConfigureAwait(true);
                break;
            case RollbackCommandIds.OpenScript:
                Tell("변경 일지에서 원복 스크립트가 있는 커밋 묶음을 고른 뒤 「원복 스크립트 열기」를 누르세요.", failure: true);
                break;
            case RollbackCommandIds.RunScript:
                if ((ActiveScript?.Invoke() ?? ActiveFromDocking()) is { } vm)
                {
                    await vm.RunAsync(cancellationToken).ConfigureAwait(true);
                }
                else
                {
                    Tell("열린 원복 스크립트 탭이 없습니다. 변경 일지에서 원복 스크립트를 먼저 여세요.", failure: true);
                }

                break;
        }
    }

    /// <summary>묶음의 원복 스크립트를 새 탭에 연다. 무효 묶음·파일 없음·키 없음은 이유만 알린다.</summary>
    internal async Task<RollbackScriptViewModel?> OpenAsync(long groupId, CancellationToken ct)
    {
        if (_services?.GetService(typeof(IRollbackStore)) is not IRollbackStore store)
        {
            Tell("원복 스크립트 저장소가 없습니다.", failure: true);
            return null;
        }

        RollbackGroup? group;
        RollbackScriptDocument? document;
        try
        {
            group = await store.GetAsync(groupId, ct).ConfigureAwait(true);
            if (group is null)
            {
                Tell($"묶음 #{groupId} 을(를) 찾지 못했습니다(보관 기간이 지나 지웠을 수 있습니다).", failure: true);
                return null;
            }

            if (group.State == RollbackScriptState.Invalid)
            {
                Tell("롤백한 묶음이라 원복 스크립트를 열 수 없습니다.", failure: true);
                return null;
            }

            if (group.State == RollbackScriptState.Open)
            {
                Tell("이 묶음은 아직 커밋하거나 롤백하지 않았습니다. 트랜잭션이 끝난 뒤 여세요.", failure: true);
                return null;
            }

            document = await store.LoadDocumentAsync(groupId, ct).ConfigureAwait(true);
        }
        catch (CryptographicException)
        {
            Tell("원복 스크립트를 열 수 없습니다: 키가 없거나 파일이 바뀌었습니다.", failure: true);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Tell("원복 스크립트를 읽지 못했습니다: " + ex.Message, failure: true);
            return null;
        }

        if (document is null)
        {
            Tell("이 묶음에는 원복 스크립트 파일이 없습니다.", failure: true);
            return null;
        }

        var settings = _services.GetService(typeof(Oranda.App.Shell.Settings.SettingsService)) as Oranda.App.Shell.Settings.SettingsService;
        var vm = new RollbackScriptViewModel(group, document, settings?.Current.Rollback.Normalized().RetentionDays ?? RollbackSettings.DefaultRetentionDays);
        vm.UseServices(_services);
        var opened = OpenDocument?.Invoke(vm)
            ?? (_services.GetService(typeof(IShellUi)) as IShellUi)?.Docking.OpenDocument(RollbackScriptViewModel.PanelId, vm, vm.Title) is not null;
        return opened ? vm : null;
    }

    private RollbackScriptViewModel? ActiveFromDocking() =>
        (_services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking.ActiveDocument?.Panel as RollbackScriptViewModel;

    private void Tell(string message, bool failure)
    {
        if (Notify is not null)
        {
            Notify(message, failure);
            return;
        }

        SaveFeedback.Show(new SaveNotice(message, failure ? SaveFeedbackKind.Failure : SaveFeedbackKind.Success));
    }
}
