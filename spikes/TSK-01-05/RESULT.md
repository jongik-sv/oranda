# TSK-01-05 결과: 에디터 컴포넌트·다중 커서·L1 렉서 성능 시험 (D-05, T-D03)

측정일 2026-10-19 · 레인 l0105-editor · 측정 잠금을 잡고 측정(17:29~17:36)

## 0. 환경과 재현

| 항목 | 값 |
|---|---|
| PC | 11th Gen Intel Core i5-1135G7 2.4 GHz, 4코어 8스레드, Windows 11 10.0.26300 |
| 런타임 | .NET 10.0.13 (SDK 10.0.402), Release, 워크스테이션 GC |
| UI | Avalonia 12.1.4, **Avalonia.AvaloniaEdit 12.0.0**(11.4.1 은 동작 안 함, 1절) |
| 파서 | Antlr4.Runtime.Standard 4.13.1, grammars-v4 `sql/plsql` @ `3036ab7a` (Apache-2.0), 생성기 antlr-4.13.1-complete.jar + GraalVM JDK 21.0.7 |
| 벤치 | BenchmarkDotNet 0.15.8 (InProcessEmit 툴체인: 측정 중 빌드 없음) |
| 문서 | 합성 PL/SQL 패키지(스펙+바디) **10,007줄 / 384,195자**, 프로시저 약 200개(커서·DML·MERGE·q-quote·힌트·주석·바인드 포함), `PlSqlGen.Package()` |
| 부하 | 다른 레인 빌드 때문에 측정 시작 전 CPU 50~100% 였다. 잠금을 잡은 뒤 CPU 35% 미만이 될 때까지 기다리다(최대 10분) 32% 에서 시작. 실행 중 전체 CPU 13~72%(measure-log.txt). 측정 프로세스는 우선순위 AboveNormal. |

재현:
```
sh PlSqlAntlr/gen-antlr.sh <antlr-4.13.1-complete.jar>   # Java 필요. 없으면 ANTLR 측정만 빠지고 나머지는 빌드됨
dotnet build EditorSpike -c Release -o out/rel
out/rel/EditorSpike.exe selftest          # 자체 검증
pwsh -File measure.ps1                     # 잠금 + 전 측정, 결과 results/*.txt
```
모드: `typing-window`(실제 창) · `typing`(헤드리스) · `bench-lexer` · `antlr`(콜드, 자식 프로세스) · `bench-antlr`(웜) · `diag` · `compat`.
원자료: `results/typing-window.txt`, `typing-headless.txt`, `bench-lexer.txt`, `antlr-cold.txt`, `bench-antlr.txt`, `measure-log.txt`.

## 1. AvaloniaEdit 버전 호환 (지정 11.4.1 → 사용 불가)

| 버전 | NuGet 의존성 | 결과 |
|---|---|---|
| 11.4.1 | Avalonia ≥ 11.0.0 (상한 없음) | restore·build 는 통과하지만 **실행 시 `TypeLoadException: Avalonia.Controls.Primitives.IScrollable` (Avalonia.Controls 12.1.4)** — 에디터 생성 즉시 실패. `-p:AvaloniaEditVersion=11.4.1` 로 재현 |
| **12.0.0** | Avalonia ≥ 12.0.0 | 정상(1만 줄 열기·입력·렌더). 현재 NuGet 최신 정식판(12.0.0-rc1 다음) |

→ wbs TSK-01-05 기술 스펙의 `Avalonia.AvaloniaEdit 11.4.1` 은 틀렸고, TRD 2절 표의 12.0.0 이 맞다. 조정자가 wbs 를 12.0.0 으로 고쳐야 한다.

## 2. e1 키 입력 → 화면 반영 (목표 p95 ≤ 16 ms)

### 2.1 실제 창 (대표값)
방법: Win32 실제 창(1600×1000, 기본 렌더러 = GPU), 5000번째 줄로 스크롤. 표본마다 25 ms 쉬고 TextArea 에 `TextInput`/`KeyDown` 라우팅 이벤트를 주입 → `window.UpdateLayout()` → `Compositor.RequestCompositionBatchCommitAsync()` 의 `Processed`·`Rendered` 대기. 표본 500개(예열 50개 버림, 별도 JIT 예열 시나리오 1회 버림). 문자는 'x' 입력과 Backspace 를 번갈아(문서 크기 유지).
- **작업 합** = 표본마다 입력처리 + 레이아웃 + 렌더(Processed→Rendered). 우리가 줄일 수 있는 비용.
- **반영** = 주입 → Rendered. 다음 렌더 틱(60 Hz, 0~16.7 ms)을 기다리는 시간이 포함된다.
- 한계: OS 입력 경로(WM_CHAR → Avalonia)와 모니터 스캔아웃은 빠진다. 렌더 스레드 시간은 GPU 제출까지이고 실제 표시 시각은 아니다.

