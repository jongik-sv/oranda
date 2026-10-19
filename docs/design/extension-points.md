# 확장 지점 사용 안내 — TSK-10-03

> 근거: PRD APP-09·9.4, TRD 4.10, [ui-contract.md](ui-contract.md) 2장, [commands.md](commands.md) 7장.
> 1.0 은 AI 기능을 만들지 않는다(D-04). 나중의 챗봇·플러그인이 **Core 를 고치지 않고** 붙을 자리만 연다.
> 실제로 붙는지는 개발 빌드의 「확장 시험 패널」(`oranda.panel.extensionTest`)이 보여 준다.

## 1. 쓸 수 있는 인터페이스

모두 `Oranda.Core` 에 있다. 확장은 이것만 쓴다.

| 인터페이스 | 하는 일 | 지켜지는 규칙 |
|---|---|---|
| `IEditorContext` | 활성 문서 읽기(`GetActiveDocument`), 커서 위치 문장(`GetCurrentStatement`), 제안 편집(`ProposeEditAsync`) | 제안은 **차이 미리보기에서 사용자가 받아들일 때만** 들어간다. 미리보기 사이에 문서가 바뀌면 넣지 않는다. 실행 기능은 없다 |
| `IExecutionService` | SQL 실행 | **정책 관문을 반드시 지난다**: 읽기 전용 거부, 운영 접속 확인, WHERE 없는 DML 확인, 위험 명령(DROP·TRUNCATE) 이름 다시 입력. `ExecutionOptions.Source = HistorySource.Extension` 으로 보낸다(이력에 출처가 남는다) |
| `IMetadataProvider` | 스키마·객체·열·DDL 읽기 | 읽기 전용 |
| `IOutboundConsent` | 외부로 보내기 전 동의 받기 | 보낼 내용을 그대로 보여 주고, `Denied` 면 보내지 않는다. 동의 서비스가 없으면 거절로 본다 |
| `IToolPanelRegistry` · `ICommandRegistry` | 실행 중 도구 창·명령 붙이기(`Register` → `IDisposable`) | 반환값 Dispose 로 뗀다. ID 가 겹치면 예외 |

## 2. 붙이는 순서

1. 도구 창 ViewModel 을 만든다(`ToolPanelViewModelBase` 상속). View 는 이름 규칙(`…ViewModel` → `…View`).
2. App 안 기능이면 `[OrandaToolPanel]`·`[OrandaCommand]` 특성만 단다. 바깥 어셈블리면 `IToolPanelRegistry.Register`·`ICommandRegistry.Register` 로 붙인다.
3. 생성자는 인자 없음과 `IServiceProvider` 둘만 둔다. 서비스는 **처음 쓸 때** `GetService` 로 꺼낸다(시작 때 Oracle·ANTLR 어셈블리를 깨우지 않기 위해, D-006).
4. 편집은 `ProposeEditAsync`, 실행은 `IExecutionService`, 외부 전송은 `IOutboundConsent` 를 거친다. 다른 길은 없다.

## 3. 시험 패널이 하는 일

| 버튼 | 쓰는 확장 지점 | 결과 |
|---|---|---|
| 읽기 | `IEditorContext` | 선택 영역(없으면 커서 위치 문장)을 읽고 고른 규칙(대문자·소문자·주석 머리)으로 제안을 만든다 |
| 제안 넣기 | `IEditorContext.ProposeEditAsync` | 차이 미리보기 → 승인해야 들어간다(실행 취소 1단계) |
| 현재 문장 실행 | `IExecutionService` | 정책 관문 판단(거부·확인 취소·성공)과 규칙 이름을 기록한다 |
| 테이블 목록 | `IMetadataProvider` | 현재 사용자 테이블 이름 최대 50개 |
| 외부 전송 시험 | `IOutboundConsent` | 받는 곳 `https://extension.invalid/assist`. 동의가 없거나 거절이면 막힌다. 1.0 은 동의해도 실제로 보내지 않는다 |

- 열기: 명령 `oranda.view.extensionTest`(활성 조건 `devBuild`). 릴리스 빌드에는 패널도 명령 활성도 없다.
- 시험: `tests/Oranda.App.Tests/Features/ExtensionTest/ExtensionTestTests.cs` — 실제 `EditorContext`, 실제 실행 엔진·정책 관문·확인 상호작용으로 운영 UPDATE 확인 대화상자, 읽기 전용 거부, DROP 다시 입력, 동의 없는 전송 차단을 단정한다.
