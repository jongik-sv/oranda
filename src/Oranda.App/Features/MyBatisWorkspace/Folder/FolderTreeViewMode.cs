using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>
/// 「문장」 창의 폴더 트리 모드(<see cref="IMyBatisStatementsViewMode"/>, design.md 9장 R5). 매퍼 폴더가 열려 있을 때만 쓸 수 있고,
/// 「문장」 창은 이 모드가 쓸 수 있게 바뀌면(폴더를 열면) 이 모드를 켠다.
/// </summary>
[OrandaService(typeof(IMyBatisStatementsViewMode))]
public sealed class FolderTreeViewMode : IMyBatisStatementsViewMode
{
    private readonly MapperFolderService _folder;
    private EventHandler? _availabilityChanged;

    // 구독자에게 알린 값. 「문장」 창은 단추를 만들 때 IsAvailable 을 읽고 「쓸 수 있게 바뀜」 알림으로 모드를 켠다.
    // 폴더를 연 뒤에야 창이 구독하면(폴더 열기가 창을 띄우고 창은 다음 그리기에 붙는다) 알린 값을 되돌려 두고 다음 차례에 다시 알린다:
    // 새 구독자는 「쓸 수 없음」으로 단추를 만들었다가 바뀜을 받아 모드를 켜고, 먼저 구독한 쪽은 같은 값을 다시 받을 뿐이다.
    private bool _announced;

    public FolderTreeViewMode(MapperFolderService folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        _folder = folder;
        _folder.Changed += (_, _) => Announce();
    }

    public string Id => "folder";

    public string Title => "매퍼 폴더 트리";

    public string IconKey => "folder";

    public bool IsAvailable => _announced;

    public event EventHandler? AvailabilityChanged
    {
        add
        {
            _availabilityChanged += value;
            if (_folder.IsOpen)
            {
                _announced = false;
                Dispatcher.UIThread.Post(Announce); // 구독한 쪽이 단추를 다 만든 뒤에 알린다
            }
        }

        remove => _availabilityChanged -= value;
    }

    public Control CreateView() => new MapperFolderTreeView { DataContext = new MapperFolderTreeViewModel(_folder) };

    private void Announce()
    {
        if (_announced != _folder.IsOpen)
        {
            _announced = _folder.IsOpen;
            _availabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
