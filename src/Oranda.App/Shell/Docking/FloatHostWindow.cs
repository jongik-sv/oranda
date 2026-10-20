using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 떠 있는 창. Dock 기본 <see cref="HostWindow"/> 는 메인 창을 소유자로 두어 늘 그 위에 붙으므로,
/// 소유자 없이 보통 창으로 띄우고 크기를 메인 창의 3분의 2 이하로 줄인다(위치는 Dock 이 끌어 낸 자리로 정한다)(<see cref="Present"/>, <see cref="IHostWindow"/> 다시 구현).
/// </summary>
public sealed class FloatHostWindow : HostWindow, IHostWindow
{
    protected override Type StyleKeyOverride => typeof(HostWindow);

    /// <summary>
    /// 떠 있는 창을 보이기 직전에 부르는 창구. 주 창이 macOS 시스템 메뉴를 이 창에도 붙이는 데 쓴다
    /// (떠 있는 창이 key 창일 때 시스템 막대가 앱 메뉴만 남지 않게). 주 창이 붙을 때 채운다.
    /// </summary>
    public static Action<Window>? Presenting { get; set; }

    public new void Present(bool isDialog)
    {
        var main = Window?.Factory?.DockControls.Select(c => TopLevel.GetTopLevel(c as Visual)).OfType<Window>().FirstOrDefault(w => w is not HostWindow);
        if (main is not null)
        {
            var width = Math.Min(Width, main.Bounds.Width * 2 / 3);
            var height = Math.Min(Height, main.Bounds.Height * 2 / 3);
            if (width > 0 && height > 0)
            {
                Width = width;
                Height = height;
            }
        }

        Topmost = false;
        if (isDialog && main is not null)
        {
            _ = ShowDialog(main); // 대화상자로 요청받은 경우(Dock 기본 동작)는 소유자를 둔다
            return;
        }

        Presenting?.Invoke(this);
        if (main is not null)
        {
            // 소유자가 없으니 메인 창이 닫힐 때 직접 닫아야 앱이 끝난다(레이아웃은 메인 창이 닫히기 전에 저장된다).
            main.Closed += (_, _) => Close();
        }

        Show();
    }
}
