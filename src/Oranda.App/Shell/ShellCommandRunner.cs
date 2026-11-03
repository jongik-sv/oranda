using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// DI 없이 만들어지는 화면(ViewLocator)이 셸 명령을 ID 로 실행하는 창구. 주 창이 붙을 때 <see cref="Runner"/> 를 채운다.
/// 화면 단추가 명령과 같은 길(활성 조건·후속 동작 포함)로 가게 할 때 쓴다.
/// </summary>
public static class ShellCommandRunner
{
    /// <summary>명령 ID → 실행. 주 창이 설정한다(시험은 직접 넣는다).</summary>
    public static Func<string, Task>? Runner { get; set; }

    public static Task RunAsync(string commandId) => Runner?.Invoke(commandId) ?? Task.CompletedTask;

    /// <summary>
    /// 레지스트리로 실행하는 <see cref="Runner"/> 를 만든다. 키맵·메뉴와 같이 거부와 예외를 기록에 남긴다:
    /// 단추는 결과를 받을 곳이 없어 거부가 조용히 사라지고, 예외는 async void 처리기를 넘어 앱까지 간다.
    /// </summary>
    public static Func<string, Task> Bind(ICommandRegistry registry, IContextKeys keys) => async id =>
    {
        try
        {
            if (!await registry.ExecuteAsync(id, keys).ConfigureAwait(true))
            {
                Trace.TraceWarning("화면 단추의 명령 '{0}' 을(를) 실행하지 않았다(등록되지 않았거나 지금은 비활성).", id);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("명령 '{0}' 실행 중 예외: {1}", id, ex);
        }
    };
}
