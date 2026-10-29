using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;

namespace Oranda.App.Shell.Controls;

/// <summary>유형 체크 목록의 한 줄(유형 이름 + 체크).</summary>
public sealed partial class TypeChoice : ObservableObject
{
    private readonly TypeChoiceSet _owner;

    internal TypeChoice(TypeChoiceSet owner, string type)
    {
        _owner = owner;
        Type = type;
    }

    public string Type { get; }

    [ObservableProperty]
    private bool _isChecked = true;

    partial void OnIsCheckedChanged(bool value) => _owner.OnItemChanged();
}

/// <summary>
/// 객체 유형 다중 선택(스키마 브라우저·객체 찾기의 「모든 유형」 드롭다운, TSK-14-25, D-056).
/// 맨 위 「전체 선택」 은 세 상태(전부 true · 일부 null · 없음 false). 전부 또는 아무것도 안 고르면 거르지 않는다
/// (<see cref="SelectedTypes"/> 가 빈 목록, 닫힌 글 「모든 유형」) — 빈 트리로 끝나지 않게.
/// </summary>
public sealed partial class TypeChoiceSet : ObservableObject
{
    private int _batch;
    private bool _pending;

    public TypeChoiceSet(IEnumerable<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        Items = [.. types.Distinct(StringComparer.Ordinal).Select(t => new TypeChoice(this, t))];
    }

    /// <summary>유형 줄(목록 순서 그대로).</summary>
    public IReadOnlyList<TypeChoice> Items { get; }

    /// <summary>체크가 바뀌었다(한 번의 묶음 바꾸기는 한 번만 알린다).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 「전체 선택」: 전부 true · 일부 null · 없음 false. 일부일 때 누르면(체크 상자는 false 를 준다) 전부 고른다(Windows 탐색기 관례).
    /// </summary>
    public bool? AllChecked
    {
        get
        {
            var n = Items.Count(i => i.IsChecked);
            return n == Items.Count ? true : n == 0 ? false : null;
        }
        set
        {
            var current = AllChecked;
            if (value is null)
            {
                OnPropertyChanged(); // 사용자가 세 번째 상태로 둘 수는 없다: 지금 상태로 되돌린다
                return;
            }

            SetAll(current is null || value.Value);
        }
    }

    /// <summary>거를 유형. 전부 또는 아무것도 안 고르면 빈 목록(= 모든 유형).</summary>
    public IReadOnlyList<string> SelectedTypes
    {
        get
        {
            var picked = Items.Where(i => i.IsChecked).Select(i => i.Type).ToList();
            return picked.Count == 0 || picked.Count == Items.Count ? [] : picked;
        }
    }

    /// <summary>모든 유형을 보이는가(거르지 않음).</summary>
    public bool IsAll => SelectedTypes.Count == 0;

    /// <summary>닫힌 드롭다운 글: 「모든 유형」 · 「TABLE」 · 「TABLE, VIEW」 · 「TABLE, VIEW 외 2」.</summary>
    public string Summary
    {
        get
        {
            var picked = SelectedTypes;
            return picked.Count switch
            {
                0 => UiText.Get("TypeChoice.all", "모든 유형"),
                <= 2 => string.Join(", ", picked),
                _ => UiText.Format("TypeChoice.more", "{0} 외 {1}", string.Join(", ", picked.Take(2)), picked.Count - 2),
            };
        }
    }

    /// <summary>모두 고르거나 모두 푼다.</summary>
    public void SetAll(bool on) => Batch(() =>
    {
        foreach (var i in Items)
        {
            i.IsChecked = on;
        }
    });

    /// <summary>모든 유형으로 되돌린다(필터 지우기).</summary>
    public void Reset() => SetAll(true);

    /// <summary>
    /// 주어진 유형만 고른다(목록에 없는 이름은 버린다). 빈 목록이나 null 이면 모든 유형.
    /// </summary>
    public void SelectOnly(IEnumerable<string>? types)
    {
        var set = types is null ? [] : new HashSet<string>(types, StringComparer.Ordinal);
        set.IntersectWith(Items.Select(i => i.Type));
        Batch(() =>
        {
            foreach (var i in Items)
            {
                i.IsChecked = set.Count == 0 || set.Contains(i.Type);
            }
        });
    }

    /// <summary>알림 없이 고른다(저장해 둔 선택을 되살릴 때).</summary>
    public void Restore(IEnumerable<string>? types)
    {
        var handlers = Changed;
        Changed = null;
        try
        {
            SelectOnly(types);
        }
        finally
        {
            Changed = handlers;
        }
    }

    internal void OnItemChanged()
    {
        if (_batch > 0)
        {
            _pending = true;
            return;
        }

        Raise();
    }

    private void Batch(Action change)
    {
        _batch++;
        try
        {
            change();
        }
        finally
        {
            _batch--;
        }

        if (_batch == 0 && _pending)
        {
            _pending = false;
            Raise();
        }
        else if (_batch == 0)
        {
            OnPropertyChanged(nameof(AllChecked)); // 바뀐 것이 없어도 체크 상자를 지금 상태로 맞춘다
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(SelectedTypes));
        OnPropertyChanged(nameof(IsAll));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