| 시나리오 | 작업 합 p50 | **작업 합 p95** | 작업 합 max | 반영 p50 | 반영 p95 | 반영 p99 | 반영 max | 입력처리 p95 | 레이아웃 p95 | 렌더 p95 |
|---|---|---|---|---|---|---|---|---|---|---|
| 문자 입력/BS, 강조 없음 | 3.39 | **4.61** | 13.50 | 12.68 | 21.11 | 23.93 | 24.90 | 1.35 | 0.46 | 3.33 |
| 문자 입력/BS, L1 강조 | 3.02 | **5.20** | 13.02 | 12.79 | 21.15 | 23.93 | 486.29 | 1.19 | 0.45 | 4.05 |
| Enter/BS, L1 강조 | 4.24 | **6.22** | 522.23 | 14.13 | 22.83 | 486.18 | 536.06 | 1.30 | 0.83 | 4.53 |
| 문자 입력/BS, L1 + 다중 커서 50개 | 7.42 | **14.19** | 515.66 | 18.82 | 33.12 | 54.61 | 526.70 | 2.25 | 8.03 | 5.81 |

(단위 ms)

판정:
- **키 입력 처리 비용(작업 합) p95 = 4.6~6.2 ms(커서 1개), 14.2 ms(커서 50개) → 16 ms 이하 충족.** 1만 줄이어도 비용은 화면에 보이는 줄 수에 비례한다(헤드리스에서 1,040줄과 10,007줄 문서의 비용이 같았음, `diag`).
- **반영(틱 대기 포함) p95 = 21~23 ms** 로 16 ms 를 넘는다. 60 Hz 렌더 루프에 맞춰 그리는 구조라 작업이 0 이어도 평균 8 ms, 최대 16.7 ms 를 기다리므로 "키 → 화면 16 ms" 를 문자 그대로는 어떤 60 Hz 렌더러도 보장할 수 없다. 수락 기준은 "한 프레임 예산 안에 처리" 로 읽어 작업 합 p95 로 판정할 것을 제안한다.
- 400~540 ms 튀는 표본이 L1 강조 시나리오들에 있었다(강조 없음 0개, Enter 시나리오는 p99 가 486 ms 이므로 약 1% 이상). 입력처리·레이아웃은 정상이고 렌더 쪽이 멈춘 표본이다. 측정 중 다른 레인 부하(CPU 최대 72%)·GPU/DWM 경합으로 보이나 원인은 확인하지 못했다. TSK-04-02 의 성능 시험에서 조용한 PC 로 다시 볼 것.

### 2.2 헤드리스 (참고값, 대표값 아님)
방법: Avalonia.Headless + Skia 실제 래스터(UseHeadlessDrawing=false), `KeyTextInput`/`KeyPress` 주입 → `RunJobs` → `ForceRenderTimerTick` → `CaptureRenderedFrame`. 표본 300개(예열 50개).

| 시나리오 | p50 | p95 | p99 | max | 입력처리 p95 | L1 렉싱 p95 | 재렉싱 줄 max |
|---|---|---|---|---|---|---|---|
| 빈 프레임(기준선) | 3.72 | 4.68 | 5.19 | 5.45 | 0.00 | - | - |
| 문자 입력/BS, 강조 없음 | 29.42 | 47.65 | 49.33 | 51.38 | 43.55 | - | - |
| Enter/BS, 강조 없음 | 23.35 | 28.38 | 31.98 | 35.09 | 25.79 | - | - |
| 문자 입력/BS, L1 강조 | 24.23 | 34.78 | 42.56 | 46.52 | 32.10 | 0.01 | 1 |
| Enter/BS, L1 강조 | 22.97 | 37.03 | 44.39 | 51.12 | 34.35 | 0.02 | 2 |
| '/*' 열기·닫기, L1 강조 | 23.40 | 36.76 | 45.25 | 46.74 | 34.10 | 0.01 | 14 |
| 문자 입력/BS, L1 + 커서 50개 | 24.98 | 41.66 | 44.87 | 59.20 | 39.32 | 0.00 | 1 |

