using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.FormatterProfiles;
using Oranda.Core.Formatting;
using Oranda.Core.Storage;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 설정 창 「SQL 포맷」 쪽(Orange Options &gt; Common &gt; SQL Formatter 대응, TSK-13-38): 키워드 맞춤·목록 쉼표·키워드 대소문자·
/// GROUP/ORDER BY 목록 4묶음. 값은 지금 쓰는 포매터 프로필(<c>formatterProfileId</c>)의 옵션에서 읽고, 설정 창 「저장」 때 그 프로필에 쓴다.
/// 기본 제공 프로필은 고칠 수 없으므로 바뀐 값이 있으면 사용자 프로필(<see cref="FormatterIds.SettingsCopy"/>)로 복사해 저장하고
/// 그 프로필을 기본으로 바꾼다. 「닫기」는 아무것도 쓰지 않는다. 나머지 세부 옵션은 포매터 프로필 화면(「자세히…」)이 맡는다.
/// </summary>
public sealed partial class SqlFormatPage : ObservableObject
{
    /// <summary>미리보기 SQL(짧은 조회 1개: 목록·JOIN·조건·GROUP BY·ORDER BY 가 다 보인다).</summary>
    public const string PreviewSql =
        "select e.id, e.name, count(*) cnt from emp e join dept d on d.id = e.dept_id "
        + "where e.sal > 100 and d.loc = 'SEOUL' group by e.id, e.name order by e.name, e.id";

    private readonly IFormatterProfileStore? _store;
    private readonly ISqlFormatter? _formatter;
    private readonly IShellText _text;
    private FormatterProfile? _profile;
    private bool _touched;

    [NotifyPropertyChangedFor(nameof(AlignLeft), nameof(AlignRight), nameof(Preview))]
    [ObservableProperty]
    private KeywordAlign _keywordAlign;

    [NotifyPropertyChangedFor(nameof(CommaTrailing), nameof(CommaLeading), nameof(Preview))]
    [ObservableProperty]
    private CommaPosition _comma;

    [NotifyPropertyChangedFor(nameof(CaseAsIs), nameof(CaseUpper), nameof(CaseLower), nameof(Preview))]
    [ObservableProperty]
    private LetterCase _keywordCase;

    [NotifyPropertyChangedFor(nameof(ByListSingle), nameof(ByListMulti), nameof(Preview))]
    [ObservableProperty]
    private ByListLayout _byList;

    [NotifyPropertyChangedFor(nameof(ProfileText))]
    [ObservableProperty]
    private string _profileName = string.Empty;

    /// <param name="store">프로필 저장소(null = 앱 밖 시험·조립 전: 기본값만 보이고 저장하지 않는다)</param>
    /// <param name="formatter">미리보기 포매터(null = 미리보기 없음)</param>
    /// <param name="profileId">설정의 기본 프로필 ID</param>
    /// <param name="text">화면 글자(복사본 이름·프로필 줄)</param>
    public SqlFormatPage(IFormatterProfileStore? store, ISqlFormatter? formatter, string profileId, IShellText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _store = store;
        _formatter = formatter;
        _text = text;
        ProfileId = profileId;
        Show(new FormatterOptions());
        Loaded = LoadAsync(profileId);
    }

    /// <summary>프로필 읽기가 끝나면 끝나는 작업(시험·저장이 기다린다).</summary>
    public Task Loaded { get; }

    /// <summary>읽은 프로필 ID(기본 프로필이 없으면 첫 프로필).</summary>
    public string ProfileId { get; private set; }

    /// <summary>「저장」을 누르면 저장한다(저장소가 있을 때).</summary>
    public bool CanSave => _store is not null;

    /// <summary>「프로필: 이름」. 기본 제공 프로필이면 저장할 때 복사본을 만든다는 말을 덧붙인다.</summary>
    public string ProfileText => string.IsNullOrEmpty(ProfileName)
        ? string.Empty
        : _text.Lookup("Shell.settings.fmt.profile").Replace("{0}", ProfileName, StringComparison.Ordinal)
            + (FormatterIds.IsBuiltIn(ProfileId) ? " · " + _text.Lookup("Shell.settings.fmt.builtInNote") : string.Empty);

    /// <summary>저장하지 못한 이유(없으면 빈 글).</summary>
    [ObservableProperty]
    private string _errorText = string.Empty;

    public bool AlignLeft
    {
        get => KeywordAlign == KeywordAlign.Left;
        set => Pick(value, () => KeywordAlign = KeywordAlign.Left);
    }

    public bool AlignRight
    {
        get => KeywordAlign == KeywordAlign.Right;
        set => Pick(value, () => KeywordAlign = KeywordAlign.Right);
    }

    public bool CommaTrailing
    {
        get => Comma == CommaPosition.Trailing;
        set => Pick(value, () => Comma = CommaPosition.Trailing);
    }

    public bool CommaLeading
    {
        get => Comma == CommaPosition.Leading;
        set => Pick(value, () => Comma = CommaPosition.Leading);
    }

    public bool CaseAsIs
    {
        get => KeywordCase == LetterCase.AsIs;
        set => Pick(value, () => KeywordCase = LetterCase.AsIs);
    }

