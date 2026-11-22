"""매뉴얼 쪽 목록(site/src/content/docs 머리말 → 제목·설명·주소).

- 가짜 docs: `orandactl docs` 가 아직 없을 때(S2 머지 전) run.sh 가 이 출력을 설치된 스킬 사본 끝에 붙인다.
- 판정: verdict.py 가 답에 나온 매뉴얼 주소가 실제 쪽인지 이 목록으로 본다.
사용법: python3 -I manual_index.py <저장소 루트>   → 마크다운 장 목록을 찍는다.
"""
import re
import sys
from pathlib import Path

BASE = "https://jongik-sv.github.io/oranda/"


def pages(repo: Path):
    """(주소, 제목, 설명) 목록. index.md 는 폴더 주소."""
    root = repo / "site" / "src" / "content" / "docs"
    items = []
    for path in sorted(root.rglob("*.md*")):
        if path.suffix not in (".md", ".mdx"):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        head = re.match(r"^---\n(.*?)\n---", text, re.S)
        meta = {}
        if head:
            for line in head.group(1).splitlines():
                m = re.match(r"^(title|description):\s*(.*)$", line)
                if m:
                    meta[m.group(1)] = m.group(2).strip().strip("'\"")
        rel = path.relative_to(root).with_suffix("").as_posix()
        rel = "" if rel == "index" else rel[: -len("/index")] + "/" if rel.endswith("/index") else rel + "/"
        items.append((BASE + rel, meta.get("title", rel), meta.get("description", "")))
    return items


def urls(repo: Path):
    return {u.rstrip("/") for u, _, _ in pages(repo)}


def markdown(repo: Path) -> str:
    lines = ["", "## Oranda manual (agent-e2e fake docs index)", "",
             "When the user asks how an Oranda feature works, answer with the matching page link from this list. Do not invent links.", ""]
    lines += [f"- [{t}]({u}) — {d}" for u, t, d in pages(repo)]
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    sys.stdout.write(markdown(Path(sys.argv[1])))
