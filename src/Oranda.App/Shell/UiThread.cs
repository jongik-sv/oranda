using System;
using Avalonia;
using Avalonia.Threading;

namespace Oranda.App.Shell;

/// <summary>UI 스레드로 일을 넘기는 공용 헬퍼. 이미 UI 스레드면 바로 실행한다.</summary>
internal static class UiThread
{
    /// <summary>UI 스레드면 바로, 아니면 UI 스레드 큐에 올린다.</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>앱이 안 떠 있으면(시험·종료 중) 또는 UI 스레드면 바로, 아니면 UI 스레드 큐에 올린다.</summary>
    public static void PostIfAppRunning(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
