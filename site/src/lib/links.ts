// 사이트 안 주소를 base(/oranda) 아래로 만든다.
const base = import.meta.env.BASE_URL.replace(/\/$/, '');

/** public 폴더의 파일처럼 설명서 본문이 아닌 주소. */
export const siteUrl = (path: string) => `${base}/${path}`;

/** 설명서 쪽 주소. page('…') 로 적은 주소는 ManualLandingTests 가 본문에 실제로 있는지 검사한다. */
export const page = (path: string) => siteUrl(path);
