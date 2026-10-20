# 성능 측정 절차 (PRD 7.2)

TSK-11-03 에서 만든 측정 도구와 쓰는 법이다. 결과와 판정은 `docs/itest/TSK-11-03.md`, 맥북 참고값은 `docs/perf/macos-2026-10-20.md`.

## 두 가지 성능 시험을 나눈다

| 종류 | 무엇 | 언제 | 명령 |
|---|---|---|---|
| 성능 단정 시험 | `[Trait("Category","Perf")]` 가 붙은 xUnit 시험 (포맷 2,000줄, L1 문맥, 1만 줄 분리·키 입력 등). 헤드리스·부하에 따라 흔들려서 기본 실행에서 뺀다 | 조용한 장비에서 따로 | `dotnet test Oranda.sln -c Release --filter "Category=Perf"` |
| 게시본 측정 | Release·ReadyToRun 게시본과 실제 창으로 PRD 7.2 의 12개 항목을 재는 도구 (이 문서의 나머지) | 조용한 장비에서 따로 | `scripts/perf/run-windows.ps1`, `scripts/perf/run-macos.sh` |

- 기본 `dotnet test` 와 CI 는 `Category!=Perf` 다. `tests/Directory.Build.props` 의 `VSTestTestCaseFilter` 가 기본 필터이고, 명령줄 `--filter` 가 이것을 덮어쓴다. 통합 시험 프로젝트는 `Category!=Integration&Category!=Perf`.
- 성능 단정 시험은 Debug 에서는 한도를 3배로 늘려 두었다. 판정은 Release 로 돌린 값만 쓴다.

## 도구

| 도구 | 하는 일 |
|---|---|
| `tests/Oranda.Perf` | 게시본 실행 파일을 반복해서 띄워 시작 표식(`PerfMarks`)과 전용 메모리를 읽는다. 옵션: `--exe --runs --discard --data-dir --cold-copy --idle-s --out` |
| `tests/Oranda.Perf.Ui` | 진짜 앱(`Oranda.App`)을 진짜 창(Win32·macOS 백엔드, 헤드리스 아님)으로 한 프로세스 안에 띄우고 사용자 동작을 흉내 낸다. 시나리오: `format typing diagnostics completion firstrow cancel memory grid` |
| `tests/Oranda.Perf.Common` | 위 두 도구가 같이 쓰는 통계(중앙값·p95)·메모리 읽기(Windows 전용 작업 집합, macOS `footprint`)·결과표 |
| `tests/Oranda.Perf.Tests` | 위 도구의 시험 (통계, 옵션, 표식 읽기, 가짜 앱 실행, 프레임·스크롤 계획) |
| `scripts/perf/run-windows.ps1`, `run-macos.sh` | 게시 → 모든 측정을 정해진 횟수로 돌려 `artifacts/perf-*` 에 `.md`·`.json` 으로 남긴다 |

판정 값은 항목마다 PRD 가 정한 통계를 쓴다. 시작·메모리는 중앙값, 키 입력·자동완성 등 지연은 p95. 표본은 항목마다 최소 5개이고 대부분 15~500개다. 창 시나리오는 프로세스를 3번 다시 띄워(`--label r1..r3`) 흔들림도 본다.

## 측정 조건

- 조용한 PC: 다른 빌드·시험·무거운 프로그램이 없다. 로컬 콘솔 세션(원격 데스크톱이면 프레임이 원격 주기에 묶인다), 화면 잠금 없음, 60 Hz 이상.
- Windows: 전원 연결, 전원 모드 최고 성능이 좋다. macOS: AC 전원, 저전력 모드 끔.
- Oracle 항목은 상주 컨테이너(`oracle-26ai-free`, `127.0.0.1:1521/FREEPDB1`)를 쓴다. 환경 변수 `ORANDA_IT_ADMIN_PASSWORD`(필수), `ORANDA_IT_ADMIN_USER`(기본 SYSTEM), `ORANDA_IT_HOST/PORT/SERVICE`. 도구가 `ORA_PF_<hex>` 사용자를 만들고 끝나면 지운다. 새 컨테이너를 띄우지 않는다. 비밀번호는 출력하지 않는다.
- 데이터 폴더(`ORANDA_DATA_DIR`)는 실행마다 빈 임시 폴더다. 시작 시간만 예외로 고정 폴더(레이아웃 복원이 있는 「두 번째 실행」)를 쓴다.

## Windows 에서 돌리기

```powershell
# 한 번만: Velopack (설치 파일 크기용)
dotnet tool install -g vpk --version 1.2.161
$env:ORANDA_IT_ADMIN_PASSWORD = '<관리자 비밀번호>'   # 출력·커밋하지 않는다
pwsh scripts/perf/run-windows.ps1 -Out artifacts/perf-win
```

## macOS 에서 돌리기 (맥북 세션이 그대로 따라 한다)

```bash
git switch dev-jji-win144 && git pull
sudo -v                                   # 「최초 실행」 측정이 매 회 sudo purge 를 쓴다
export ORANDA_IT_ADMIN_PASSWORD='<관리자 비밀번호>'   # 출력·커밋하지 않는다
# Oracle 컨테이너가 맥에서 도달 가능해야 한다 (필요하면 ORANDA_IT_HOST=<주소>)
scripts/perf/run-macos.sh artifacts/perf-mac
```

