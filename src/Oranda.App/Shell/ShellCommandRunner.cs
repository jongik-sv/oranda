using System;
using System.Threading.Tasks;

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
}