    public bool CaseUpper
    {
        get => KeywordCase == LetterCase.Upper;
        set => Pick(value, () => KeywordCase = LetterCase.Upper);
    }

    public bool CaseLower
    {
        get => KeywordCase == LetterCase.Lower;
        set => Pick(value, () => KeywordCase = LetterCase.Lower);
    }

    public bool ByListSingle
    {
        get => ByList == ByListLayout.SingleLine;
        set => Pick(value, () => ByList = ByListLayout.SingleLine);
    }

    public bool ByListMulti
    {
        get => ByList == ByListLayout.MultiLine;
        set => Pick(value, () => ByList = ByListLayout.MultiLine);
    }

    public bool HasPreview => _formatter is not null;

    /// <summary>지금 고른 값으로 <see cref="PreviewSql"/> 을 포맷한 글. 실패하면 원문.</summary>
    public string Preview
    {
        get
        {
            if (_formatter is null)
            {
                return string.Empty;
            }

            var r = _formatter.Format(PreviewSql, Compose());
            return r.Text;
        }
    }

    /// <summary>읽은 프로필 옵션에 지금 고른 4항목을 얹은 값.</summary>
    public FormatterOptions Compose()
    {
        var o = _profile?.Options ?? new FormatterOptions();
        return o with
        {
            KeywordAlign = KeywordAlign,
            CommaPosition = Comma,
            Case = o.Case with { Keyword = KeywordCase },
            LineBreaks = o.LineBreaks with { GroupOrderBy = ByList },
        };
    }

    /// <summary>Orange 「Default」: 4항목만 기본값(<see cref="FormatterOptions"/> 기본 = Oranda 기본 프로필)으로. 저장은 설정 창 「저장」.</summary>
    [RelayCommand]
    public void ResetDefaults()
    {
        _touched = true;
        var d = new FormatterOptions();
        KeywordAlign = d.KeywordAlign;
        Comma = d.CommaPosition;
        KeywordCase = d.Case.Keyword;
        ByList = d.LineBreaks.GroupOrderBy;
    }

    /// <summary>
    /// 바뀐 값이 있으면 프로필에 쓴다. 기본 제공 프로필이면 사용자 프로필로 복사해 쓰고 그 ID 를 돌려준다(설정의 기본 프로필을 그것으로 바꾼다).
    /// 바꿀 것이 없거나 저장소가 없으면 null.
    /// </summary>
    public async Task<string?> SaveAsync(CancellationToken ct = default)
    {
        await Loaded.ConfigureAwait(true);
        if (_store is null)
        {
            return null;
        }

        var before = _profile?.Options ?? new FormatterOptions();
        var after = Compose();
        if (after == before)
        {
            return null;
        }

        if (_profile is { } p && !FormatterIds.IsBuiltIn(p.Id))
        {
            await _store.SaveAsync(p with { Options = after }, ct).ConfigureAwait(true);
            _profile = p with { Options = after };
            return p.Id == ProfileId ? null : p.Id;
        }

        // 이미 있는 사용자 프로필(예: 전에 만든 복사본을 「자세히…」에서 다듬어 둔 것)은 덮어쓰지 않는다: 비어 있는 ID·이름을 고른다.
        var existing = await _store.ListAsync(ct).ConfigureAwait(true);
        var baseName = _text.Lookup("Shell.settings.fmt.copyName");
        string id = FormatterIds.SettingsCopy, name = baseName;
        for (var n = 2; existing.Any(x => x.Id == id || string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
        {
            id = FormatterIds.SettingsCopy + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            name = baseName + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var copy = new FormatterProfile { Id = id, Name = name, Options = after };
        await _store.SaveAsync(copy, ct).ConfigureAwait(true);
        _profile = copy;
        ProfileId = copy.Id;
        ProfileName = copy.Name;
        OnPropertyChanged(nameof(ProfileText));
        return copy.Id;
    }

    private async Task LoadAsync(string profileId)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var all = await _store.ListAsync(CancellationToken.None).ConfigureAwait(true);
            _profile = all.FirstOrDefault(x => x.Id == profileId) ?? (all.Count > 0 ? all[0] : null);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _profile = null; // 읽지 못하면 기본값을 보이고, 저장하면 복사본을 만든다.
        }

        if (_profile is { } p)
        {
            ProfileId = p.Id;
            ProfileName = p.Name;
            OnPropertyChanged(nameof(ProfileText));
            if (!_touched)
            {
                Show(p.Options); // 읽는 동안 사용자가 고른 값은 덮지 않는다.
            }
        }
    }

    private void Show(FormatterOptions o)
    {
        KeywordAlign = o.KeywordAlign;
        Comma = o.CommaPosition;
        KeywordCase = o.Case.Keyword;
        ByList = o.LineBreaks.GroupOrderBy;
    }

    // 라디오 단추 묶음: 켜진 단추만 값을 바꾼다(같은 묶음의 다른 단추가 꺼질 때 오는 false 는 무시).
    private void Pick(bool value, Action set)
    {
        if (value)
        {
            _touched = true;
            set();
        }
    }
}
