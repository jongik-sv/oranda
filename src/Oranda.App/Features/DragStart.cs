using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Input;

namespace Oranda.App.Features;

/// <summary>
/// 끌어 놓기 시작(<see cref="DragDrop.DoDragDropAsync"/>)을 부르고, 실패해도 앱이 죽지 않게 한다(TSK-07-05).
/// Windows 에서는 OLE <c>DoDragDrop</c> 이 HRESULT 0x80004005 로 실패하며 <c>COMException</c> 이 나는데,
/// 끌기 시작은 <c>async void</c> 이벤트 처리기에서 부르므로 잡지 않으면 처리되지 않은 예외로 앱이 종료된다.
/// </summary>
internal static class DragStart
{
    /// <summary>제품 기본값. 시험은 같은 모양의 대리자로 바꿔 실패를 흉내 낸다.</summary>
    public static Task<DragDropEffects> Default(PointerPressedEventArgs trigger, IDataTransfer data, DragDropEffects effects) =>
        DragDrop.DoDragDropAsync(trigger, data, effects);

    /// <summary>
    /// <paramref name="starter"/> 를 <b>동기로</b> 불러 끌기를 시작하고, 끝날 때까지 기다린다(첫 <c>await</c> 앞에서 이미 호출이 끝난다).
    /// 실패는 모두 잡아 로그에 남기고 <paramref name="report"/> 로 안내한다. 취소는 조용히 넘어간다.
    /// </summary>
    public static async Task RunAsync(
        Func<PointerPressedEventArgs, IDataTransfer, DragDropEffects, Task<DragDropEffects>> starter,
        PointerPressedEventArgs trigger,
        IDataTransfer data,
        DragDropEffects effects,
        Action<string> report)
    {
        try
        {
            await starter(trigger, data, effects).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail(ex, report);
        }
    }

    /// <summary>끌기 시작 실패를 로그(예외 형식·HResult)에 남기고 상태줄에 이유를 알린다. 안내 자체가 실패해도 밖으로 던지지 않는다.</summary>
    public static void Fail(Exception ex, Action<string> report)
    {
        Trace.TraceError("끌어 놓기 시작 실패 ({0}, HResult 0x{1:X8}): {2}", ex.GetType().Name, ex.HResult, ex);
        try
        {
            report("끌어 놓기를 시작하지 못했습니다: " + ex.Message);
        }
        catch (Exception reportEx)
        {
            Trace.TraceError("끌어 놓기 실패 안내 중 예외: {0}", reportEx);
        }
    }
}