헤드리스는 **입력 주입 경로 자체가 25~45 ms** 로 비용 대부분을 차지한다(같은 AvaloniaEdit 코드가 실제 창에서는 1~2 ms). `diag` 로 나눠 보면 문서 변경 뒤 디스패처의 Render·Loaded 우선순위 작업이 수십 ms 이고, 문서 크기·한글 유무·검색 패널과 무관했다. 헤드리스 플랫폼 쪽 오버헤드로 보고, **헤드리스 수치로 16 ms 판정을 하지 않는다.** 회귀 감시용 상대값으로만 쓸 수 있다.

## 3. e2 다중 커서 (T-D03)

시제품 `MultiCaret/MultiCaretController.cs`(약 190줄). **AvaloniaEdit 를 포크하지 않고 공개 확장점만 사용**:
- 보조 커서 = `TextDocument.CreateAnchor`(AfterInsertion) → 문서 편집에 따라 자동 이동
- 그리기 = `IBackgroundRenderer`(KnownLayer.Caret), `TextView.GetVisualPosition`
- 입력 = TextArea 의 `TextInputEvent`/`KeyDownEvent` 를 터널 단계에서 가로채 모든 커서에 같은 편집을 `BeginUpdate/EndUpdate` 한 번으로 적용 → **undo 1단계**
- 구현: 문자 입력, Backspace, Delete, Enter, Esc(해제), Ctrl+Alt+↑/↓(위·아래 줄 추가), Alt+클릭(추가), 겹친 커서 합치기, L1 강조와 함께 동작
- 검증(selftest): 커서 3개에 "XY" 입력·Backspace·undo 단위·Esc 해제 확인
- 성능: 커서 50개 작업 합 p95 14.2 ms(2.1절) — 16 ms 안. 레이아웃 p95 8 ms 가 커서 수에 따라 늘어나므로 수백 개는 최적화 필요(현재는 매 입력 `AllOffsets` 정렬·`BringCaretToView`).

미구현(본 구현에서 할 일): 커서별 선택 영역과 그 편집, 커서별 클립보드 복사·붙여넣기, ←→/Home/End/단어 이동, IME 조합 입력, 커서 깜빡임, AvaloniaEdit 내장 열 선택(Alt+드래그 RectangleSelection)과의 전환.

판단: **확장으로 가능하다.** 위험은 AvaloniaEdit 내부 키 처리(EditingCommandHandler·CaretNavigationCommandHandler)와 이벤트 순서 충돌이며, 이동·선택을 넣으려면 해당 명령을 우리 쪽에서 다시 구현해야 한다(추정 2~4일).

## 4. e3 L1 렉서 (손 토크나이저 + 증분)

시제품 `L1/L1Lexer.cs`(줄 단위, 줄 시작 상태 = Normal/블록 주석/힌트/문자열/q-quote(닫는 구분자 포함)/따옴표 식별자), `L1/IncrementalLexer.cs`(바뀐 줄부터 다시 렉싱, 편집 범위를 지나 줄 끝 상태가 예전과 같아지면 멈춤), `L1/L1Colorizer.cs`(AvaloniaEdit `DocumentColorizingTransformer` 연결, 상태가 번진 줄만 `Redraw`).
검증: 무작위 편집 400회(줄 안 삽입·분할·합치기, 주석/따옴표 조각 포함) 뒤마다 증분 결과 = 전체 재렉싱 결과(토큰·상태 전부 일치).

BenchmarkDotNet (1만 줄, 20회 반복):

| 벤치 | Mean | P95 | Max | 할당 |
|---|---|---|---|---|
| 전체 렉싱 1만 줄 | 1,751 µs | 2,022 µs | 2,282 µs | 1.47 MB |
| 증분: 문자 1개 입력 | 0.29 µs | 0.33 µs | 0.34 µs | 264 B |
| 증분: Enter(줄 분할·합치기) | 2.38 µs | 2.62 µs | 2.62 µs | 308 B |
| 증분: '/*' 열기(다음 `*/` 까지 전파) | 1.17 µs | 1.29 µs | 1.31 µs | 1 KB |
| 최악: 1번 줄 '/*', 닫는 `*/` 없음(끝까지 전파) | 1,044 µs | 1,126 µs | 1,144 µs | 0.8 MB |

UI 안에서 측정한 L1 렉싱 p95 = 0.01~0.02 ms, '/*' 입력 때 다시 렉싱한 줄 최대 14줄(2.2절). → **최악(문서 전체 재렉싱)도 2 ms 안이라 지연 렉싱 없이 즉시 렉싱해도 된다.** TRD 4.5 L1 목표(16 ms) 대비 여유가 크다.

