-- oranda.db schema v8 (AI 모델 나눠 쓰기, 설계 docs/oranda1/research/ai-expansion-draft-2026-11-18.md §1.2 「기록」)
-- agent_audit 에 요청 등급(ai_tier: 'Main'·'Light')과 실제로 보낸 모델 이름(ai_model)을 더한다. 옛 기록은 NULL(모름).
-- 마이그레이션 실행기가 버전(schema_version)으로 한 번만 적용하므로 ALTER TABLE 을 그대로 쓴다.

ALTER TABLE agent_audit ADD COLUMN ai_tier  TEXT;
ALTER TABLE agent_audit ADD COLUMN ai_model TEXT;
