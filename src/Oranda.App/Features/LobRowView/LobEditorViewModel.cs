using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.LobRowView;

/// <summary>LOB 편집기에 넘기는 값 한 개. <see cref="Lob"/> 이 null 이면 <see cref="PlainText"/>(LOB 가 아닌 셀 값)를 읽기 전용으로 보인다.</summary>
/// <param name="Target">저장할 곳. null 이면 저장 불가(이유는 <paramref name="SaveBlockedReason"/>)</param>
public sealed record LobEditorSource(string Title, ISession? Session, LobValue? Lob, string? PlainText, LobSaveTarget? Target, string? SaveBlockedReason);

/// <summary>
/// <c>oranda.panel.lobEditor</c> — LOB 편집기(GRID-07, TSK-06-05). CLOB·NCLOB 은 텍스트(편집·저장), BLOB 은 16진·이미지 미리보기.
/// 큰 LOB 은 스트림으로 읽는다: 편집기는 앞부분(<see cref="LobContent.MaxEditChars"/>)만 올리고 파일로 저장할 때 전체를 흘려 보낸다.
/// DB 저장은 <see cref="LobWriter"/>(키 규칙·정책 관문)로, 읽기 전용 프로필에서는 막는다.
/// 서비스는 처음 쓸 때 꺼낸다(시작 경로에서 Oracle 어셈블리를 올리지 않기 위해).
/// </summary>
[OrandaToolPanel("oranda.panel.lobEditor", ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class LobEditorViewModel : ToolPanelViewModelBase
{
    private readonly IServiceProvider? _services;
    private ILobAccessor? _accessor;
    private LobWriter? _writer;
    private LobEditorSource? _source;
    private bool _loading;
    private byte[]? _pendingBytes;

    public LobEditorViewModel()
    {
    }

    public LobEditorViewModel(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 서비스를 직접 준다.</summary>
    internal LobEditorViewModel(ILobAccessor accessor, LobWriter writer)
    {
        _accessor = accessor;
        _writer = writer;
    }

    /// <summary>CLOB·NCLOB 이면 true(텍스트 보기).</summary>
    [ObservableProperty]
    private bool _isText;

    /// <summary>BLOB·BFILE 이면 true(16진·이미지 보기).</summary>
    [ObservableProperty]
    private bool _isBinary;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _infoText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>CLOB 본문(앞 <see cref="LobContent.MaxEditChars"/> 문자까지).</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _hexText = string.Empty;

    /// <summary>이미지로 알아본 BLOB 의 원시 바이트(화면이 그림으로 바꾼다). 아니면 null.</summary>
    [ObservableProperty]
    private byte[]? _imageBytes;

    /// <summary>앞부분만 올렸다(전체는 파일 저장으로만). 본문 편집을 막는다.</summary>
    [ObservableProperty]
    private bool _isPartial;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>본문을 고칠 수 있다(LOB 저장 대상이 있고, 쓰기 가능한 접속이고, 전체를 올렸을 때).</summary>
    [ObservableProperty]
    private bool _canEditText;

    /// <summary>DB 저장 단추 활성(바뀐 내용 + 저장 대상 + 쓰기 가능).</summary>
    [ObservableProperty]
    private bool _canSaveToDatabase;

    [ObservableProperty]
    private string _fileEncodingName = LobContent.EncodingNames[0];

    /// <summary>저장 대화상자(제안 파일 이름 → 고른 경로, 취소면 null). 화면이 채운다.</summary>
    public Func<string, CancellationToken, Task<string?>>? PickSaveFile { get; set; }

    /// <summary>불러오기 대화상자(→ 고른 경로, 취소면 null). 화면이 채운다.</summary>
    public Func<CancellationToken, Task<string?>>? PickOpenFile { get; set; }

    public LobEditorSource? Source => _source;


    /// <summary>LOB 편집기에 값을 넣고 내용을 읽는다.</summary>
    public async Task AttachAsync(LobEditorSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _pendingBytes = null;
        Title = source.Title;
        await LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>다시 읽는다(바뀐 내용은 버린다).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var src = _source;
        if (src is null)
        {
            return;
        }

        IsBusy = true;
        _loading = true;
        try
        {
            _pendingBytes = null;
            IsDirty = false;
            Text = string.Empty;
            HexText = string.Empty;
            ImageBytes = null;
            IsPartial = false;
            if (src.Lob is not { } lob)
            {
                IsText = true;
                IsBinary = false;
                Text = src.PlainText ?? string.Empty;
                InfoText = "LOB 가 아닌 값(읽기 전용)";
                StatusText = string.Empty;
                return;
            }

            IsText = lob.Kind is LobKind.Clob or LobKind.NClob;
            IsBinary = !IsText;
            InfoText = Describe(lob);
            if (IsText)
            {
                await LoadTextAsync(src, lob, ct).ConfigureAwait(true);
            }
            else
            {
                await LoadBinaryAsync(src, lob, ct).ConfigureAwait(true);
            }

            StatusText = IsPartial
                ? $"앞 {LobContent.MaxEditChars:N0} 문자만 보입니다. 전체는 「파일로 저장」으로 받으세요."
                : string.Empty;
        }
        catch (OperationCanceledException)
        {
            StatusText = "취소했습니다.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            StatusText = "LOB 를 읽지 못했습니다: " + ex.Message;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
            UpdateFlags();
        }
    }

    partial void OnTextChanged(string value)
    {
        if (!_loading)
        {
            IsDirty = true;
            UpdateFlags();
        }
    }

    /// <summary>LOB 내용을 파일로 저장한다. 전체를 스트림으로 흘리므로 메모리에 올리지 않는다.</summary>
    public async Task SaveToFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_source is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            long written;
            if (IsText)
            {
                var enc = LobContent.GetEncoding(FileEncodingName);
                if (IsDirty && !IsPartial)
                {
                    // 고친 내용이 있으면 화면의 글을 저장한다.
                    var bytes = enc.GetPreamble().Concat(enc.GetBytes(Text)).ToArray();
                    await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(true);
                    written = bytes.Length;
                }
                else
                {
                    await using var stream = await OpenStreamAsync(ct).ConfigureAwait(true);
                    written = await LobContent.SaveTextFileAsync(stream, path, enc, ct).ConfigureAwait(true);
                }
            }
            else
            {
                if (_pendingBytes is { } pending)
                {
                    await File.WriteAllBytesAsync(path, pending, ct).ConfigureAwait(true);
                    written = pending.Length;
                }
                else
                {
                    await using var stream = await OpenStreamAsync(ct).ConfigureAwait(true);
                    written = await LobContent.CopyToFileAsync(stream, path, ct).ConfigureAwait(true);
                }
            }

            StatusText = $"파일로 저장했습니다: {path} ({written:N0} {(IsText ? "문자" : "B")})";
        }
        catch (OperationCanceledException)
        {
            StatusText = "저장을 취소했습니다.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusText = "파일로 저장하지 못했습니다: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>파일 내용을 편집기에 불러온다(DB 에는 「DB 에 저장」 을 눌러야 들어간다).</summary>
    public async Task LoadFromFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_source?.Lob is null)
        {
            return;
        }

        IsBusy = true;
        _loading = true;
        try
        {
            if (IsText)
            {
                var text = await LobContent.ReadTextFileAsync(path, ct).ConfigureAwait(true);
                Text = text;
                IsPartial = false;
                StatusText = $"파일을 불러왔습니다: {path} ({text.Length:N0} 문자). 저장하려면 「DB 에 저장」.";
            }
            else
            {
                var bytes = await LobContent.ReadBinaryFileAsync(path, ct).ConfigureAwait(true);
                _pendingBytes = bytes;
                ShowBytes(bytes);
                StatusText = $"파일을 불러왔습니다: {path} ({bytes.Length:N0} B). 저장하려면 「DB 에 저장」.";
            }

            IsDirty = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "불러오기를 취소했습니다.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusText = "파일을 불러오지 못했습니다: " + ex.Message;
        }
        finally
        {
            _loading = false;
            IsBusy = false;
            UpdateFlags();
        }
    }

    /// <summary>바뀐 LOB 를 DB 에 저장한다(영향 1 행 확인, 아니면 롤백). 읽기 전용 접속이면 막는다.</summary>
    public async Task<LobWriteResult> SaveToDatabaseAsync(bool commit, CancellationToken ct = default)
    {
        var src = _source;
        if (src?.Lob is not { } lob)
        {
            return Report(new LobWriteResult(false, "LOB 가 아닌 값은 저장할 수 없습니다."));
        }

        if (src.Session is not { } session || src.Target is not { } target)
        {
            return Report(new LobWriteResult(false, src.SaveBlockedReason ?? "저장할 대상 행을 알 수 없습니다."));
        }

        if (session.Profile.ReadOnly)
        {
            return Report(new LobWriteResult(false, $"읽기 전용 접속 '{session.Profile.Name}' 에서는 LOB 를 저장할 수 없습니다."));
        }

        if (!IsDirty)
        {
            return Report(new LobWriteResult(false, "바뀐 내용이 없습니다."));
        }

        BindValue value;
        if (IsText)
        {
            if (IsPartial)
            {
                return Report(new LobWriteResult(false, "앞부분만 올려서 저장할 수 없습니다. 파일을 불러와 바꾸세요."));
            }

            value = new BindValue(BindDataType.Clob, Text);
        }
        else if (_pendingBytes is { } bytes)
        {
            value = new BindValue(BindDataType.Blob, bytes);
        }
        else
        {
            return Report(new LobWriteResult(false, "BLOB 는 「파일에서 불러오기」 로 바꿀 내용을 먼저 불러오세요."));
        }

        var writer = ResolveWriter();
        if (writer is null)
        {
            return Report(new LobWriteResult(false, "실행 엔진을 찾을 수 없습니다."));
        }

        IsBusy = true;
        try
        {
            var result = await writer.WriteAsync(session, target, value, commit, ct).ConfigureAwait(true);
            if (result.Success)
            {
                IsDirty = false;
                UpdateFlags();
            }

            return Report(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private LobWriteResult Report(LobWriteResult r)
    {
        StatusText = r.Message;
        return r;
    }

    private async Task LoadTextAsync(LobEditorSource src, LobValue lob, CancellationToken ct)
    {
        if (!lob.IsTruncated && lob.TextPreview is { } full)
        {
            Text = full;
            return;
        }

        await using var stream = await OpenStreamAsync(ct).ConfigureAwait(true);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, LobContent.CopyBufferSize);
        var sb = new StringBuilder();
        var buffer = new char[LobContent.CopyBufferSize];
        while (sb.Length < LobContent.MaxEditChars)
        {
            var want = Math.Min(buffer.Length, LobContent.MaxEditChars - sb.Length);
            var n = await reader.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(true);
            if (n <= 0)
            {
                break;
            }

            sb.Append(buffer, 0, n);
        }

        Text = sb.ToString();
        IsPartial = reader.Peek() >= 0;
    }

    private async Task LoadBinaryAsync(LobEditorSource src, LobValue lob, CancellationToken ct)
    {
        if (!lob.IsTruncated)
        {
            ShowBytes(lob.BinaryPreview.ToArray());
            return;
        }

        var head = new byte[LobContent.MaxHexBytes];
        int got;
        await using (var stream = await OpenStreamAsync(ct).ConfigureAwait(true))
        {
            got = await ReadFullyAsync(stream, head, ct).ConfigureAwait(true);
        }

        var mime = LobContent.DetectImageMime(head.AsSpan(0, got));
        if (mime is not null && lob.Length is > 0 and <= LobContent.MaxImageBytes)
        {
            var all = new byte[lob.Length];
            await using var stream = await OpenStreamAsync(ct).ConfigureAwait(true);
            var n = await ReadFullyAsync(stream, all, ct).ConfigureAwait(true);
            ImageBytes = n == all.Length ? all : null;
        }

        HexText = LobContent.HexDump(head.AsSpan(0, got));
        if (lob.Length > got)
        {
            HexText += $"... (앞 {got:N0} B 만 보임, 전체 {lob.Length:N0} B)" + Environment.NewLine;
        }
    }

    private void ShowBytes(byte[] bytes)
    {
        var n = Math.Min(bytes.Length, LobContent.MaxHexBytes);
        HexText = LobContent.HexDump(bytes.AsSpan(0, n));
        if (bytes.Length > n)
        {
            HexText += $"... (앞 {n:N0} B 만 보임, 전체 {bytes.Length:N0} B)" + Environment.NewLine;
        }

        ImageBytes = bytes.Length <= LobContent.MaxImageBytes && LobContent.DetectImageMime(bytes) is not null ? bytes : null;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>LOB 전체 내용을 읽는 스트림(CLOB=UTF-8). 미리보기가 전체면 서버 없이 미리보기에서 만든다.</summary>
    private async Task<Stream> OpenStreamAsync(CancellationToken ct)
    {
        var src = _source ?? throw new InvalidOperationException("LOB 가 없습니다.");
        var lob = src.Lob ?? throw new InvalidOperationException("LOB 가 아닌 값입니다.");
        if (!lob.IsTruncated && lob.Kind != LobKind.BFile)
        {
            return new MemoryStream(lob.Kind is LobKind.Clob or LobKind.NClob
                ? new UTF8Encoding(false).GetBytes(lob.TextPreview ?? string.Empty)
                : lob.BinaryPreview.ToArray());
        }

        var accessor = ResolveAccessor() ?? throw new InvalidOperationException("LOB 읽기 서비스를 찾을 수 없습니다.");
        var session = src.Session ?? throw new InvalidOperationException("접속이 없습니다.");
        return await accessor.OpenReadAsync(session, lob, ct).ConfigureAwait(false);
    }

    private ILobAccessor? ResolveAccessor() => _accessor ??= _services?.GetService(typeof(ILobAccessor)) as ILobAccessor;

    private LobWriter? ResolveWriter()
    {
        if (_writer is not null)
        {
            return _writer;
        }

        return _services?.GetService(typeof(IExecutionService)) is IExecutionService exec ? _writer = new LobWriter(exec) : null;
    }

    private void UpdateFlags()
    {
        var src = _source;
        var writable = src is { Lob: not null, Target: not null, Session: { } s } && !s.Profile.ReadOnly;
        CanEditText = writable && IsText && !IsPartial;
        CanSaveToDatabase = writable && IsDirty && !IsPartial;
        if (src is { Lob: not null } && !writable && !IsBusy && string.IsNullOrEmpty(StatusText))
        {
            StatusText = src.Session?.Profile.ReadOnly == true
                ? "읽기 전용 접속이라 저장할 수 없습니다(보기·파일로 저장만)."
                : src.SaveBlockedReason ?? string.Empty;
        }
    }

    private static string Describe(LobValue lob)
    {
        var kind = lob.Kind switch
        {
            LobKind.Clob => "CLOB",
            LobKind.NClob => "NCLOB",
            LobKind.Blob => "BLOB",
            _ => "BFILE",
        };
        var unit = lob.Kind is LobKind.Clob or LobKind.NClob ? "문자" : "B";
        return lob.Length < 0 ? kind : $"{kind} {lob.Length:N0} {unit}";
    }
}
