#!/usr/bin/env sh
# PL/SQL ANTLR 파서 생성 (Java 필요). 산출물 PlSqlAntlr/Generated/ 는 커밋하지 않는다(git ignore).
# 사용: sh gen-antlr.sh <antlr-4.13.1-complete.jar 경로>
# 문법 출처: antlr/grammars-v4 sql/plsql @ 3036ab7a91d7b2ca2282ab6c3c9b945c0a92f8b0 (Apache-2.0)
# jar sha256: bc13a9c57a8dd7d5196888211e5ede657cb64a3ce968608697e4f668251a8487
set -e
JAR="$1"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HERE/Generated"
cd "$HERE/grammar"
java -jar "$JAR" -Dlanguage=CSharp -no-listener -no-visitor -o "$HERE/Generated" -Xexact-output-dir PlSqlLexer.g4 PlSqlParser.g4
