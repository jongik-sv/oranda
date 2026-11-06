using Avalonia.Threading;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 진행 보고를 화면 스레드에서 처리하는 <see cref="IProgress{T}"/>. 실행기가 다른 스레드에서 보고해도 바인딩된 속성은 화면 스레드에서만 바뀐다.
/// 화면 스레드이면 바로 처리한다.
/// </summary>
internal sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            handler(value);
        }
        else
        {
            Dispatcher.UIThread.Post(() => handler(value));
        }
    }
}