- 결과: `artifacts/perf-mac/**/*.md`. 항목별 값을 `docs/itest/TSK-11-03.md` 의 「macOS」 칸에 옮기고 판정을 적는다.
- 게시 폴더는 리포 안(`artifacts/`)에서 실행한다. 임시 폴더에서는 실행 파일이 시작 즉시 SIGKILL 된 적이 있다.
- 설치 파일 크기는 CI macos 작업의 「크기 검사」 단계 로그 값을 쓴다 (서명·공증 포함 `.pkg`).
- 도구 단독 실행: `dotnet run --project tests/Oranda.Perf -c Release -- --exe artifacts/publish/osx-arm64/Oranda.App --runs 12 --discard 2 --data-dir <폴더>`,
  `artifacts/perfui/osx-arm64/Oranda.Perf.Ui <시나리오> --out <폴더>`.
- macOS 코드 경로(`footprint` 읽기)는 읽는 글자 해석만 단위 시험했고 실제 맥에서는 아직 돌리지 않았다. 처음 돌릴 때 `idle-memory` 결과의 「전용 메모리」 칸이 비어 있으면 `footprint -p <pid>` 출력 형식부터 확인한다.

## 항목별 측정 방법

| PRD 7.2 항목 | 도구·시나리오 | 방법 |
|---|---|---|
| 앱 시작 → 입력 가능 | `Oranda.Perf --discard 2 --data-dir` | 게시본을 12번 띄운다 (앞 2번 버림). `PerfMarks` 의 input-ready 중앙값 |
| 최초 실행 | Windows `--cold-copy`, macOS `sudo purge` | 매 회 새 복사본(Windows) 또는 파일 캐시를 비운 직후(macOS) 한 번 |
| 유휴 메모리 (접속 1, 탭 1) | `Perf.Ui memory` | 접속 1·탭 1 에서 10초 유휴 뒤 전용 메모리 5번의 중앙값. 접속·탭 없음은 `Oranda.Perf --idle-s 10` |
| 접속 3 + 10만 행 결과 3 | `Perf.Ui memory` | 활성 접속 1 + 작업 세션 2, 결과 3개를 모두 가져온 뒤 10초 유휴 |
| 키 입력 → 화면 반영 | `Perf.Ui typing` | 1만 줄 문서의 실제 편집기 탭에 입력 이벤트를 넣고 렌더 스레드가 그릴 때까지 (D-007: 틱 대기를 뺀 처리 시간이 판정 값) |
| 자동완성 목록 표시 | `Perf.Ui completion` | 진짜 접속에서 캐시가 데워진 뒤 별칭 뒤 「.」 요청 → 창이 그려질 때까지 |
| 문법 검사 반영 | `Perf.Ui diagnostics` | 1만 줄 문서 끝에 오류를 넣고 밑줄이 반영될 때까지 (디바운스 300 ms 를 포함한 값과 뺀 값 둘 다) |
| 포맷 (2,000줄) | `Perf.Ui format` | 포맷 서비스와 편집기 탭 적용 경로, 예열 3 + 25회 |
| 첫 행 표시 | `Perf.Ui firstrow` | 첫 묶음이 그리드 모델에 들어온 시각 → 그 행이 그려진 시각 |
| 100만 행 스크롤, 행당 메모리 | `Perf.Ui grid` | 100만 행을 모두 가져온 뒤 4가지 자동 스크롤을 8초씩, 창 프레임 간격으로 fps. 스크롤 없는 대조 구간 포함 |
| 실행 취소 반응 | `Perf.Ui cancel` | 실행 중(PL/SQL sleep, CPU 루프)에 취소 명령 → 실행 상태가 끝날 때까지 |
| 설치 파일 크기 | `vpk pack` + `scripts/release/check-installer-size.ps1` | Windows Setup.exe 크기 (macOS 는 CI .pkg) |

## 못 재는 것·한계

- 사람의 손(실제 키보드, 마우스 휠)과 모니터 스캔아웃은 재지 않는다. 입력은 이벤트 주입, 프레임은 창의 애니메이션 프레임 간격이다.
- 실행 취소 반응은 서버가 브레이크를 읽는 시점에 달렸다(TSK-06-11). gvenzl 이미지(상주 컨테이너, CI)는 서버 `sqlnet.ora` 에 `DISABLE_OOB=ON`, `BREAK_POLL_SKIP=1000` 을 넣어 두었다. 그래서 PL/SQL 대기 중 취소는 대기가 끝난 뒤에야 끝나고, CPU 루프도 몇 초 걸린다. 컨테이너 포트 중계(podman gvproxy, docker-proxy)는 TCP 긴급 데이터를 버리므로 OOB 를 켜도 중계를 거치면 브레이크가 사라진다. 1 s 목표는 OOB 가 켜진 서버에 중계 없이 붙어서 잰다.
- 「최초 실행」 은 OS 파일 캐시를 완전히 비우지 못한다 (Windows 는 새로 복사한 파일 = 백신 검사·캐시 없음의 근사).
- 접속 3개는 앱이 작업 접속을 하나만 두기 때문에 활성 1 + 세션 관리자로 연 2개로 만든다.
