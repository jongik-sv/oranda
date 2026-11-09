-- 표 다시 만들기: 지운 표가 휴지통에 남긴 객체 이름. 열 = OBJECT_NAME. 바인드: TBL(원래 표 이름).
-- id: copy.recreate_recycle_bin
-- min-version: 11
-- requires: user_recyclebin
-- license: none
SELECT object_name FROM user_recyclebin WHERE original_name = :TBL AND type = 'TABLE' AND operation = 'DROP'
