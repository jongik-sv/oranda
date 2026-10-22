using System.Collections.ObjectModel;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>컴파일 오류 창에 오류 목록을 올리는 쪽: PL/SQL 편집 탭, 또는 SQL 편집기에서 실행한 CREATE 문장.</summary>
public interface ICompileErrorSource
{
    /// <summary>창 머리 줄에 보일 이름.</summary>
    string Title { get; }

    /// <summary>오류·경고(서버 순서).</summary>
    ObservableCollection<CompileError> Errors { get; }

    /// <summary>오류 위치로 커서를 옮긴다. 옮길 수 없으면 false.</summary>
    bool NavigateTo(CompileError item);
}
