-- 표 다시 만들기: 지운 표가 휴지통에 남긴 객체 이름. 열 = OBJECT_NAME. 바인드: TBL(원래 표 이름).
-- Oracle 판과 달라진 점: Tibero 의 user_recyclebin 에는 operation 열이 없어(TM0b 3.7) 그 조건을 뺐다(휴지통 항목은 모두 DROP 이 만든다).
-- 휴지통 이름은 BIN$… 가 아니라 <계정>_TBL… 모양이고 그 이름으로 SELECT 는 안 된다(-8052). FLASHBACK TABLE … TO BEFORE DROP 에는 쓸 수 있다(TB4 IT).
-- id: copy.recreate_recycle_bin
-- dbms: tibero
-- min-version: 6
-- requires: user_recyclebin
-- license: none
SELECT object_name FROM user_recyclebin WHERE original_name = :TBL AND type = 'TABLE'
