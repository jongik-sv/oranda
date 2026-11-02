using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>
/// LLM 프로필 관리 창. API 키 입력 칸은 바인딩하지 않고(키가 화면 바인딩·로그에 남지 않게) 모델이 저장·연결 테스트 때 <see cref="AiProfileManagerViewModel.EnteredKeyProvider"/> 로 읽는다.
/// 창을 닫으면 도는 서버 조회를 멈춘다.
/// </summary>
public partial class AiProfileManagerWindow : Window
{
    private const char MaskChar = '●';
    private AiProfileManagerViewModel? _model;
    private bool _closeApproved;

    public AiProfileManagerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Closing += OnClosing;
        Closed += (_, _) => _model?.Dispose();
        KeyBox.TextChanged += (_, _) => _model?.NotifyKeyTyped(!string.IsNullOrEmpty(KeyBox.Text));
    }

    /// <summary><paramref name="owner"/> 위에 모달로 띄운다. 창이 닫히면 돌아온다.</summary>
    public static Task ShowAsync(Window owner, AiProfileManagerViewModel model)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(model);
        return new AiProfileManagerWindow { DataContext = model }.ShowDialog(owner);
    }

    private void Attach()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.EnteredKeyProvider = null;
            _model.EnteredKeyConsumed -= OnKeyConsumed;
            _model.CloseRequested -= OnCloseRequested;
        }

        _model = DataContext as AiProfileManagerViewModel;
        if (_model is null)
        {
            return;
        }

        _model.EnteredKeyProvider = () => KeyBox.Text;
        _model.EnteredKeyConsumed += OnKeyConsumed;
        _model.CloseRequested += OnCloseRequested;
        _model.PropertyChanged += OnModelPropertyChanged;
        KeyBox.PasswordChar = _model.IsKeyVisible ? '\0' : MaskChar;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiProfileManagerViewModel.IsKeyVisible) && _model is not null)
        {
            KeyBox.PasswordChar = _model.IsKeyVisible ? '\0' : MaskChar;
        }
        else if (e.PropertyName == nameof(AiProfileManagerViewModel.EditingId))
        {
            KeyBox.Text = string.Empty; // 다른 프로필로 옮기면 앞 프로필에 입력하던 키가 따라가지 않는다
        }
    }

    private void OnKeyConsumed(object? sender, EventArgs e) => KeyBox.Text = string.Empty;

    // 취소 단추는 이미 버림 확인을 거쳤다: 제목 줄 X 로 닫을 때만 저장하지 않은 변경을 다시 묻는다.
    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _closeApproved = true;
        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || _model is null || !_model.IsDirty)
        {
            return;
        }

        e.Cancel = true;
        if (await _model.ConfirmDiscardAsync().ConfigureAwait(true))
        {
            _closeApproved = true;
            Close();
        }
    }

    private async void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AiProfileListItem item } && _model is not null)
        {
            await _model.SelectProfileAsync(item.Id).ConfigureAwait(true);
        }
    }

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AiPresetCard card } && _model is not null)
        {
            _model.SelectPreset(card.Preset);
        }
    }
}
