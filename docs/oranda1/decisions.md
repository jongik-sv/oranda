# Decisions Log — project

> Append-only audit trail of autonomous decisions made during DDTR/feat/wbs cycles.
> Edit prior entries forbidden — record reversals as new entries instead.

## D-001 (2026-10-22T23:34:39Z)
- **Phase**: design
- **Decision needed**: MyBatis 매퍼를 Oranda 에서 편집·시험·저장하려면 화면과 저장 방식을 어떻게 둘지
- **Decision made**: 시안 A(문장 탭형). 매퍼 xml 이 정본. 문서 탭 하나 = 매퍼 하나, 왼쪽 「문장」 도구 창, 가운데 문장 원문 탭, 오른쪽 「파라미터 · 시나리오」, 아래 「펼친 SQL · 결과 · 조합 검사」. 저장은 바뀐 문장의 원문 범위만 바꿔 쓴다. 시나리오는 앱 데이터에 매퍼 경로 기준으로 둔다. 일반 작업 공간은 문서 탭 메뉴로 바꾸고 원래 파일은 그대로 둔다. 문서는 docs/oranda1 에 따로 두고 상위 PRD 는 고치지 않는다
- **Rationale**: 시안 B(파일 통째 편집형)는 커서로 문장을 정해야 하고 저장 때 파일 전체가 다시 쓰일 위험이 크다. A 는 지금의 작업 공간 구조(문서 탭 → SQL 탭, 탭별 바인드, 문서별 도구 창)를 넓히고 문장 단위 저장과 맞는다
- **Reversible**: yes
- **Source**: 사용자 결정 2026-10-23 「A가 좋아보이네」 「기존 PRD 문서는 놔두고 새로운 폴더 하나 만들어서 진행하자」, 시안 캔버스 https://claude.ai/artifact/JeoMQCGye8RCEqGmGVPKEa
