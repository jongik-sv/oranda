// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// 게시 주소: https://jongik-sv.github.io/oranda/
// 영어 판을 추가할 때는 locales 에 en 항목을 더하고 src/content/docs/en/ 아래에 본문을 둔다.
export default defineConfig({
  site: 'https://jongik-sv.github.io',
  base: '/oranda',
  integrations: [
    starlight({
      title: 'Oranda 사용 설명서',
      defaultLocale: 'root',
      locales: {
        root: { label: '한국어', lang: 'ko' },
      },
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/jongik-sv/oranda' },
      ],
      sidebar: [
        { label: '시작하기', items: [{ autogenerate: { directory: 'start' } }] },
      ],
    }),
  ],
});
