using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Formatting;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

// ConvertDialogViewModel.cs 에서 나눈 형: 「바꿀 탭」 표의 한 줄 모델(동작 불변).
/// <summary>「바꿀 탭」 표의 한 줄. 포함·문장 id·종류를 고치면 부모가 겹침과 미리보기를 다시 계산한다.</summary>
public sealed partial class ConvertLineViewModel : ObservableObject
{
    /// <summary>종류 콤보에 보이는 글(<see cref="ConvertStatementKind"/> 순서 그대로).</summary>
    public static IReadOnlyList<string> KindLabels { get; } = ["select", "insert", "update", "delete", "CALLABLE"];

    private bool _include;
    private IReadOnlyList<ConvertNote> _convertNotes = [];
    private string _noteText;
    private string _suggestedId;

    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private int _kindIndex;

    [ObservableProperty]
    private string? _idError;

    internal ConvertLineViewModel(ConvertCandidate candidate)
    {
        Candidate = candidate;
        _id = candidate.SuggestedId;
        _suggestedId = candidate.SuggestedId;
        _kindIndex = candidate.Kind is { } k ? (int)k : -1;
        _include = candidate.Kind is not null && candidate.IncludeByDefault;
        _noteText = BuildNoteText(candidate, []);
    }

    public ConvertCandidate Candidate { get; }

    /// <summary>바꿀 줄에 넣는가. 종류 없는 줄은 켜지지 않는다.</summary>
    public bool Include
    {
        get => _include;
        set
        {
            if (SetProperty(ref _include, value && Kind is not null))
            {
                OnPropertyChanged(nameof(IsDimmed));
            }
        }
    }

    public string TabTitle => Candidate.TabTitle;

    /// <summary>종류가 없는 줄(바꿀 수 없는 문장)은 고를 수 없고 포함도 못 한다.</summary>
    public bool CanPickKind => Candidate.Kind is not null;

    public ConvertStatementKind? Kind => KindIndex < 0 ? null : (ConvertStatementKind)KindIndex;

    /// <summary>확인 열 글: 분석 단계 알림에 바꾸기 단계 알림(<see cref="SetConvertNotes"/>)을 합친 것.</summary>
    public string NoteText
    {
        get => _noteText;
        private set => SetProperty(ref _noteText, value);
    }

    public bool HasWarning => Candidate.Notes.Count > 0 || _convertNotes.Count > 0;

    public bool HasIdError => IdError is not null;

    /// <summary>제외한 줄은 확인 글을 흐리게 보인다.</summary>
    public bool IsDimmed => !Include;

    /// <summary>
    /// 이 줄을 바꾼 결과의 알림(<see cref="ConvertedStatement.Notes"/>)을 확인 열에 합친다. 포함하지 않은 줄·바꾸지 못한 줄은 빈 목록을 넘긴다.
    /// </summary>
    internal void SetConvertNotes(IReadOnlyList<ConvertNote> notes)
    {
        if (_convertNotes.SequenceEqual(notes))
        {
            return;
        }

        _convertNotes = notes;
        NoteText = BuildNoteText(Candidate, notes);
        OnPropertyChanged(nameof(HasWarning));
    }

    /// <summary>
    /// 새 제안 id 를 적용한다. 사용자가 손으로 고친 줄(현재 id 가 이전 제안과 다른 줄)은 id 를 그대로 둔다.
    /// </summary>
    internal void ApplySuggestedId(string suggested)
    {
        var untouched = Id == _suggestedId;
        _suggestedId = suggested;
        if (untouched)
        {
            Id = suggested;
        }
    }

    partial void OnKindIndexChanged(int value) => OnPropertyChanged(nameof(Kind));

    partial void OnIdErrorChanged(string? value) => OnPropertyChanged(nameof(HasIdError));

    private static string BuildNoteText(ConvertCandidate c, IReadOnlyList<ConvertNote> convertNotes)
    {
        var bind = ConvertText.Format("MbConvert.note.bind", "바인드 {0}", c.BindCount);
        var notes = c.Notes.Concat(convertNotes).Distinct().ToList();
        if (notes.Count == 0)
        {
            return bind + " · " + UiText.Get("MbConvert.note.ok", "문제 없음");
        }

        return bind + " · " + string.Join(" · ", notes.Select(NoteLabel));
    }

    /// <summary>알림 하나의 화면 글.</summary>
    internal static string NoteLabel(ConvertNote n)
    {
        var name = n.Name ?? string.Empty;
        return n.Kind switch
        {
            ConvertNoteKind.Substitution => ConvertText.Format("MbConvert.note.substitution", "치환 {0} 값 검증 필요", name),
            ConvertNoteKind.PositionalBind => ConvertText.Format("MbConvert.note.positional", "위치 바인드를 {0} 로 바꿈", name),
            ConvertNoteKind.RefCursorOut => ConvertText.Format("MbConvert.note.refCursor", "REF CURSOR OUT {0} 은 바꾸지 못함", name),
            ConvertNoteKind.WhereNotWrapped => UiText.Get("MbConvert.note.whereNotWrapped", "where 로 감싸지 못함"),
            ConvertNoteKind.PlSqlBlock => UiText.Get("MbConvert.note.plsql", "PL/SQL 블록이라 뺌 (종류를 CALLABLE 로 고르면 포함)"),
            ConvertNoteKind.Unsupported => UiText.Get("MbConvert.note.unsupported", "바꿀 수 없는 문장"),
            ConvertNoteKind.FormatSkipped => UiText.Get("MbConvert.note.formatSkipped", "서식을 적용하지 못함"),
            _ => string.Empty,
        };
    }
}
