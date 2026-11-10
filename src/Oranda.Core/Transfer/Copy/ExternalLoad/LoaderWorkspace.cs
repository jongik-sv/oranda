using System.Globalization;
using System.Text;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// 테이블 하나의 외부 도구 작업 폴더 <c>&lt;root&gt;/oranda-load/&lt;runId&gt;/&lt;n&gt;-&lt;table&gt;</c>(root 기본 = 사용자 임시 폴더). 데이터·LOB·제어·parfile·로그·거절 파일이 모두 여기 있다.
/// <see cref="Dispose"/> 가 폴더째 지운다(비밀 parfile 도 함께). 지우기는 예외를 내지 않고 실패 이유를 <see cref="CleanupFailure"/> 에 남긴다.
/// 폴더 이름의 테이블 부분은 ASCII 글자·숫자·<c>_$#-</c> 만 남긴다(도구가 경로를 OS 문자 집합으로 열 수 있어서).
/// </summary>
public sealed class LoaderWorkspace : IDisposable
{
    public const string FolderName = "oranda-load";

    private const int MaxNamePart = 40;

    private static int s_sequence;

    private bool _disposed;

    private LoaderWorkspace(string runDirectory, string directory)
    {
        RunDirectory = runDirectory;
        Directory = directory;
    }

    /// <summary>실행 폴더(<c>&lt;root&gt;/oranda-load/&lt;runId&gt;</c>).</summary>
    public string RunDirectory { get; }

    /// <summary>이 테이블 폴더(전체 경로).</summary>
    public string Directory { get; }

    public string DataFile => Path.Combine(Directory, LoaderDataWriter.DataFileName);

    public string ControlFile => Path.Combine(Directory, SqlLoaderControlFile.FileName);

    public string ParFile => Path.Combine(Directory, SqlLoaderCommand.ParFileName);

    public string LogFile => Path.Combine(Directory, "load.log");

    public string BadFile => Path.Combine(Directory, "load.bad");

    public string DiscardFile => Path.Combine(Directory, "load.dsc");

    /// <summary>폴더를 다 지우지 못했으면 그 이유(경로·OS 오류 글). 지웠거나 아직 지우지 않았으면 null.</summary>
    public string? CleanupFailure { get; private set; }

    /// <summary>폴더 경로가 ASCII 만으로 되어 있는지. 아니면 도구가 파일을 못 열 수 있다(사전 점검·안내용).</summary>
    public bool IsAsciiPath => Directory.All(c => c < 128);

    /// <summary>새 테이블 폴더를 만든다. 순번은 이 프로세스 안에서 차례로 매긴다(동시 레인이 같은 테이블 이름을 써도 겹치지 않는다). <paramref name="root"/> null = 사용자 임시 폴더.</summary>
    public static LoaderWorkspace Create(string? root, string runId, string table) =>
        Create(runId, Interlocked.Increment(ref s_sequence), table, root);

    /// <summary>새 테이블 폴더를 만든다(있던 같은 이름 폴더는 비운다). <paramref name="index"/> = 실행 안 테이블 순번.</summary>
    public static LoaderWorkspace Create(string runId, int index, string table, string? root = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(table);
        var baseRoot = string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root;
        var runDirectory = Path.GetFullPath(Path.Combine(baseRoot, FolderName, Sanitize(runId)));
        var directory = Path.Combine(runDirectory, index.ToString(CultureInfo.InvariantCulture) + "-" + Sanitize(table));
        if (System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }

        System.IO.Directory.CreateDirectory(directory);
        return new LoaderWorkspace(runDirectory, directory);
    }

