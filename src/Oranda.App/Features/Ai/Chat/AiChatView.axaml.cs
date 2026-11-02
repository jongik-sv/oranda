using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Oranda.App.Features.Ai.Chat;

public partial class AiChatView : UserControl
{
    private static readonly FilePickerFileType ImageFiles = new("이미지") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] };

    public AiChatView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);

        // 입력칸(TextBox)은 Ctrl/Cmd+V 를 자기가 처리해 이미지만 있는 클립보드는 삼킨다: 터널 단계에서 먼저 받아 이미지가 있는지 본다.
        DraftBox.AddHandler(KeyDownEvent, OnDraftKeyDown, RoutingStrategies.Tunnel);

        // 끌어 놓기: 입력칸은 글 끌어 놓기를 받으므로 터널 단계에서 이미지·파일일 때만 가로챈다(글 끌어 놓기는 그대로).
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Tunnel);
        AttachButton.Click += async (_, _) => await PickFilesAsync();
        AttachedToVisualTree += (_, _) => ConnectAi();
        DataContextChanged += (_, _) => ConnectAi();
    }

    /// <summary>
    /// 대화 창이 앱에 붙으면 AI 에 잇는다: 튜닝을 시작하기 전에도 입력칸이 열려 있고 보낸 글이 AI 로 간다.
    /// 앱 서비스가 없는 곳(모델만 만들어 쓰는 시험)에서는 아무것도 하지 않는다.
    /// </summary>
    private void ConnectAi()
    {
        if (Vm is { } vm && ServicesProvider() is { } services)
        {
            Oranda.App.Features.Ai.Tuning.AiTuningLauncher.For(services).EnsureChat(vm);
        }
    }

    /// <summary>대화 창을 AI 에 이을 때 쓰는 앱 서비스 모음을 찾는 쪽. 기본은 앱의 서비스이고 시험에서 바꾼다.</summary>
    internal static Func<IServiceProvider?> ServicesProvider { get; set; } = () => (Avalonia.Application.Current as App)?.Services;

    private AiChatViewModel? Vm => DataContext as AiChatViewModel;

    /// <summary>
    /// 붙여 넣기: 클립보드를 읽어 이미지만 있으면 첨부하고, 글이 있거나 이미지가 없으면 원래 붙여 넣기(<see cref="TextBox.Paste"/>)를 한다.
    /// 클립보드 읽기는 비동기라 키를 먼저 처리한 것으로 표시하고, 글 붙여 넣기는 읽은 뒤에 직접 실행한다.
    /// </summary>
    private async void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        // 플랫폼의 붙여 넣기 단축키(Windows Ctrl+V·Shift+Insert, macOS Cmd+V)만 가로챈다. 그 밖의 키 조합은 입력칸이 그대로 처리한다.
        var paste = this.GetPlatformSettings()?.HotkeyConfiguration.Paste.Any(g => g.Matches(e)) == true;
        if (!paste || Vm is not { CanType: true } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        e.Handled = true;
        try
        {
            var read = await AiImageSources.ReadClipboardAsync(clipboard, vm.RemainingSlots);
            if (read.PreferImages)
            {
                await vm.AddImagesAsync(read.Images);
                return;
            }
        }
#pragma warning disable CA1031 // 클립보드를 읽지 못하면 이미지가 없는 것으로 보고 글 붙여 넣기로 넘어간다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }

        DraftBox.Paste();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        try
        {
            if (AiImageSources.HasImage(e.DataTransfer))
            {
                e.DragEffects = Vm is { CanType: true } ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            }
        }
#pragma warning disable CA1031 // 플랫폼이 끌어 놓기 데이터를 못 읽어도 입력칸의 기본 처리로 넘긴다(동기 처리기라 예외를 내면 미처리 예외가 된다).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        bool hasImage;
        try
        {
            hasImage = AiImageSources.HasImage(e.DataTransfer);
        }
#pragma warning disable CA1031 // 위 DragOver 와 같다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return;
        }

        if (!hasImage || Vm is not { } vm)
        {
            return;
        }

        e.Handled = true;
        var images = await AiImageSources.ReadDroppedAsync(e.DataTransfer, vm.RemainingSlots);
        if (images.Count > 0)
        {
            await vm.AddImagesAsync(images);
        }
    }

    private async Task PickFilesAsync()
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "이미지 첨부", AllowMultiple = true, FileTypeFilter = [ImageFiles] });
            if (files.Count > 0)
            {
                await vm.AddImagesAsync(await AiImageSources.ReadFilesAsync(files, vm.RemainingSlots));
            }
        }
#pragma warning disable CA1031 // 단추 처리기(async void)라 예외를 밖으로 내면 앱 전역 미처리 예외가 된다: 알려 주고 끝낸다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            vm.AttachNotice = "이미지 파일을 읽지 못했습니다: " + ex.Message;
        }
    }
}
