using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>
/// 조합 검사·자동 고침 명령(docs/oranda1/design.md 4.1, TSK-03-04). 둘 다 활성 MyBatis 작업 공간의 고른 문장을 대상으로 「조합 검사」 창을 연다.
/// 「조합 검사」 = 검사 시작. 「자동 고침」 = 검사 뒤 첫 고침을 미리보기로 연다(적용은 창의 「고치기」 단추, 미리보기 뒤 적용).
/// 실행 계획에 쓸 접속은 활성 탭의 세션(<see cref="ExecutionCommands.ActiveTabSession"/>)이다.
/// </summary>
[OrandaCommand("oranda.mybatis.checkCombinations", CommandCategory.Sql, Icon = "compile", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["toolbar:mybatis/combos@30", "menu:sql/mybatisCombos@25"])]
[OrandaCommand("oranda.mybatis.applyAutoFix", CommandCategory.Edit, Icon = "docEdit", Enablement = "mybatisDocActive && mybatisStatementSelected && !readOnlyDoc", Placements = ["context:mybatisEditor/autoFix@10"])]
public sealed class MyBatisComboCommands : ICommandHandler
{
    internal const string PanelId = "oranda.panel.mybatisCombos";

    private readonly ServiceContainer? _container;

    // 매개변수 없는 생성자는 시작 시험용(OutlineViewModel 주석 참고). 셸은 등록된 ServiceContainer 를 받는 생성자를 쓴다.
    public MyBatisComboCommands()
    {
    }

    public MyBatisComboCommands(ServiceContainer container)
    {
        _container = container;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (SqlEditorHost.Current.ActiveWorkspace?.MyBatis is not { SelectedStatementId: { } id } session)
        {
            return;
        }

        _container?.GetService<IShellUi>()?.Docking.ShowTool(PanelId);
        if (await FindPanelAsync().ConfigureAwait(true) is not { } vm)
        {
            return;
        }

        if (_container is { } container)
        {
            vm.SessionSource = () => ExecutionCommands.ActiveTabSession(container);
        }

        await vm.CheckAsync(session, id, cancellationToken).ConfigureAwait(true);
        if (context.CommandId == "oranda.mybatis.applyAutoFix")
        {
            if (vm.Fixes.Count == 0)
            {
                vm.StatusText = ComboText.NoFix;
                return;
            }

            await vm.PreviewAsync(vm.Fixes[0]).ConfigureAwait(true);
        }
    }

    // 창은 처음 보일 때 만들어진다(ShowTool 직후에는 아직 없을 수 있다).
    private static async Task<MyBatisCombosViewModel?> FindPanelAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            if (MyBatisCombosViewModel.Current is { } vm)
            {
                return vm;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return MyBatisCombosViewModel.Current;
    }
}
