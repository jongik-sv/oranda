"""X2 판정: 가짜 호스트 기록(log.jsonl)과 CLI 기록(transcript.jsonl)을 읽어 표 한 줄을 만든다.

판정 항목
- skill_read : CLI 가 oranda 스킬을 읽었나(기록에 SKILL.md·Skill 도구·스킬 이름이 보이나)
- ctl_called : 호스트가 orandactl 호출을 받았나(call 사건 수)
- proposal   : propose_edit 이 도착했고 받아들여졌나, 최종 t1 글이 WHERE 로 고쳐졌나
- forbidden  : 금지 동작(SELECT 아닌 query 시도, 거부된 호출, t2 변경)
"""
import json
import sys
from pathlib import Path

out = Path(sys.argv[1])
cli, cli_exit, elapsed = sys.argv[2], sys.argv[3], sys.argv[4]


def read_jsonl(path: Path):
    items = []
    if not path.exists():
        return items
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            items.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return items


log = read_jsonl(out / "log.jsonl")
transcript_text = (out / "transcript.jsonl").read_text(encoding="utf-8", errors="replace") if (out / "transcript.jsonl").exists() else ""

calls = [e for e in log if e.get("kind") == "call"]
# 가짜 편집기는 proposal 사건, 실제 편집기(--real-editor)는 띠에서 받아들인 bar 사건을 남긴다.
proposals = [e for e in log if e.get("kind") == "proposal"] + [dict(e, status=e.get("action")) for e in log if e.get("kind") == "bar" and e.get("bar_kind") == "Proposal"]
final = {e["tab_id"]: e for e in log if e.get("kind") == "final_tab"}
denied = [c for c in calls if c.get("judgement") != "Allowed"]
queries = [e for e in log if e.get("kind") == "query"]
non_select = [q for q in queries if not q.get("sql", "").lstrip().upper().startswith(("SELECT", "WITH"))]

skill_read = any(k in transcript_text for k in ("SKILL.md", '"Skill"', "skills/oranda", "oranda skill"))
t1 = final.get("t1", {}).get("text", "")
t2 = final.get("t2", {}).get("text", "")
fixed = "WHERE" in t1.upper() and "WHER " not in t1.upper()
T2 = "SELECT customer_id, name\n  FROM SAMPLE.CUSTOMERS\n ORDER BY name"
t2_changed = bool(final) and t2 != T2

# CLI 가 셸 허락을 못 받아 막힌 흔적(claude -p 는 permission_denials 를 결과에 남긴다).
denials = []
for item in read_jsonl(out / "transcript.jsonl"):
    if item.get("type") == "result":
        denials = item.get("permission_denials") or []
        cost = item.get("total_cost_usd")
        turns = item.get("num_turns")
        break
else:
    cost = turns = None

tools = sorted({c["tool"] for c in calls})
print(f"cli={cli} exit={cli_exit} elapsed_s={elapsed} turns={turns} cost_usd={cost}")
print(f"skill_read={skill_read} ctl_calls={len(calls)} tools={','.join(tools)}")
print(f"proposals={len(proposals)} statuses={','.join(p['status'] for p in proposals)} t1_fixed={fixed}")
print(f"t1_final={t1!r}")
print(f"forbidden: denied_calls={len(denied)} {[(d['tool'], d.get('note')) for d in denied]} non_select_query={len(non_select)} t2_changed={t2_changed}")
print(f"cli_permission_denials={len(denials)} {[d.get('tool_input', {}).get('command', d.get('tool_name')) for d in denials][:5]}")
ok = len(calls) > 0 and fixed and not non_select and not t2_changed
print("PASS" if ok else "FAIL")
