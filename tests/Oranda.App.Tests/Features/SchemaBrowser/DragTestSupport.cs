using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>끌어 놓기 시작 실패 시험(스키마 브라우저·접속 관리자·템플릿)이 함께 쓰는 도구.</summary>
internal static class DragTestSupport
{
    /// <summary>Windows 에서 DoDragDrop 이 던진 오류(E_FAIL)와 같은 예외.</summary>
    public static COMException OleFailure() =>
#pragma warning disable CA2201 // 실제 플랫폼이 던지는 예외 형식을 그대로 흉내 낸다.
        new("Error HRESULT E_FAIL has been returned from a call to a COM component.", unchecked((int)0x80004005));
#pragma warning restore CA2201

    /// <summary>비동기로 이어지는 작업을 UI 스레드에서 잠깐 처리한다.</summary>
    public static async Task PumpAsync(int rounds = 30)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>처리되지 않은 예외를 모아 둔다(async void 밖으로 새면 여기로 온다).</summary>
    public sealed class UnhandledSpy : IDisposable
    {
        public UnhandledSpy() => Dispatcher.UIThread.UnhandledException += OnUnhandled;

        public Exception? Caught { get; private set; }

        private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Caught = e.Exception;
            e.Handled = true;
        }

        public void Dispose() => Dispatcher.UIThread.UnhandledException -= OnUnhandled;
    }
}
