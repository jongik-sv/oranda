using Oranda.App.Features.Localization;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>새 스크립트를 만들 수 없는 까닭.</summary>
public enum NewScriptError
{
    None,
    EmptyName,
    InvalidChar,
    ReservedName,
    TrailingDot,
    TooLong,
    AlreadyExists,
    FolderMissing,
    NoPermission,
    Failed,
}

/// <summary>이름 검사 결과. <see cref="FileName"/> 은 확장자를 붙인 최종 이름(오류면 입력을 다듬은 것).</summary>
/// <param name="FileName">최종 파일 이름.</param>
/// <param name="Error">오류(없으면 <see cref="NewScriptError.None"/>).</param>
/// <param name="Detail">오류에 끼울 값(쓸 수 없는 글자·예약 이름·시스템 메시지).</param>
public readonly record struct NewScriptCheck(string FileName, NewScriptError Error, string? Detail = null)
{
    public bool Ok => Error == NewScriptError.None;

    /// <summary>창 안 오류 줄에 보일 글(오류가 없으면 빈 글).</summary>
    public string Message => NewScriptFile.Describe(Error, Detail);
}

/// <summary>만들기 결과. 성공하면 <see cref="Path"/> 에 만든 파일의 전체 경로가 있다.</summary>
public readonly record struct NewScriptResult(string? Path, NewScriptError Error, string? Detail = null)
{
    public bool Ok => Error == NewScriptError.None && Path is not null;

    public string Message => NewScriptFile.Describe(Error, Detail);
}

/// <summary>
/// 스크립트 창의 「새 스크립트」: 기본 이름·이름 검사·파일 만들기. 로컬 파일만 만들고 DB 에는 아무것도 쓰지 않는다.
/// 기존 파일은 절대 덮어쓰지 않는다(<see cref="FileMode.CreateNew"/>).
/// </summary>
public static class NewScriptFile
{
    public const string DefaultBaseName = "새 스크립트";

    public const string DefaultExtension = ".sql";

    private const int MaxNameLength = 255;

    /// <summary>어느 PC 에서든 파일 이름에 쓸 수 없는 글자(Windows 기준 + 폴더 구분).</summary>
    private static readonly System.Buffers.SearchValues<char> ForbiddenChars = System.Buffers.SearchValues.Create("\\/:*?\"<>|");

    /// <summary>Windows 가 장치 이름으로 예약한 이름(확장자가 붙어도 마찬가지).</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>폴더 안에서 아직 없는 기본 이름: 「새 스크립트.sql」, 겹치면 「새 스크립트 2.sql」, 「새 스크립트 3.sql」…</summary>
    public static string SuggestName(string folder)
    {
        var name = DefaultBaseName + DefaultExtension;
        for (var n = 2; Exists(folder, name); n++)
        {
            name = $"{DefaultBaseName} {n}{DefaultExtension}";
        }

        return name;
    }

    /// <summary>입력을 다듬는다: 앞뒤 공백을 지우고, 스크립트 확장자가 없으면 <c>.sql</c> 을 붙인다.</summary>
    public static string Normalize(string? input)
    {
        var name = (input ?? string.Empty).Trim();
        if (name.Length == 0 || name.EndsWith('.') || ScriptsViewModel.IsScriptFile(name))
        {
            return name;
        }

        return name + DefaultExtension;
    }

