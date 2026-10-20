namespace Oranda.App.Features.Update;

/// <summary>새로 찾은 버전 한 개(화면에 보일 정보만). Velopack 형식은 구현 안에 둔다.</summary>
/// <param name="Version">새 버전 문자열.</param>
/// <param name="NotesMarkdown">릴리스 노트(마크다운 원문, 없으면 null).</param>
/// <param name="SizeBytes">내려받을 크기.</param>
public sealed record UpdateOffer(string Version, string? NotesMarkdown, long SizeBytes);

/// <summary>
/// 업데이트 확인·내려받기·적용의 작은 입구(TSK-10-01). 화면·명령은 이 인터페이스만 쓰고, 시험은 가짜로 바꾼다.
/// 적용은 두 가지뿐이며 둘 다 「앱이 실제로 끝날 때」 Velopack 갱신기를 부른다 — 앱을 즉시 끄는 경로는 없다
/// (저장 안 한 탭·커밋 안 한 트랜잭션 확인과 작업 공간 저장을 건너뛰지 않기 위해).
/// </summary>
public interface IAppUpdater
{
    /// <summary>설치 파일(Setup.exe·.pkg)로 설치한 실행본인지. 개발 실행(dotnet run, bin 직접 실행)이면 false.</summary>
    bool IsInstalled { get; }

    /// <summary>설치본의 Velopack 버전. 설치본이 아니면 null.</summary>
    string? InstalledVersion { get; }

    /// <summary>내려받아 적용만 남은 버전. 없으면 null(앞선 실행에서 받아 둔 것도 포함).</summary>
    string? PendingRestart { get; }

    /// <summary>피드에서 새 버전을 찾는다. 새 버전이 없으면 null. 연결 실패 등은 예외로 알린다.</summary>
    Task<UpdateOffer?> CheckAsync(UpdateFeed feed, CancellationToken ct);

    /// <summary>방금 <see cref="CheckAsync"/> 가 돌려준 버전을 내려받는다. 진행률은 0~100.</summary>
    Task DownloadAsync(UpdateOffer offer, IProgress<int>? progress, CancellationToken ct);

    /// <summary>내려받은 버전을 앱이 끝난 뒤 적용한다(다시 띄우지 않는다). 종료 확인을 모두 통과한 뒤에만 부른다.</summary>
    void ApplyOnExit();

    /// <summary>내려받은 버전을 앱이 끝난 뒤 적용하고 다시 띄운다. 종료 확인을 모두 통과한 뒤에만 부른다.</summary>
    void ApplyAndRestart();
}