## 5. e3 ANTLR PL/SQL (L2)

- Java 있음(GraalVM JDK 21.0.7) → 문법 생성 성공(16 s). 생성물 PlSqlParser.cs 7.9 MB + PlSqlLexer.cs 1.1 MB. 이 라이브러리 컴파일만 2~5분(부하 따라) → 별도 프로젝트로 떼어 둠(`PlSqlAntlr`), 생성물은 커밋하지 않음.
- 파싱 전략: SLL + BailErrorStrategy, 실패 시 LL 재파싱. 합성 패키지는 **SLL 로 모두 통과, 구문 오류 0, 토큰 122,060개**.

콜드(새 프로세스마다 첫 파싱, 5회):

| 항목 | 값(ms) |
|---|---|
| 첫 파싱: 프로시저 1개(~50줄) | 2,117 / 2,630 / 2,076 / 1,922 / 2,069 → **중앙값 2,076** |
| 그 뒤 같은 프로시저 2~10번째 | 1.5~3.8 |
| 첫 파싱: 1만 줄 패키지 | 2,653 / 2,481 / 2,449 / 2,263 / 2,635 → **중앙값 2,481** |
| 그 뒤 패키지 2~5번째 | 244~733 |
| 패키지 파싱 뒤 프로시저 1개 | 0.35~4 (가끔 30~47: GC) |
| 작업 집합 | 프로시저만 106 MB, 패키지 파싱 뒤 191~214 MB |

웜(BenchmarkDotNet Monitoring, 10회): 프로시저 1개 **1.22 ms**(P95 1.78), 1만 줄 패키지 **159 ms**(P95 230, 할당 64 MB/회).

해석: 첫 파싱 2초는 입력 크기가 아니라 ATN 역직렬화·거대 파서 JIT·DFA 캐시 채우기 비용이다(50줄과 1만 줄의 첫 파싱 차이 0.4 s). 앱 시작 직후 백그라운드에서 짧은 문장으로 한 번 파싱해 데워 두면 사용자는 보지 않는다. 데운 뒤 "바뀐 문장만"(TRD 4.5) 파싱하면 1~2 ms 라 300 ms 디바운스 안에 충분하다. 전체 파일 재파싱(160~230 ms)은 피한다. ReadyToRun 으로 JIT 비용을 줄이는 효과는 재지 않았다(T-D07 측정 때 함께 볼 것).

## 6. 결정 후보

- **D-05 (에디터 컴포넌트)**: Avalonia.AvaloniaEdit **12.0.0**(MIT) + 자체 L1 렉서(줄 상태 증분) 로 확정. 1만 줄 문서에서 키 입력 처리 p95 4.6~6.2 ms(실제 창, 렌더 포함, 틱 대기 제외). 11.4.1 은 Avalonia 12 에서 실행 불가라 쓰지 않는다. 유료 컴포넌트 불필요.
- **T-D03 (다중 커서)**: AvaloniaEdit 포크 없이 확장(앵커+배경 렌더러+터널 입력 처리)으로 구현한다. 1.0 에는 입력·삭제·Enter·Alt+클릭·Ctrl+Alt+↑/↓ 와 내장 열 선택(Alt+드래그)을 넣고, 커서별 선택·이동·클립보드는 같은 확장 위에 후속으로 붙인다(EDIT-07 은 P0 유지 가능, 범위만 위처럼 나눔). 커서 50개까지 p95 14 ms.
- **TRD 4.5 L1/L2 수치 보강**: L1 즉시 증분 렉싱(최악 전체 재렉싱 1.1 ms). L2 ANTLR 은 시작 시 백그라운드 예열(콜드 첫 파싱 약 2.1~2.5 s), 이후 문장 단위 1~2 ms. 수락 기준 "키 입력 반영 16 ms" 는 "렌더 틱 대기를 뺀 처리 시간 p95" 로 정의한다(60 Hz 틱 대기 포함 시 p95 21~23 ms).

## 7. 보류·후속

- wbs TSK-01-05 기술 스펙의 AvaloniaEdit 11.4.1 → 12.0.0 수정(조정자, docs).
- 400~540 ms 튀는 렌더 표본(시나리오당 약 1%) 원인 미확인 — TSK-04-02 성능 시험에서 조용한 PC 로 재확인.
- 실제 OS 키 입력 경로(SendInput) 포함 측정과 고 DPI(150~250%) 측정은 하지 않았다.