    /// <summary>이름을 검사한다(파일은 만들지 않는다). <paramref name="windowsRules"/> 가 null 이면 이 PC 가 Windows 인지로 정한다.</summary>
    public static NewScriptCheck Check(string folder, string? input, bool? windowsRules = null)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new NewScriptCheck(trimmed, NewScriptError.EmptyName);
        }

        var bad = trimmed.AsSpan().IndexOfAny(ForbiddenChars);
        if (bad < 0)
        {
            bad = FirstControl(trimmed);
        }

        if (bad >= 0)
        {
            var c = trimmed[bad];
            return new NewScriptCheck(trimmed, NewScriptError.InvalidChar, char.IsControl(c) ? "U+" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) : c.ToString());
        }

        if (trimmed.EndsWith('.'))
        {
            return new NewScriptCheck(trimmed, NewScriptError.TrailingDot);
        }

        var name = Normalize(trimmed);
        if (Path.GetFileNameWithoutExtension(name).Length == 0)
        {
            return new NewScriptCheck(name, NewScriptError.EmptyName); // 「.sql」 만 쓴 경우
        }

        if (name.Length > MaxNameLength)
        {
            return new NewScriptCheck(name, NewScriptError.TooLong);
        }

        if (windowsRules ?? OperatingSystem.IsWindows())
        {
            var stem = name.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                return new NewScriptCheck(name, NewScriptError.ReservedName, stem.ToUpperInvariant());
            }
        }

        if (!Directory.Exists(folder))
        {
            return new NewScriptCheck(name, NewScriptError.FolderMissing);
        }

        return Exists(folder, name) ? new NewScriptCheck(name, NewScriptError.AlreadyExists) : new NewScriptCheck(name, NewScriptError.None);
    }

    /// <summary>
    /// 파일을 만든다. 이름 검사를 통과해야 하고, 이미 있으면 덮어쓰지 않고 <see cref="NewScriptError.AlreadyExists"/>.
    /// <paramref name="content"/> 가 비면 빈 파일이다. 글자는 편집기가 새 파일을 저장할 때와 같은 UTF-8(BOM 없음)로 쓴다.
    /// </summary>
    public static NewScriptResult Create(string folder, string? input, string? content = null, bool? windowsRules = null)
        => Create(folder, input, content, windowsRules, open: null);

    internal static NewScriptResult Create(string folder, string? input, string? content, bool? windowsRules, Func<string, Stream>? open)
    {
        var check = Check(folder, input, windowsRules);
        if (!check.Ok)
        {
            return new NewScriptResult(null, check.Error, check.Detail);
        }

        var path = Path.Combine(folder, check.FileName);
        var created = false;
        try
        {
            using (var stream = open is null ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None) : open(path))
            {
                created = true;
                if (!string.IsNullOrEmpty(content))
                {
                    var bytes = FileEncoding.Encode(content, "utf-8");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }

            return new NewScriptResult(path, NewScriptError.None);
        }
        catch (UnauthorizedAccessException)
        {
            return new NewScriptResult(null, NewScriptError.NoPermission);
        }
        catch (DirectoryNotFoundException)
        {
            return new NewScriptResult(null, NewScriptError.FolderMissing);
        }
        catch (IOException ex)
        {
            // 확인한 뒤 누군가 먼저 만들었으면 CreateNew 가 IOException 을 던진다. 그 파일은 지우지 않는다.
            if (!created && File.Exists(path))
            {
                return new NewScriptResult(null, NewScriptError.AlreadyExists);
            }

            TryDelete(path, created);
            return new NewScriptResult(null, NewScriptError.Failed, ex.Message);
        }
    }

    /// <summary>
    /// 새 스크립트를 만들 폴더: 폴더를 골랐으면 그 폴더, 파일을 골랐으면 그 파일의 폴더, 선택이 없으면 연결 폴더가 하나일 때 그 폴더.
    /// 연결 폴더가 없거나 여럿이면 null(고를 폴더가 없다).
    /// </summary>
    public static string? ResolveTargetFolder(string? selectedPath, bool selectedIsDirectory, IReadOnlyList<string> rootPaths)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);
        if (!string.IsNullOrEmpty(selectedPath))
        {
            return selectedIsDirectory ? selectedPath : Path.GetDirectoryName(selectedPath) is { Length: > 0 } dir ? dir : null;
        }

        return rootPaths.Count == 1 ? rootPaths[0] : null;
    }

    /// <summary>오류를 창 안에 보일 짧은 글로(오류 없음 = 빈 글).</summary>
    public static string Describe(NewScriptError error, string? detail = null) => error switch
    {
        NewScriptError.None => string.Empty,
        NewScriptError.EmptyName => UiText.Get("Scripts.new.error.empty", "이름을 입력하세요"),
        NewScriptError.InvalidChar => UiText.Format("Scripts.new.error.char", "쓸 수 없는 글자가 있습니다: {0}", detail ?? string.Empty),
        NewScriptError.ReservedName => UiText.Format("Scripts.new.error.reserved", "Windows 예약 이름은 쓸 수 없습니다: {0}", detail ?? string.Empty),
        NewScriptError.TrailingDot => UiText.Get("Scripts.new.error.dot", "이름 끝에 점을 쓸 수 없습니다"),
        NewScriptError.TooLong => UiText.Get("Scripts.new.error.long", "이름이 너무 깁니다(255자까지)"),
        NewScriptError.AlreadyExists => UiText.Get("Scripts.new.error.exists", "같은 이름의 파일이 이미 있습니다"),
        NewScriptError.FolderMissing => UiText.Get("Scripts.new.error.folder", "폴더를 찾을 수 없습니다"),
        NewScriptError.NoPermission => UiText.Get("Scripts.new.error.denied", "이 폴더에 쓸 권한이 없습니다"),
        _ => UiText.Format("Scripts.new.error.failed", "만들지 못했습니다: {0}", detail ?? string.Empty),
    };

    private static bool Exists(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static int FirstControl(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsControl(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static void TryDelete(string path, bool created)
    {
        if (!created)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 반쯤 쓴 파일을 못 지워도 더 할 일이 없다.
        }
    }
}