    /// <summary>
    /// 파일 글(UTF-8, 도구가 아직 쥐고 있어도 읽는다). 없거나 읽지 못하면 null — 로그가 없으면(도구가 로그를 만들기 전에 멈춤) 표준 출력으로 판단한다.
    /// 상대 경로는 이 폴더 기준.
    /// </summary>
    public string? ReadText(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.IsPathRooted(path) ? path : Path.Combine(Directory, path);
        try
        {
            if (!File.Exists(full))
            {
                return null;
            }

            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 폴더를 지우고, 실행 폴더가 비면 그것도 지운다. 예외를 내지 않는다(읽기 전용 파일은 속성을 풀고 다시 시도, 그래도 남으면 <see cref="CleanupFailure"/>).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CleanupFailure = TryDelete(Directory);
        try
        {
            if (System.IO.Directory.Exists(RunDirectory) && !System.IO.Directory.EnumerateFileSystemEntries(RunDirectory).Any())
            {
                System.IO.Directory.Delete(RunDirectory);
            }

            // 빈 oranda-load 폴더도 남기지 않는다(#181: 빈 폴더가 쌓임).
            var parent = Path.GetDirectoryName(RunDirectory);
            if (parent is not null && System.IO.Directory.Exists(parent) && !System.IO.Directory.EnumerateFileSystemEntries(parent).Any())
            {
                System.IO.Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 다른 테이블이 같은 실행 폴더를 쓰는 중일 수 있다 — 남겨 둔다.
        }
    }

    /// <summary>
    /// <c>&lt;root&gt;/oranda-load</c> 아래 마지막 쓰기가 <paramref name="age"/> 보다 오래된 실행 폴더를 지운다(앞선 실행이 비정상으로 끝나 남긴 것). 예외를 내지 않는다. 지운 폴더 수.
    /// </summary>
    public static int DeleteStale(string? root, TimeSpan age)
    {
        var parent = Path.Combine(string.IsNullOrWhiteSpace(root) ? Path.GetTempPath() : root, FolderName);
        var removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(parent))
            {
                return 0;
            }

            var cutoff = DateTime.UtcNow - age;
            foreach (var run in System.IO.Directory.EnumerateDirectories(parent))
            {
                DateTime written;
                try
                {
                    written = LatestWrite(run);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (written < cutoff && TryDelete(run) is null)
                {
                    removed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 목록을 못 읽으면 다음 실행이 다시 해 본다.
        }

        return removed;
    }

    /// <summary>폴더 이름에 쓸 글자만 남긴다(ASCII 글자·숫자·<c>_$#-</c>, 그 밖은 <c>_</c>, 최대 40자).</summary>
    internal static string Sanitize(string name)
    {
        var b = new StringBuilder(Math.Min(name.Length, MaxNamePart));
        foreach (var c in name)
        {
            if (b.Length >= MaxNamePart)
            {
                break;
            }

            b.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '#' or '-' ? c : '_');
        }

        return b.Length == 0 ? "_" : b.ToString();
    }

    /// <summary>폴더와 그 아래 항목 중 가장 늦은 마지막 쓰기 시각(UTC). 한 단계 아래까지만 본다.</summary>
    private static DateTime LatestWrite(string directory)
    {
        var latest = System.IO.Directory.GetLastWriteTimeUtc(directory);
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
        {
            var t = System.IO.Directory.Exists(entry) ? System.IO.Directory.GetLastWriteTimeUtc(entry) : File.GetLastWriteTimeUtc(entry);
            if (t > latest)
            {
                latest = t;
            }
        }

        return latest;
    }

    /// <summary>폴더째 지운다. 성공(또는 없음)이면 null, 실패하면 이유 글.</summary>
    private static string? TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (!System.IO.Directory.Exists(directory))
                {
                    return null;
                }

                System.IO.Directory.Delete(directory, recursive: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 1)
                {
                    return $"외부 도구 작업 폴더를 지우지 못했습니다: {directory} ({ex.Message})";
                }

                ClearReadOnly(directory);
            }
        }

        return null;
    }

    private static void ClearReadOnly(string directory)
    {
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 다시 지우기가 실패를 알린다.
        }
    }
}
