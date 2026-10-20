using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Language.Snippets;

namespace Oranda.App.Features.Templates;

/// <summary>
/// 사용자 조각 편집 대화상자(새로 만들기·고치기·복제). 줄임말·이름·설명·본문을 받고, 저장 전에 템플릿 창이 준 규칙으로 검사한다.
/// 저장 자체는 <see cref="TemplatesViewModel.CommitAsync"/> 가 한다.
/// </summary>
public sealed partial class SnippetEditViewModel : ObservableObject
{
    private readonly Func<SnippetEditViewModel, string?> _validate;
    private readonly Func<string, string?> _note;

    /// <param name="original">고치는 사용자 조각. 새로 만들거나 복제하면 null.</param>
    /// <param name="validate">오류 글(문제가 없으면 null).</param>
    /// <param name="note">줄임말에 대한 알림(기본 조각을 대신함 등, 없으면 null).</param>
    public SnippetEditViewModel(string title, Snippet? original, Func<SnippetEditViewModel, string?> validate, Func<string, string?> note)
    {
        Title = title;
        Original = original;
        _validate = validate;
        _note = note;
    }

    public string Title { get; }

    public Snippet? Original { get; }

    [ObservableProperty]
    public partial string Prefix { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    /// <summary>검사 오류(없으면 빈 글).</summary>
    [ObservableProperty]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>줄임말 알림(없으면 빈 글).</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    public bool HasNote => Note.Length > 0;

    partial void OnPrefixChanged(string value)
    {
        Note = _note(value.Trim()) ?? string.Empty;
        Error = string.Empty;
    }

    partial void OnBodyChanged(string value) => Error = string.Empty;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    /// <summary>규칙을 검사해 <see cref="Error"/> 를 채운다. 문제가 없으면 true.</summary>
    public bool Validate()
    {
        Error = _validate(this) ?? string.Empty;
        return Error.Length == 0;
    }

    /// <summary>입력한 값으로 만든 사용자 조각(이름이 비면 줄임말).</summary>
    public Snippet ToSnippet()
    {
        var prefix = Prefix.Trim();
        var name = Name.Trim();
        return new Snippet(prefix, name.Length > 0 ? name : prefix, Description.Trim(), Body, SnippetKind.Snippet, IsUser: true);
    }
}
