using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Controls;
using Oranda.Core.Connection;

namespace Oranda.App.Features.LockMonitor;

/// <summary>
/// 세션 종료·연결 끊기 확인 창(시안 b 아래). 머리 띠 = 접속 환경 색, 본문 = 대상·사용자·트랜잭션, 대기자 경고(위험 띠), 보낼 문장.
/// 기본 단추는 취소(포커스·Enter·Esc), 실행 단추는 위험 글자색. 색은 테마 리소스만 쓴다.
/// </summary>
internal static class LockKillDialog
{
    public static async Task<bool> ShowAsync(Window owner, LockKillRequest request)
    {
        var accepted = false;
        var window = new Window
        {
            Title = request.ActionText + " 확인",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            MinWidth = 420,
            MaxWidth = 640,
            ShowInTaskbar = false,
        };
        window.Content = Build(request, out var cancel, out var run);
        run.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) => cancel.Focus();
        DialogShell.AutomationName(window, request.ActionText + " 확인");
        await DialogShell.ShowAsync(window, owner).ConfigureAwait(true);
        return accepted;
    }

    /// <summary>창 본문(시험이 그대로 그려 본다).</summary>
    internal static Control Build(LockKillRequest r, out Button cancel, out Button run)
    {
        // 머리 띠: 운영 빨강·테스트 주황·개발 초록 바탕 + 접속 글자.
        var header = new Border { Padding = new Thickness(16, 10) };
        var (bg, fg) = r.Environment switch
        {
            ConnectionEnvironment.Production => ("OrandaEnvProductionBrush", "OrandaEnvProductionForegroundBrush"),
            ConnectionEnvironment.Test => ("OrandaEnvTestBrush", "OrandaEnvProductionForegroundBrush"),
            _ => ("OrandaEnvDevelopmentBrush", "OrandaEnvProductionForegroundBrush"),
        };
        header.Bind(Border.BackgroundProperty, header.GetResourceObservable(bg));
        var title = new TextBlock { Text = r.HeaderTitle, FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { Text = r.HeaderDetail, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable(fg));
        detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable(fg));
        header.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, detail } };

        var body = new StackPanel { Margin = new Thickness(16, 14), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = r.Question, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });

        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 3 };
        for (var i = 0; i < r.Facts.Count; i++)
        {
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = r.Facts[i].Key, Classes = { "dim" } };
            var v = new TextBlock { Text = r.Facts[i].Value, TextWrapping = TextWrapping.Wrap };
            if (i == 0)
            {
                v.Classes.Add("mono");
            }

            Grid.SetRow(k, i);
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            facts.Children.Add(k);
            facts.Children.Add(v);
        }

        body.Children.Add(facts);

        if (r.WaiterHeadline is { } headline)
        {
            var callout = new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
            callout.Bind(Border.BackgroundProperty, callout.GetResourceObservable("OrandaDiffRemovedBrush"));
            callout.Bind(Border.BorderBrushProperty, callout.GetResourceObservable("OrandaDangerForegroundBrush"));
            Control icon = Shell.Icons.ShellIcons.Create("warn", 16) ?? new Panel();
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 8, 0);
            DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
            callout.Child = new DockPanel
            {
                Children =
                {
                    icon,
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = headline, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = r.WaiterLine, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "mono" } },
                        },
                    },
                },
            };
            DialogShell.AutomationName(callout, headline);
            body.Children.Add(callout);
        }

        body.Children.Add(new TextBlock { Text = r.Note, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "dim" }, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left });
        var sql = new SqlCodeView { Text = r.Statement, ShowLineNumbers = false, Height = 34, MinWidth = 420 };
        DialogShell.AutomationName(sql, "보낼 문장");
        body.Children.Add(new Border { Classes = { "code" }, Padding = new Thickness(2), Child = sql });

        cancel = new Button { Content = "취소", MinWidth = 80, IsDefault = true, IsCancel = true, Classes = { "accent" } };
        run = new Button { Content = r.ActionText, MinWidth = 80 };
        run.Bind(Button.ForegroundProperty, run.GetResourceObservable("OrandaDangerForegroundBrush"));
        DialogShell.AutomationName(run, r.ActionText + " 실행");
        var buttons = DialogShell.Buttons(cancel, run);
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        var footer = new Border { Padding = new Thickness(16, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.Bind(Border.BackgroundProperty, footer.GetResourceObservable("OrandaToolbarBackgroundBrush"));
        footer.Bind(Border.BorderBrushProperty, footer.GetResourceObservable("OrandaSeparatorBrush"));
        footer.Child = new DockPanel
        {
            Children =
            {
                buttons,
                new TextBlock
                {
                    Text = "이 명령은 DB 에 씁니다(세션 상태 바꿈).", FontSize = 12, Classes = { "dim" },
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0),
                },
            },
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        return root;
    }
}
