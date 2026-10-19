# TSK-01-04 시작 시간·메모리 측정과 단축 방안 — 결과

- 측정일 2026-10-19, Windows 11 Pro 10.0.26300(Insider 빌드), .NET SDK 10.0.402 / 런타임 10.0.13, Avalonia 12.1.4.
- 이 PC 에는 사내 백신(AhnLab V3: `ASDSvc`, `V3UI`)이 돈다. macOS 는 장비가 없어 **보류**.
- 대상: `StartupProbe/` = `src/Oranda.App`(2eaf738) 복제본에 단계 표식만 더한 측정 전용 진입점. 같은 src 프로젝트(Core·Language·Oracle·Platform)를 참조해 게시 폴더 구성이 실제 App 과 같다. 실제 App 은 수정하지 않았다.
- 게시: Release, self-contained, win-x64(`publish.ps1`). 측정: `measure.ps1`(실행 전 워밍업 1회 버림 → N회, 중앙값). 최종 표는 모두 측정 잠금을 잡고 돌렸다(`run-locked.ps1`). 원자료 = `results/round*/runs.csv`.
- **N = 9 회 중앙값**(시험 기준은 5회). 첫 프레임 구간이 60 ms / 500 ms 두 갈래로 갈리는(bimodal) 현상이 있어 5회 중앙값이 흔들렸다(1차 5회 결과는 `results/round1`). 같은 사례의 회차 간 차이는 ±80 ms 안팎.
- 웜 캐시 기준이다. 게시 직후 첫 실행은 백신 검사로 `main` 이 2.5~2.9 s 까지 늘었다(콜드 스타트는 이 표에 없음).

## 표식(단계) 정의

`ORANDA_PERF stage=<이름> ms=<프로세스 StartTime 기준 ms>` — l0107-ci 의 App 훅(71ce5ee)과 같은 규약.

| 표식 | 시점 |
|---|---|
| module-init | 진입 어셈블리 모듈 초기화(관리 코드 첫 실행) |
| main | `Main` 첫 줄 |
| appbuilder | `BuildAvaloniaApp()` 반환(App 훅과 같은 위치) |
| app-init | `App.Initialize()` 진입 = Win32 플랫폼·렌더러 초기화 끝 |
| app-xaml | `App.axaml`(FluentTheme) 적재 끝 |
| setup | `AfterSetup` 콜백 |
| window-created | `new MainWindow` 끝(App 훅과 같은 의미) |
| window-opened | `Opened` 이벤트 |
| first-render | `Opened` 뒤 첫 `RequestAnimationFrame` 콜백(App 훅과 같은 시점) |
| first-frame | 두 번째 `RequestAnimationFrame` 콜백 = 첫 프레임이 한 번 그려진 뒤 |
| input-ready | `first-frame` 뒤 `DispatcherPriority.ApplicationIdle` 작업 실행 = 디스패처가 비어 입력을 바로 받는 시점 |

## s1. 단계별 분해 (round 4, 9회 중앙값, ms)

| 구간 | R2R(현재 설정) | R2R composite |
|---|---:|---:|
| 프로세스 시작 → module-init | 496 | 509 |
| → main | 3 | 3 |
| → appbuilder | 23 | 15 |
| → app-init (Win32 플랫폼 초기화) | **1031** | **1056** |
| → app-xaml (FluentTheme) | 22 | 26 |
| → window-created | 145 | 92 |
| → window-opened | 109 | 57 |
| → first-render (첫 rAF) | 2 | 1 |
| → first-frame (실제 첫 프레임) | **517** | **72** |
| → input-ready | 0.3 | 0.3 |
| **input-ready 합계** | **2349** | **1830** |
| Private Bytes (input-ready 시점) | 105 MB | 110 MB |

시작 시간 = 큰 덩어리 셋: ① `Main` 전 약 500 ms, ② Avalonia Win32 플랫폼 초기화 약 1.0 s, ③ (composite 가 아니면) 첫 프레임 약 0.5 s.

### ① Main 전 500 ms — 이 PC 환경 비용(추정 근거 있음)

| 사례 (round 3, 9회) | main |
|---|---:|
| hello(Avalonia 없는 self-contained R2R 콘솔) | 59~64 |
| hello + WinExe + 같은 app.manifest | 59~64 |
| hello + Avalonia·ODP.NET 패키지 참조(같은 deps.json·파일 수) | 64 |
| StartupProbe, `[STAThread]` | 499~524 |
| StartupProbe, `[STAThread]` 제거(NOSTA) | **79** (대신 플랫폼 초기화가 그만큼 늘어 input-ready 는 1768 로 같음) |

