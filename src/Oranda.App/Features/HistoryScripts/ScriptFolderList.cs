using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>
/// 스크립트 창에 연결한 로컬 폴더 목록(EXEC-09). 폴더 자체는 사용자 소유라 건드리지 않고, 목록만
/// <c>{데이터 폴더}/script-folders.json</c>(경로 문자열 배열)에 둔다. 파일이 없거나 깨졌으면 빈 목록.
/// </summary>
public sealed class ScriptFolderList
{
    private readonly string? _file;
    private readonly List<string> _folders = [];

    /// <summary>파일 없이 메모리만(시험·디자이너).</summary>
    public ScriptFolderList()
    {
    }

    public ScriptFolderList(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _file = Path.Combine(dataDirectory, "script-folders.json");
        Load();
    }

    public IReadOnlyList<string> Folders => _folders;

    /// <summary>추가(이미 있으면 false). 저장까지 한다.</summary>
    public bool Add(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (_folders.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _folders.Add(full);
        Save();
        return true;
    }

    public bool Remove(string folder)
    {
        var removed = _folders.RemoveAll(f => string.Equals(f, Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    private void Load()
    {
        try
        {
            if (_file is not null && File.Exists(_file))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file));
                _folders.AddRange(list?.Where(p => !string.IsNullOrWhiteSpace(p)) ?? []);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _folders.Clear(); // 목록 파일이 손상되면 비어 있는 것으로 시작한다(폴더 자체는 그대로).
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_folders));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 목록을 못 저장해도 이번 실행에서는 계속 쓸 수 있다.
        }
    }
}
