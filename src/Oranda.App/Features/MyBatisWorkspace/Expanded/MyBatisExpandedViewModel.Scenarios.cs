using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.ViewModels;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Expanded;

// MyBatisExpandedViewModel 의 일부: 시나리오 콤보 동기화·추가/이름 바꾸기/복제/삭제 명령(MyBatisExpandedViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisExpandedViewModel
{
    // ---- 시나리오 ---------------------------------------------------------------------------

    private bool CanEdit => _eval is not null && StoreAvailable();

    // 저장소를 묻는 순간 계산기·저장소 어셈블리가 깨어난다. 활성 문장이 있을 때만 부른다.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool StoreAvailable() => _hub?.Store is not null;

    private void NotifyCommands()
    {
        AddCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>콤보 항목을 계산 상태의 시나리오에 맞춘다. 항목은 ID 로 이어 붙여(저장할 때마다 record 가 새로 만들어진다) 선택을 지킨다.</summary>
    private void SyncScenarios()
    {
        _syncing = true;
        try
        {
            var wanted = _eval?.Scenarios ?? [];
            for (var i = 0; i < wanted.Count; i++)
            {
                var scenario = wanted[i];
                if (i < _scenarios.Count && _scenarios[i].Id == scenario.Id)
                {
                    _scenarios[i].Update(scenario);
                    continue;
                }

                var at = IndexOfId(scenario.Id, i + 1);
                if (at >= 0)
                {
                    _scenarios.Move(at, i);
                    _scenarios[i].Update(scenario);
                }
                else
                {
                    _scenarios.Insert(i, new MyBatisScenarioItem(scenario));
                }
            }

            while (_scenarios.Count > wanted.Count)
            {
                _scenarios.RemoveAt(_scenarios.Count - 1);
            }

            var selectedId = _eval?.SelectedScenario?.Id;
            SelectedScenario = selectedId is null ? null : _scenarios.FirstOrDefault(s => s.Id == selectedId);
        }
        finally
        {
            _syncing = false;
        }
    }

    private int IndexOfId(long id, int from)
    {
        for (var i = from; i < _scenarios.Count; i++)
        {
            if (_scenarios[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void Pick(MyBatisScenarioItem item)
    {
        if (_eval is not { } eval)
        {
            return;
        }

        // 디바운스 중인 고침이 있으면 먼저 반영해 둔다(고른 시나리오를 바꾸면 예약이 버려진다).
        eval.Flush();
        if (eval.Scenarios.FirstOrDefault(s => s.Id == item.Id) is { } scenario)
        {
            eval.SelectScenario(scenario);
        }
    }

    private Task AddAsync() => RunAsync(async eval =>
    {
        eval.Flush();
        await eval.AddScenarioAsync(NewName(eval)).ConfigureAwait(true);
    });

    private async Task RenameAsync()
    {
        if (_eval?.SelectedScenario is not { } scenario || _hub is null)
        {
            return;
        }

        var title = UiText.Get("MyBatis.scenario.renameTitle", "시나리오 이름 바꾸기");
        var name = await _hub.Host.Dialogs.PromptAsync(title, scenario.Name, CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), scenario.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync(eval => eval.RenameScenarioAsync(scenario, name.Trim())).ConfigureAwait(true);
    }

    private Task DuplicateAsync() => RunAsync(async eval =>
    {
        if (eval.SelectedScenario is { } scenario)
        {
            await eval.DuplicateScenarioAsync(scenario, CopyName(eval, scenario.Name)).ConfigureAwait(true);
        }
    });

    private Task DeleteAsync() => RunAsync(async eval =>
    {
        if (eval.SelectedScenario is { } scenario)
        {
            await eval.DeleteScenarioAsync(scenario).ConfigureAwait(true);
        }
    });

    /// <summary>저장소 호출을 감싼다. 실패는 머리 줄 아래 한 줄로 알린다(저장소는 SQLite 라 잠금·디스크 오류가 날 수 있다).</summary>
    private async Task RunAsync(Func<MyBatisStatementEval, Task> action)
    {
        if (_eval is not { } eval)
        {
            return;
        }

        try
        {
            SetActionError(string.Empty);
            await action(eval).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetActionError(MyBatisText.Format("MyBatis.scenario.failed", "시나리오를 처리하지 못했습니다: {0}", e.Message));
        }
    }

    /// <summary>새 시나리오 이름: 「시나리오 n」(이미 있는 이름과 겹치지 않는 가장 작은 n, 개수 + 1 부터).</summary>
    private static string NewName(MyBatisStatementEval eval)
    {
        for (var n = eval.Scenarios.Count + 1; ; n++)
        {
            var name = MyBatisText.Format("MyBatis.scenario.newName", "시나리오 {0}", n);
            if (!eval.Scenarios.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            {
                return name;
            }
        }
    }

    /// <summary>복제 이름: 「원래 이름 복사」(이미 있으면 뒤에 번호).</summary>
    private static string CopyName(MyBatisStatementEval eval, string original)
    {
        var name = MyBatisText.Format("MyBatis.scenario.copyName", "{0} 복사", original);
        var candidate = name;
        for (var n = 2; eval.Scenarios.Any(s => string.Equals(s.Name, candidate, StringComparison.Ordinal)); n++)
        {
            candidate = name + " " + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }
}
