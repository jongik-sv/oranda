using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.MyBatisWorkspace.Hooks;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 도구 막대 오른쪽 끝의 보기 전환 단추 하나(<see cref="IMyBatisStatementsViewMode"/> 하나당 하나).
/// 눌러 두면 그 모드의 View 가 트리 자리를 차지하고, 다시 누르면 파일 하나 보기로 돌아간다.
/// </summary>
public sealed partial class MyBatisStatementsModeButton : ObservableObject
{
    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private bool _isChecked;

    private Control? _view;

    internal MyBatisStatementsModeButton(IMyBatisStatementsViewMode mode, string title)
    {
        Mode = mode;
        _title = title;
        _isAvailable = mode.IsAvailable;
    }

    public IMyBatisStatementsViewMode Mode { get; }

    public string Id => Mode.Id;

    public string IconKey => Mode.IconKey;

    /// <summary>모드의 View. 처음 필요할 때 한 번 만들어 들고 있는다.</summary>
    internal Control View => _view ??= Mode.CreateView();

    /// <summary>눌림이 바뀌었다(ViewModel 이 다른 단추를 풀고 내용을 바꾼다).</summary>
    internal Action<MyBatisStatementsModeButton>? CheckedChangedCallback { get; set; }

    partial void OnIsCheckedChanged(bool value) => CheckedChangedCallback?.Invoke(this);
}
