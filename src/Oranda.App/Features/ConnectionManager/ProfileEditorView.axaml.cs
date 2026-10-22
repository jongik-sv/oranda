using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>프로필 편집기 화면(접속 관리자 오른쪽). 모델은 <see cref="ProfileEditorViewModel"/>.</summary>
public partial class ProfileEditorView : UserControl
{
    /// <summary>스펙트럼을 모델 색에 맞추는 중(이때 생기는 색 바뀜은 모델에 되쓰지 않는다).</summary>
    private bool _syncingSpectrum;

    private ProfileEditorViewModel? _model;

    public ProfileEditorView()
    {
        InitializeComponent();
        PickFolder = PickWithStorageAsync;
        TnsAdminBrowseButton.Click += async (_, _) =>
        {
            if (DataContext is ProfileEditorViewModel vm && await PickFolder("TNS 디렉터리", vm.TnsAdmin) is { } path)
            {
                vm.TnsAdmin = path;
            }
        };
        WalletBrowseButton.Click += async (_, _) =>
        {
            if (DataContext is ProfileEditorViewModel vm && await PickFolder("지갑 폴더", vm.WalletLocation) is { } path)
            {
                vm.WalletLocation = path;
            }
        };

        // 라벨 색: 견본·기본 색은 고르고 닫는다. 스펙트럼은 끄는 동안 바로 넣는다(빨강 판정은 저장 때와 같다).
        ColorPresetList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { DataContext: LabelColorPreset preset })
            {
                Model?.PickColor(preset.Hex);
                ColorButton.Flyout?.Hide();
            }
        });
        ColorDefaultButton.Click += (_, _) =>
        {
            Model?.PickColor(null);
            ColorButton.Flyout?.Hide();
        };
        ColorSpectrum.ColorChanged += (_, e) =>
        {
            if (!_syncingSpectrum)
            {
                Model?.PickColor(LabelColorRules.ToHex(e.NewColor.R, e.NewColor.G, e.NewColor.B));
            }
        };
    }

    private ProfileEditorViewModel? Model => DataContext as ProfileEditorViewModel;

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = Model;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }

        SyncSpectrum();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileEditorViewModel.EffectiveColor))
        {
            SyncSpectrum();
        }
    }

    /// <summary>스펙트럼에 지금 라벨 색을 보인다(같은 색이면 건드리지 않는다).</summary>
    private void SyncSpectrum()
    {
        if (_model is null || !LabelColorRules.TryParse(_model.EffectiveColor, out var r, out var g, out var b))
        {
            return;
        }

        var color = Color.FromRgb(r, g, b);
        if (ColorSpectrum.Color == color)
        {
            return;
        }

        _syncingSpectrum = true;
        try
        {
            ColorSpectrum.Color = color;
        }
        finally
        {
            _syncingSpectrum = false;
        }
    }

    /// <summary>폴더 선택 창(제목, 지금 칸 값 → 고른 폴더 경로, 취소면 null). 시험에서 바꿔 끼운다.</summary>
    public Func<string, string, Task<string?>> PickFolder { get; set; }

    private async Task<string?> PickWithStorageAsync(string title, string current)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current.Trim()))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(current.Trim());
        }

        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
