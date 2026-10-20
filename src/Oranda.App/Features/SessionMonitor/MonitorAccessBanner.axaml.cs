using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>모니터 창 위에 얹는 권한 없음 안내 띠. 데이터 문맥 = <see cref="MonitorAccessState"/>. 「SQL 복사」는 여기서 처리하고 「다른 접속으로 열기…」는 창에 알린다.</summary>
public partial class MonitorAccessBanner : UserControl
{
    public MonitorAccessBanner()
    {
        InitializeComponent();
        OtherAccountButton.Click += (_, _) => OtherAccountRequested?.Invoke(this, EventArgs.Empty);
        CopyGrantButton.Click += async (_, _) =>
        {
            if (DataContext is MonitorAccessState { GrantSql: { } sql } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(sql);
            }
        };
    }

    /// <summary>「다른 접속으로 열기…」 단추를 눌렀다.</summary>
    public event EventHandler? OtherAccountRequested;
}
