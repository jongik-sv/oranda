import { defineRouteMiddleware } from '@astrojs/starlight/route-data';
import { page } from './lib/links';

// 사이트 뿌리는 랜딩 페이지이므로, 설명서 머리글의 제목은 설명서 첫 화면으로 보낸다.
export const onRequest = defineRouteMiddleware((context) => {
  context.locals.starlightRoute.siteTitleHref = page('manual/');
});