- 런타임 호스트 자체는 60 ms 다. 나머지 약 430 ms 는 **프로세스의 첫 STA/첫 창 초기화**다. hello 에서 따로 재면: `[STAThread]` 붙이면 main 470 ms, STA 스레드 하나 띄우는 데 430 ms, STA 없이 첫 `CreateWindowEx(HWND_MESSAGE)` 한 번에 425 ms(두 번째 창은 0.2 ms). 셋이 같은 1회성 비용이다(STA 를 먼저 치르면 첫 창은 0.8 ms).
- 보통 PC 에서 첫 창 생성은 수 ms 수준이다. 그래서 이 425 ms 는 이 PC 고유 비용으로 본다. 후보는 백신(V3)의 창·후크 주입, 한국어 IME/TSF(ctfmon), Insider 빌드다. **다른 PC 에서 확인이 필요하다**(사람 자원).
- App 쪽에서는 줄일 수 없다. Avalonia 는 클립보드·끌어놓기(OLE)에 STA 가 필요해 `[STAThread]` 를 빼면 안 된다. 빼도 비용이 플랫폼 초기화로 옮겨 갈 뿐이다.

### ② Win32 플랫폼 초기화 약 1.0 s

- `dotnet-trace`(dotnet-sampled-thread-time) 표본에서 상위 항목은 `Win32Platform.CreateMessageWindow`(exclusive 25%), `WinUiCompositorConnection`(WinUI 합성 스레드) 생성·RunLoop, `EglDisplay.CreatePBufferFromClientBuffer`(ANGLE)다.
- 소프트웨어 렌더링(`Win32RenderingMode.Software`)에서도 이 구간은 약 980 ms 다. GPU 초기화가 주원인은 아니다.
- 원인은 아직 확정하지 못했다. 위 ①과 같은 첫 창 비용이 다른 스레드(합성 스레드 등)에서 한 번 더 드는지 확인이 필요하다. 다음 단계로 ETW(PerfView/WPR) 네이티브 스택을 다른 PC 와 비교하는 것을 제안한다.

### ③ 첫 프레임 0.5 s — ReadyToRun composite 로 사라짐

- 일반 R2R 의 `first-render → first-frame` 은 9회 중 8회 480~550 ms, 1회 60~70 ms 였다(round 2). composite 는 9회 모두 45~73 ms, 소프트웨어 렌더링은 30~41 ms 였다.
- composite 가 왜 이 구간을 없애는지는 확정하지 못했다(렌더 스레드 GPU 경로의 첫 실행 비용으로 추정).

### App 훅(71ce5ee)의 first-render 판정

- App 훅은 `Opened` 뒤 **첫** `RequestAnimationFrame` 콜백에서 `first-render` 를 찍는다. 이 콜백은 그 렌더 틱의 그리기 **전에** 돈다. 실제 첫 프레임보다 이르다.
- 차이는 현재 설정(R2R)에서 중앙값 **약 490 ms**(60 ms / 500 ms 두 갈래)이고, composite 에서는 약 55 ms 다. 따라서 **현재 App 훅의 first-render 는 실제 첫 프레임과 맞지 않는다**(시작 시간을 최대 0.5 s 적게 잰다).
- 두 번째 rAF 콜백에서 찍도록 바꾸는 것을 제안한다(아래 「App 적용 제안」). 그러면 이 스파이크의 `first-frame` 과 같아진다. `input-ready` 는 그 뒤 0.3 ms 라 따로 둘 필요가 없다.

## s2. 설정별 효과 (round 2, 9회 중앙값, CPU 8~23%)

| 사례 | 설정 | input-ready ms | Δ vs R2R | Private MB | 게시 폴더 |
|---|---|---:|---:|---:|---|
| jit | ReadyToRun 끔 | 2439 | +163 | 112.5 | 213 MB / 241 파일 |
| **r2r** | ReadyToRun (현재 TRD 설정) | **2276** | 0 | 105.3 | 235 MB / 241 |
| r2r-trim | + PublishTrimmed | 2287 | +11 (효과 없음) | 104.1 | **183 MB / 108** |
| **r2r-composite** | + PublishReadyToRunComposite | **1670** | **−606** | 109.8 | 266 MB / 242 |
| r2r-nointer | Inter 글꼴 끔(시스템 글꼴) | 2329 | +53 (효과 없음) | 103.1 | — |
| r2r-composite-nointer | composite + Inter 끔 | 1715 | −561 | 108.1 | — |
| r2r-software | 소프트웨어 렌더링 | 1799 | −477 | **30.1** | — |
| r2r-software-nointer | 소프트웨어 + Inter 끔 | 1794 | −482 | 28.2 | — |
| r2r-composite-software-nointer | composite + 소프트웨어 + Inter 끔 | 1675 | −601 | 33.3 | — |
| r2r-eager-oracle | 시작 경로에서 ODP.NET 적재(지연 로딩 안 함) | 2304 | +28 (window-created +160, ODP.NET 적재 자체 약 210 ms) | 106.4 | — |
| r2r-nopgo | `DOTNET_TieredPGO=0` | 2281 | +5 (효과 없음) | 105.1 | — |

