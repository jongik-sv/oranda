using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Menus;

/// <summary>메뉴 항목·툴바 단추가 쓰는 <see cref="ICommand"/>. 실행은 <see cref="ICommandRegistry.ExecuteAsync"/> 한 길로 간다.</summary>
public sealed class RegistryCommand : ICommand
{
    private readonly string _id;
    private readonly ICommandRegistry _registry;
    private readonly IContextKeys _keys;

    public RegistryCommand(string commandId, ICommandRegistry registry, IContextKeys keys)
    {
        _id = commandId;
        _registry = registry;
        _keys = keys;
    }

    public event EventHandler? CanExecuteChanged;

    public string CommandId => _id;

    /// <summary>마지막으로 시작한 실행(시험이 끝을 기다릴 때 쓴다).</summary>
    public Task? LastRun { get; private set; }

    public bool CanExecute(object? parameter) => _registry.CanExecute(_id, _keys);

    public void Execute(object? parameter) => LastRun = RunAsync(parameter);

    /// <summary>문맥 키가 바뀌었을 때 호출해 활성 상태를 다시 계산하게 한다.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync(object? parameter)
    {
        try
        {
            await _registry.ExecuteAsync(_id, _keys, parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace.TraceError("명령 '{0}' 실행 중 예외: {1}", _id, ex);
        }
    }
}
