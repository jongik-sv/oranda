using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Oranda.App.Shell;

/// <summary>화면 요소가 속한 창의 클립보드에 글자를 두는 공용 헬퍼.</summary>
internal static class ClipboardText
{
    /// <summary>
    /// 클립보드에 <paramref name="text"/> 를 둔다. 클립보드를 못 얻으면 false.
    /// 다른 프로그램이 클립보드를 쥐고 있을 때 등의 예외는 그대로 올린다(호출하는 쪽이 자기 규칙으로 잡는다).
    /// </summary>
    public static async Task<bool> SetAsync(Visual visual, string text)
    {
        ArgumentNullException.ThrowIfNull(visual);
        if (TopLevel.GetTopLevel(visual)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text);
        return true;
    }
}
