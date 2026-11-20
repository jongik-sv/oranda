using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Oranda.App.Features.Update;

/// <summary>릴리스 노트 줄을 글자 상자의 Run 으로 그린다(릴리스 노트 창·업데이트 창 공용). 조각마다 Run 하나, 줄 사이는 LineBreak.</summary>
internal static class ReleaseNotesRenderer
{
    private const string CodeFontKey = "OrandaCodeFontFamily";
    private const string CodeFontFallback = "Cascadia Mono,Consolas,Menlo,monospace";

    public static void Fill(SelectableTextBlock box, IEnumerable<NoteLine> lines)
    {
        box.Inlines ??= new InlineCollection();
        box.Inlines.Clear();
        FontFamily? code = null;
        var first = true;
        foreach (var line in lines)
        {
            if (!first)
            {
                box.Inlines.Add(new LineBreak());
            }

            first = false;
            foreach (var span in line.Spans)
            {
                var run = new Run(span.Text) { FontWeight = span.Bold ? FontWeight.Bold : FontWeight.Normal };
                if (span.Code)
                {
                    code ??= CodeFont(box);
                    run.FontFamily = code;
                }

                box.Inlines.Add(run);
            }
        }
    }

    /// <summary>앱의 코드 글꼴 리소스. 상자가 아직 창에 붙지 않았으면 앱 리소스에서, 그것도 없으면 기본 고정폭.</summary>
    private static FontFamily CodeFont(Control box)
    {
        if (box.TryFindResource(CodeFontKey, out var found) && found is FontFamily family)
        {
            return family;
        }

        if (Avalonia.Application.Current?.TryFindResource(CodeFontKey, out var appFound) == true && appFound is FontFamily appFamily)
        {
            return appFamily;
        }

        return new FontFamily(CodeFontFallback);
    }
}
