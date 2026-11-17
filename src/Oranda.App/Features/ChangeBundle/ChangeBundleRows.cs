using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>
/// 반영 묶음 표의 한 줄(설계 9.2): 체크, #, 문장 한 줄 요약, 출처, 개발·테스트·운영(환경별 마지막 성공 날짜). 체크를 바꾸면 <see cref="CheckChanged"/> 로 알린다
/// (ViewModel 이 저장소에 쓴다). 체크 해제 줄은 흐리게(<see cref="IsDimmed"/>).
/// </summary>
public sealed partial class ChangeBundleRow : ObservableObject
{
    /// <summary>요약 칸 최대 글자 수(넘으면 「…」).</summary>
    internal const int MaxSummaryChars = 160;

    private bool _suppress;

    public ChangeBundleRow(ChangeBundleItem item, int number, ChangeBundleRunMarks marks)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(marks);
        Item = item;
        Number = number;
        Marks = marks;
        _isChecked = item.Checked;
    }

    public ChangeBundleItem Item { get; }

    public long Id => Item.Id;

    /// <summary>화면 순번(1부터).</summary>
    public int Number { get; }

    public ChangeBundleRunMarks Marks { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimmed))]
    private bool _isChecked;

    /// <summary>체크를 사용자가 바꿨다(저장소에 쓴다). 화면이 다시 읽어 넣을 때는 내지 않는다.</summary>
    internal event Action<ChangeBundleRow, bool>? CheckChanged;

    public bool IsDimmed => !IsChecked;

    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>문장 첫 줄(공백 정리, 길면 자름).</summary>
    public string SummaryText => Summarize(Item.Text);

    public string FullText => Item.Text;

    public string SourceText => SourceName(Item.Source);

    public string DevelopmentText => DateText(Marks.Development);

    public string TestText => DateText(Marks.Test);

    public string ProductionText => DateText(Marks.Production);

    public bool HasWarning => Item.HasWarning;

    /// <summary>⚠ 툴팁(갈래 문구 + 만든 쪽이 준 문구). 없으면 빈 글.</summary>
    public string WarningText => CautionText(Item);

    /// <summary>요약 칸 툴팁: 전문(길면 자름) + ⚠.</summary>
    public string ToolTipText => HasWarning ? FullTextPreview + Environment.NewLine + Environment.NewLine + "⚠ " + WarningText : FullTextPreview;

    private string FullTextPreview => Item.Text.Length > 2000 ? Item.Text[..2000] + "…" : Item.Text;

    /// <summary>다시 읽은 값을 알림 없이 넣는다.</summary>
    internal void SetCheckedSilently(bool value)
    {
        _suppress = true;
        try
        {
            IsChecked = value;
        }
        finally
        {
            _suppress = false;
        }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_suppress)
        {
            CheckChanged?.Invoke(this, value);
        }
    }

    internal static string Summarize(string text)
    {
        var flat = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        return flat.Length > MaxSummaryChars ? flat[..MaxSummaryChars] + "…" : flat;
    }

    internal static string SourceName(ChangeBundleItemSource source) => source switch
    {
        ChangeBundleItemSource.TableEditor => "테이블 편집",
        ChangeBundleItemSource.SqlEditor => "SQL 편집기",
        _ => "직접 넣음",
    };

    /// <summary>실행 표시 칸: 마지막 성공 날짜(지역 시간 「MM-dd」, 올해가 아니면 「yyyy-MM-dd」). 없으면 빈 글.</summary>
    internal static string DateText(DateTimeOffset? at)
    {
        if (at is not { } value)
        {
            return string.Empty;
        }

        var local = value.ToLocalTime();
        return "✔ " + local.ToString(local.Year == DateTimeOffset.Now.Year ? "MM-dd" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    internal static string CautionText(ChangeBundleItem item)
    {
        var parts = new List<string>();
        if (item.Cautions.HasFlag(ChangeBundleCautions.OwnerNotQualified))
        {
            parts.Add("문장 모양을 몰라 소유자를 붙이지 못하고 원문 그대로 넣었다.");
        }

        if (item.Cautions.HasFlag(ChangeBundleCautions.SecretRedacted))
        {
            parts.Add("비밀 값을 가려 저장했다(***). 실행·저장 전에 다시 적어야 한다.");
        }

        if (item.Warning is { Length: > 0 } warning)
        {
            parts.Add(warning);
        }

        return string.Join(Environment.NewLine, parts);
    }
}

/// <summary>
/// 「소유자 바꾸기」 칸 한 줄(설계 9.4, TE-22): 묶음의 대상 소유자 → 저장(실행)할 이름. 기본은 같은 이름.
/// 따옴표 없는 일반 이름은 대문자로 맞춘다(이름 규칙). 빈 칸은 바꾸지 않음.
/// </summary>
public sealed partial class OwnerMapRow : ObservableObject
{
    public OwnerMapRow(string from)
    {
        From = from;
        _to = from;
    }

    public string From { get; }

    [ObservableProperty]
    private string _to;

    /// <summary>바꿀 이름(이름 규칙대로). 같거나 비었으면 null.</summary>
    internal string? Target
    {
        get
        {
            var to = (To ?? string.Empty).Trim();
            if (to.Length == 0 || string.Equals(to, From, StringComparison.Ordinal))
            {
                return null; // 기본값 그대로(따옴표로 만든 소문자 소유자도 바꾸지 않는다)
            }

            if (Regex.IsMatch(to, @"^[A-Za-z][A-Za-z0-9_$#]*$"))
            {
                to = to.ToUpperInvariant();
            }

            return string.Equals(to, From, StringComparison.Ordinal) ? null : to;
        }
    }

    /// <summary>바꿀 줄만 모은 옛 소유자 → 새 소유자.</summary>
    internal static IReadOnlyDictionary<string, string> ToMap(IEnumerable<OwnerMapRow> rows)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Target is { } target)
            {
                map[row.From] = target;
            }
        }

        return map;
    }

    /// <summary>묶음의 대상 소유자와 문장의 대상 소유자를 합친 목록(대상 소유자 먼저, 중복 없음).</summary>
    internal static List<OwnerMapRow> For(ChangeBundleInfo bundle, IEnumerable<ChangeBundleItem> items)
    {
        var owners = new List<string>();
        foreach (var owner in bundle.TargetOwners.Concat(items.Select(i => i.TargetOwner)))
        {
            if (owner is { Length: > 0 } && !owners.Contains(owner, StringComparer.Ordinal))
            {
                owners.Add(owner);
            }
        }

        return [.. owners.Select(o => new OwnerMapRow(o))];
    }
}

/// <summary>「실행…」 창의 접속 한 줄(지금 열린 접속). DBMS 가 묶음과 다르면 고를 수 없다(TE-23).</summary>
public sealed record ChangeBundleConnection(ISession Session, bool IsEnabled, string? Tooltip)
{
    public string Display => $"{Session.Profile.Name} ({EnvironmentName(Session.Profile.Environment)}, {Session.Server.Dbms})";

    internal static string EnvironmentName(ConnectionEnvironment environment) => environment switch
    {
        ConnectionEnvironment.Development => "개발",
        ConnectionEnvironment.Test => "테스트",
        _ => "운영",
    };
}
