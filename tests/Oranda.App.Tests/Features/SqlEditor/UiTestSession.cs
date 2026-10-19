using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>
/// 편집기 시험 본문을 셸 시험과 같은 헤드리스 UI 스레드(<see cref="HeadlessHost"/>)에서 돌린다.
/// 한 시험 프로세스에는 Avalonia 앱 세션이 하나만 있을 수 있어 따로 만들지 않는다.
/// AvaloniaEdit 의 <c>TextDocument</c> 는 만든 스레드에서만 쓸 수 있어, 비동기 시험도 같은 스레드로 돌아와야 한다.
/// </summary>
public static class Ui
{
    public static Task Run(Func<Task> body) => HeadlessHost.Run(body);

    public static Task Run(Action body) => HeadlessHost.Run(body);
}
