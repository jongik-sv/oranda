using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Oranda.App.Shell.Icons;
using Oranda.Core.Language;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.Completion;

/// <summary>자동완성 목록 한 줄(AvaloniaEdit <see cref="ICompletionData"/>). 거르기는 <see cref="Text"/>(이름), 넣기는 <see cref="CompletionItem.InsertText"/>.</summary>
public sealed class CompletionData(CompletionItem item) : ICompletionData
{
    public CompletionItem Item { get; } = item;

    private Control? _content;

    public IImage? Image => null;

    public string Text => Item.Label;

    /// <summary>
    /// 목록 한 줄: 종류 아이콘 + 이름 + 흐린 글씨의 상세(열 형식·객체 종류). SQL Developer·Orange 의 완성 목록처럼
    /// 이름 열 폭을 맞춰 상세가 한 줄로 늘어서게 한다. 줄마다 한 번만 만든다.
    /// </summary>
    public object Content => _content ??= BuildContent();

    /// <summary>옆 설명 창. 상세는 줄에 이미 보이므로 줄에서 잘릴 만큼 길 때만 띄운다.</summary>
    public object? Description => Item.Detail is { Length: > DetailFitsChars } d ? KindText(Item.Kind) + "\n" + d : null;

    /// <summary>목록 폭(360)에서 상세 칸에 잘리지 않고 들어가는 글자 수(대략).</summary>
    private const int DetailFitsChars = 22;

    /// <summary>DB 객체가 키워드보다 앞(같은 접두어일 때 먼저 고른다).</summary>
    public double Priority => Item.Kind switch
    {
        CompletionItemKind.Column => 5,
        CompletionItemKind.Table or CompletionItemKind.View or CompletionItemKind.Synonym => 4,
        CompletionItemKind.Procedure or CompletionItemKind.Function or CompletionItemKind.Package => 3,
        CompletionItemKind.Schema => 2,
        _ => 1,
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        ArgumentNullException.ThrowIfNull(textArea);
        ArgumentNullException.ThrowIfNull(completionSegment);
        textArea.Document.Replace(completionSegment, Item.InsertText);
    }

    /// <summary>종류별 아이콘 키(스키마 브라우저와 같은 그림). 키워드는 아이콘 없이 자리만 둔다.</summary>
    public static string? IconKey(CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Schema => "schemaUser",
        CompletionItemKind.Table => "table",
        CompletionItemKind.View => "objView",
        CompletionItemKind.Synonym => "objSynonym",
        CompletionItemKind.Column => "column",
        CompletionItemKind.Package => "objPackage",
        CompletionItemKind.Procedure => "objProcedure",
        CompletionItemKind.Function => "objFunction",
        CompletionItemKind.Hint => "info",
        CompletionItemKind.Snippet => "fileSql",
        _ => null,
    };

    private DockPanel BuildContent()
    {
        const double iconSize = 14;
        var icon = (Control?)ShellIcons.Create(IconKey(Item.Kind), iconSize) ?? new Border { Width = iconSize, Height = iconSize };
        var name = new TextBlock { Text = Item.Label, MinWidth = 150, Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock
        {
            Text = Item.Detail is { Length: > 0 } d && Item.Kind != CompletionItemKind.Schema ? d : KindText(Item.Kind),
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var row = new DockPanel { Children = { icon, name, detail } };
        DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(name, Avalonia.Controls.Dock.Left);
        return row;
    }

    public static string KindText(CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Keyword => "키워드",
        CompletionItemKind.Schema => "스키마",
        CompletionItemKind.Table => "테이블",
        CompletionItemKind.View => "뷰",
        CompletionItemKind.Synonym => "시노님",
        CompletionItemKind.Column => "열",
        CompletionItemKind.Package => "패키지",
        CompletionItemKind.Procedure => "프로시저",
        CompletionItemKind.Function => "함수",
        CompletionItemKind.Hint => "힌트",
        CompletionItemKind.Snippet => "코드 조각",
        _ => kind.ToString(),
    };
}

/// <summary>인자 도움말 창의 오버로드 목록(AvaloniaEdit <see cref="IOverloadProvider"/>). 지금 인자를 머리말에 표시한다.</summary>
public sealed class SignatureOverloads : IOverloadProvider
{
    private readonly IReadOnlyList<ProcedureInfo> _signatures;
    private readonly int _argumentIndex;
    private int _selected;

    public SignatureOverloads(IReadOnlyList<ProcedureInfo> signatures, int argumentIndex)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        _signatures = signatures;
        _argumentIndex = argumentIndex;

        // 인자 수가 지금 위치를 담는 첫 오버로드를 고른다.
        for (int i = 0; i < signatures.Count; i++)
        {
            if (signatures[i].Arguments.Count(a => !string.IsNullOrEmpty(a.Name)) > argumentIndex)
            {
                _selected = i;
                break;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (_selected == value || value < 0 || value >= _signatures.Count)
            {
                return;
            }

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentIndexText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentContent)));
        }
    }

    public int Count => _signatures.Count;

    public string CurrentIndexText => string.Create(CultureInfo.InvariantCulture, $"{_selected + 1}/{_signatures.Count}");

    /// <summary>시그니처 한 줄(<c>NAME(P1 IN NUMBER, …)</c>).</summary>
    public object CurrentHeader => global::Oranda.Language.Completion.ParameterHints.Format(_signatures[_selected]);

    /// <summary>지금 인자 설명(없으면 빈 글).</summary>
    public object CurrentContent
    {
        get
        {
            var args = _signatures[_selected].Arguments.Where(a => !string.IsNullOrEmpty(a.Name)).OrderBy(a => a.Position).ToList();
            if (_argumentIndex < 0 || _argumentIndex >= args.Count)
            {
                return string.Empty;
            }

            var a = args[_argumentIndex];
            return $"{a.Name}: {a.Direction} {a.TypeName ?? a.DataType}{(a.HasDefault ? " (기본값 있음)" : string.Empty)}";
        }
    }
}
