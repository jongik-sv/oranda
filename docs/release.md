# 릴리스 내는 법

업데이트 피드 = 이 저장소의 GitHub Releases(`https://github.com/jongik-sv/oranda`, 공개). CI(`.github/workflows/ci.yml`)가 릴리스 빌드에 이 주소를 기본값(`OrandaUpdateFeed`)으로 넣으므로, 설치본은 설정의 피드 주소가 비어 있으면 여기서 새 버전을 찾는다. 로컬 빌드는 값이 없어 「피드 없음」이다.

## 태그 푸시로 내기

1. 올릴 커밋에서 태그를 만든다. 태그 이름이 패키지 버전이 된다(`vX.Y.Z` → `X.Y.Z`, 시험판은 `vX.Y.Z-beta.1` 형식).
   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```
2. `ci` 워크플로가 macOS 패키지를 만들고(서명·공증 비밀값이 있으면 서명·공증), `release` 작업이 `vpk upload github --publish` 로 GitHub Release `Oranda 0.2.0` 을 올린다. 이전 릴리스가 있으면 먼저 받아 델타 패키지도 만든다.
3. 설치본은 시작 20초 뒤(24시간마다) 또는 도움말 > 업데이트 확인에서 새 버전을 찾아 내려받고, 종료할 때 적용한다.

## 수동 실행

Actions > ci > Run workflow(`workflow_dispatch`). 태그가 아니므로 버전은 `0.1.<실행 번호>`, 태그는 `v0.1.<실행 번호>` 로 올라간다.

## 올리지 않는 경우

보통의 push·PR 은 산출물(`release-macos-arm64`)만 남기고 GitHub Release 는 만들지 않는다. `release` 작업만 `contents: write` 권한을 가지며 토큰은 `GITHUB_TOKEN` 뿐이다.

## 첫 릴리스 전 확인

- 저장소 Settings > Actions > General 의 Workflow permissions 가 읽기 전용이어도 된다(`release` 작업이 자기 권한을 올린다).
- 첫 릴리스에는 받을 이전 릴리스가 없어 `Download previous release` 단계가 「no previous release to download」 를 찍고 넘어간다.
