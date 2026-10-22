using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionImport;

/// <summary>접속 고르기 창의 한 줄(체크 + 이름·폴더·user@접속 + 표시 글).</summary>
public sealed partial class ConnectionPickItem : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public ConnectionPickItem(ConnectionProfile profile, bool exists = false, string? problem = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
        Exists = exists;
        CanCheck = problem is null;
        Note = problem ?? (exists ? UiText.Get("Connection.transfer.exists", "이미 있음") : string.Empty);
    }

    public ConnectionProfile Profile { get; }

    public string Name => Profile.Name;

    /// <summary>폴더 경로(없으면 빈 글자).</summary>
    public string Folder => Profile.Group ?? string.Empty;

    public string Detail => $"{Profile.User}@{Profile.DataSource}";

    /// <summary>환경 라벨 색(목록 견본).</summary>
    public string EnvironmentColor => LabelColorRules.Effective(Profile.Environment, Profile.Color);

    /// <summary>같은 접속(이름·사용자·접속 문자열)이 이미 있다(가져오기).</summary>
    public bool Exists { get; }

    /// <summary>저장할 수 없는 값이면 false(체크할 수 없다).</summary>
    public bool CanCheck { get; }

    /// <summary>오른쪽 표시 글: 「이미 있음」 또는 검증 문제. 없으면 빈 글자.</summary>
    public string Note { get; }

    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// 접속 내보내기·가져오기에서 접속을 체크해 고르는 창의 모델(TSK-13-13). 내보내기는 모두 체크로 시작하고,
/// 가져오기는 이미 있는 접속·저장할 수 없는 접속을 끈 채로 시작한다.
/// </summary>
public sealed partial class ConnectionPickViewModel : ViewModelBase
{
    public ConnectionPickViewModel(string title, string caption, string confirmText, IEnumerable<ConnectionPickItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Title = title;
        Caption = caption;
        ConfirmText = confirmText;
        Items = [.. items];
        foreach (var item in Items)
        {
            item.PropertyChanged += OnItemChanged;
        }
    }

    public string Title { get; }

    public string Caption { get; }

    public string ConfirmText { get; }

    public ObservableCollection<ConnectionPickItem> Items { get; }

    public int CheckedCount => Items.Count(i => i.IsChecked);

    public bool CanConfirm => CheckedCount > 0;

    /// <summary>「n개 고름 / 전체 m개」.</summary>
    public string CountText => UiText.Get("Connection.transfer.count", "{0}개 고름 / 전체 {1}개")
        .Replace("{0}", CheckedCount.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
        .Replace("{1}", Items.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);

    /// <summary>체크한 접속(목록 순서).</summary>
    public IReadOnlyList<ConnectionProfile> Chosen => [.. Items.Where(i => i.IsChecked).Select(i => i.Profile)];

    /// <summary>내보내기: 저장된 순서 그대로, 모두 체크.</summary>
    public static ConnectionPickViewModel ForExport(IReadOnlyList<ConnectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return new ConnectionPickViewModel(
            UiText.Get("Connection.transfer.exportTitle", "접속 내보내기"),
            UiText.Get("Connection.transfer.exportCaption", "내보낼 접속을 고르세요. 비밀번호·저장된 자격 증명은 파일에 넣지 않습니다(가져간 곳에서 처음 접속할 때 묻습니다)."),
            UiText.Get("Connection.transfer.exportConfirm", "내보내기…"),
            profiles.Select(p => new ConnectionPickItem(p) { IsChecked = true }));
    }

    /// <summary>가져오기: 이미 있는 접속은 「이미 있음」 으로 끄고, 저장할 수 없는 접속은 문제를 보이고 못 고르게 한다.</summary>
    public static ConnectionPickViewModel ForImport(IReadOnlyList<ConnectionProfile> incoming, IReadOnlyList<ConnectionProfile> existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(existing);
        var items = incoming.Select(p =>
        {
            var problem = ConnectionProfileValidator.Validate(p) is [var first, ..] ? first : null;
            var exists = existing.Any(e => ConnectionExport.SameConnection(e, p));
            return new ConnectionPickItem(p, exists, problem) { IsChecked = problem is null && !exists };
        });
        return new ConnectionPickViewModel(
            UiText.Get("Connection.transfer.importTitle", "접속 가져오기"),
            UiText.Get("Connection.transfer.importCaption", "가져올 접속을 고르세요. 이미 있는 접속(이름·사용자·접속 문자열이 같음)은 꺼 두었습니다. 비밀번호는 처음 접속할 때 묻습니다."),
            UiText.Get("Connection.transfer.importConfirm", "가져오기"),
            items);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items.Where(i => i.CanCheck))
        {
            item.IsChecked = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items)
        {
            item.IsChecked = false;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionPickItem.IsChecked))
        {
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(CountText));
        }
    }
}
