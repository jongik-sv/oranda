using System.Collections.Generic;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>검색 질의: 입력 글과 「내용」 토글.</summary>
public sealed record ScriptSearchQuery(string Text, bool IncludeContent);

/// <summary>미리보기 글 안의 강조 구간.</summary>
public readonly record struct ScriptMatchRange(int Start, int Length);

/// <summary>내용 검색에서 맞은 한 줄: 줄 번호(1부터), 미리보기, 미리보기 기준 강조 구간(한 줄에 여러 번 있으면 모두).</summary>
public sealed record ScriptSearchLine(int LineNumber, string Preview, IReadOnlyList<ScriptMatchRange> Ranges);

/// <summary>검색에 걸린 파일. <see cref="Folder"/> 는 연결 폴더 이름부터 시작하는 상대 폴더, <see cref="Lines"/> 는 내용 일치(이름만 맞으면 비어 있다).</summary>
public sealed record ScriptSearchFile(string Path, string Name, string Folder, bool NameMatched, IReadOnlyList<ScriptSearchLine> Lines);

/// <summary>
/// 검색 결과. <see cref="MatchCount"/> 는 일치 줄 수 + 줄 일치가 없는 이름 일치 파일 수(한도 계산 기준).
/// 읽을 수 없는 파일·폴더는 건너뛰고 <see cref="SkippedUnreadable"/> 로 센다.
/// </summary>
public sealed record ScriptSearchResult(IReadOnlyList<ScriptSearchFile> Files, int MatchCount, bool LimitReached, int SkippedLarge, int SkippedUnreadable)
{
    public int FileCount => Files.Count;
}

/// <summary>검색 중 알리는 진행: 지금까지 살핀 파일 수와 일치 수(파일 20개마다).</summary>
public readonly record struct ScriptSearchProgress(int FilesScanned, int MatchCount);