추가 사례(round 5, 9회): 세계화(globalization) 설정과 합성 모드.

| 사례 | input-ready ms |
|---|---:|
| r2r / r2r-usenls(NLS) / r2r-invariant | 2308 / 2178 / 2327 |
| r2r-composite / composite-usenls | 1826 / 1747 |
| r2r-redirection(WinUI 합성 끔) / composite-redirection | 2132 / 2051 (composite 에서는 오히려 +225, 메모리 +6 MB) |

- 세계화 설정은 측정 오차(±80 ms) 안이다. 효과를 주장하지 않는다.
- 회차 간 같은 사례 비교: composite 는 1670 / 1695 / 1760 / 1830 / 1826 ms, R2R 는 2276 / 2335 / 2348 / 2308 ms. composite 의 약 500 ms 단축은 모든 회차에서 재현됐다.

정리:
- **ReadyToRun composite 가 유일하게 큰 효과다(−0.5~0.6 s).** 비용은 게시 폴더 +30 MB, Private +5 MB, 게시 시간 증가다.
- 트리밍은 시간 효과가 없고 크기만 −52 MB 줄인다. 경고는 ViewLocator IL2026 1건, ODP.NET IL2104 1건이다.
- Inter 글꼴 유무와 TieredPGO 는 효과가 없다.
- ODP.NET 지연 로딩은 지킬 가치가 있다. 시작 경로에서 ODP.NET 을 건드리면 창 생성이 약 160~210 ms 늦어진다.
- 소프트웨어 렌더링은 메모리 −75 MB(Private 105 → 30 MB)다. 시간은 composite 와 같이 쓰면 이득이 없다. 큰 결과 그리드·에디터 스크롤 성능이 확인되지 않아 기본값으로는 권하지 않는다(메모리 절약 옵션 후보).

## s3. NativeAOT (T-D07 자료)

- `PublishAot=true` 게시는 **링크 단계에서 실패**했다. 이 PC 에 Visual Studio C++ 빌드 도구(link.exe)가 없다. `IlcUseEnvironmentalTools=true` 로 ILC 컴파일(경고 산출)까지는 끝냈다. AOT 실행 시간은 **측정 못 함**(사람 자원: VS "Desktop development with C++" 설치 또는 설치된 PC).
- 경고 수(`TrimmerSingleWarn=false`, 고유 메시지 기준): **54건**.

| 출처 | 건수 | 코드별 |
|---|---:|---|
| Oracle.ManagedDataAccess (23.26.301) | 53 | IL2026 20, IL3050 12, IL2075 7, IL2080 4, IL2057 2, IL2065 2, IL2093 2, IL3000 2, IL2046 1, IL2122 1 |
| Oranda.App (ViewLocator 의 `Type.GetType`) | 1 | IL2026 1 |
| Avalonia 12.1.4 / CommunityToolkit.Mvvm | **0** | — |

- 기본(요약) 모드에서는 5건이다(IL2104·IL3053 각 1, IL3000 2, IL2026 1).
- 이 숫자는 현재 App 이 실제로 부르는 ODP.NET 코드만 센 것이다(연결 문자열 빌더). 접속·조회 코드가 붙으면 늘어난다.
- 판단 자료: UI 스택(Avalonia·MVVM)은 AOT 경고가 0 이다. 걸림돌은 **ODP.NET**(리플렉션·System.Text.Json·Assembly.Load) 하나다. ViewLocator 는 컴파일된 매핑으로 바꾸면 0 이 된다.

## s4. 1.5 s 목표 판단

