# MyBatis 골든 정답 (TSK-02-02)

동적 SQL 계산기(MyBatisEvaluator·OgnlLite)가 MyBatis 3.5 실제 출력과 같은지 확인하는 정답 텍스트다.
MyBatis 3.5.19 jar(Maven Central, OGNL 내장)를 JDK 17 로 돌려 `MappedStatement.getBoundSql(값)` 결과만 뽑았다. DB 접속은 없다.
생성 프로그램(Java)과 jar 는 저장소에 넣지 않는다.

## 파일

- `<표본>.golden.json`: 매퍼 하나의 문장 × 값 묶음 정답
  - `EmpMapper`·`WithBom` 등은 `Oranda.Testing.MyBatis.MapperSamples` 의 같은 이름 표본이다. `mapperSha256` = 표본 글자(UTF-8, BOM 없음)의 sha256 이다. 표본이 바뀌면 시험이 알려 주고 골든을 다시 만든다.
  - `TagSamples` 는 이 폴더의 `TagSamples.xml`(태그·경계 표본)이다.
  - MyBatis 는 매퍼를 DTD 로 검증하므로 DOCTYPE 이 없는 표본은 그대로는 읽지 못한다. 그 오류를 `strictMapperError` 에 남기고, 표준 DOCTYPE 을 끼워 다시 읽은 결과를 `cases` 에 둔다(`doctypeInjected`).
- `Ognl.golden.json`: test 식 표. 식마다 `<if test="식">` 하나로 계산한 결과(`true`·`false`·`error`)와 오류 문구.
- `Bind.golden.json`: bind value 식 표. 식마다 `<bind name="x" value="식"/>` 로 묶인 값과 Java 형(`result`), `String.valueOf` 글자(`javaString`), 오류 문구.
  Oranda 는 계산하면 같은 값·형·글자를 내야 하고, 같게 못 하면(BigInteger 결과) 계산 못 함으로 알린다.

## 값 표기

`{"t": 형, "v": 값}`. 형 = `null`·`string`·`int`·`long`·`decimal`(글자)·`double`·`bool`·`char`·`list`·`map`.

## 비교 규칙

- Oranda 의 `:이름` 바인드를 `?` 로 바꾸고, 양쪽 공백 덩어리를 공백 하나로 줄이고 앞뒤를 자른 뒤 `normalizedSql` 과 비교한다.
- 바인드 값은 `params[].value` 와 순서대로 비교한다. `property`(`__frch_id_0` 등)는 MyBatis 내부 이름이라 비교하지 않는다.
- `error` 가 있으면 MyBatis 가 그 값으로 예외를 냈다는 뜻이다(foreach 컬렉션 null, bind 의 null 더하기 등).

## 다시 만들기

생성 프로그램은 `~/.coord/mybatis-ws-1023/lanes/mb-evaluator/golden-gen/` 에 있다(GoldenGen.java, 표본 추출용 dump.cs).

```
dotnet run dump.cs -- <표본 폴더>
java -Duser.language=en -cp mybatis-3.5.19.jar GoldenGen.java <표본 폴더> TagSamples.xml <출력 폴더>
```