- **이 PC 에서 입력 가능 1.5 s 이하를 달성한 설정은 없다.** 최선은 R2R composite 1.67~1.83 s(회차별 중앙값)다.
- 이 중 약 0.43 s 는 App 과 무관한 이 PC 의 첫 STA/첫 창 비용이다(Avalonia 없는 hello 도 같은 비용을 낸다). 이를 빼면 composite 는 약 1.24~1.40 s 다. 깨끗한 PC 에서는 1.5 s 달성 가능성이 높지만 **미확인**이다.
- 대안 목표(제안):
  1. 시작 예산을 둘로 나눈다. ⓐ **프로세스 내부 구간(main → input-ready) 1.3 s 이하**(App 이 책임질 몫, 이 PC 의 composite 실측 1.17~1.32 s), ⓑ 전체(프로세스 시작 → input-ready)는 기준 PC 에서 1.5 s 이하. 백신·IME 가 있는 사내 PC 는 1.9 s 이하를 허용한다.
  2. 기준 PC(보안 SW 가 없는 Windows 10/11, 같은 사양)를 정해 1.5 s 를 다시 잰다. 거기서도 미달이면 ②(Win32 플랫폼 1.0 s)를 Avalonia 이슈로 올리고 PRD 목표를 재협의한다.
- 측정 지표: CI·성능 시험은 `first-render`(첫 rAF)가 아니라 **실제 첫 프레임(두 번째 rAF) 또는 input-ready** 로 판정한다.

## App 적용 제안 (diff — 실제 App 에는 넣지 않았다)

```diff
--- a/src/Oranda.App/Oranda.App.csproj
+++ b/src/Oranda.App/Oranda.App.csproj
@@
   <PropertyGroup>
     <OutputType>WinExe</OutputType>
     <ApplicationManifest>app.manifest</ApplicationManifest>
   </PropertyGroup>
+
+  <!-- TSK-01-04: 게시본 시작 단축. composite 는 self-contained 에서만 동작(게시 시 -r win-x64 --self-contained).
+       R2R 대비 input-ready −0.5~0.6 s, 게시 폴더 +30 MB. 트리밍은 시간 효과가 없어 넣지 않는다. -->
+  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
+    <PublishReadyToRun>true</PublishReadyToRun>
+    <PublishReadyToRunComposite>true</PublishReadyToRunComposite>
+  </PropertyGroup>
```

```diff
--- a/src/Oranda.App/Views/MainWindow.axaml.cs
+++ b/src/Oranda.App/Views/MainWindow.axaml.cs
@@
     private void OnOpenedForPerf(object? sender, EventArgs e)
     {
         Opened -= OnOpenedForPerf;
-        RequestAnimationFrame(_ => PerfMarks.Mark("first-render"));
+        // 첫 rAF 콜백은 첫 프레임을 그리기 *전* 이다(R2R 에서 실제 프레임보다 약 0.5 s 이름, TSK-01-04).
+        // 두 번째 콜백 = 첫 프레임이 한 번 그려진 뒤.
+        RequestAnimationFrame(_ => RequestAnimationFrame(_ => PerfMarks.Mark("first-render")));
     }
```

지연 로딩 규칙(코드 변경 아님): 시작 경로(`App.OnFrameworkInitializationCompleted`·`MainWindow` 생성자·`MainViewModel` 생성자)에서 `Oranda.Oracle`/ODP.NET 형식을 건드리지 않는다. 접속 창을 열 때 처음 적재한다.

## decisions 후보

1. 게시본은 ReadyToRun **composite**(self-contained, win-x64)로 낸다. 트리밍·Inter 글꼴 제거·TieredPGO 끄기는 시작 시간 효과가 없어 쓰지 않는다. ODP.NET 은 시작 경로 밖에서 지연 로딩한다.
2. 1.5 s 목표는 「기준 PC 전체 1.5 s + 프로세스 내부(main → input-ready) 1.3 s」로 나눈다. 사내 보안 SW PC 는 1.9 s 까지 허용한다. 판정 지표는 실제 첫 프레임(두 번째 rAF) 또는 input-ready, 9회 중앙값이다.
3. T-D07: 1.0 은 ReadyToRun composite 를 유지한다. NativeAOT 는 UI 스택 경고 0, ODP.NET 경고 53 이다. ODP.NET 이 AOT 호환을 내기 전에는 보류한다.

## 재현

```powershell
pwsh spikes/TSK-01-04/publish.ps1                       # out/<변형> 게시 (aot 는 MSVC 필요)
pwsh spikes/TSK-01-04/run-locked.ps1 -Runs 9 -Cases r2r,r2r-composite   # 잠금 잡고 측정 → results/
```

환경 변수 변형은 `ORANDA_PROBE_FONT=none`, `ORANDA_PROBE_RENDER=software`, `ORANDA_PROBE_COMPOSITION=redirection`, `ORANDA_PROBE_ORACLE=eager`, `ORANDA_PROBE_EXIT_AT_MAIN=1` 이다. `HelloFloor/` 는 바닥값(런타임·STA·첫 창 비용)을 재는 콘솔이다.
