// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import remarkMissingImages from './plugins/remark-missing-images.mjs';

// 게시 주소: https://jongik-sv.github.io/oranda/
// 영어 판을 추가할 때는 locales 에 en 항목을 더하고 src/content/docs/en/ 아래에 본문을 둔다.
export default defineConfig({
  site: 'https://jongik-sv.github.io',
  base: '/oranda',
  markdown: { remarkPlugins: [remarkMissingImages] },
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
        { label: '설치', items: [{ autogenerate: { directory: 'install' } }] },
        { label: '접속', items: [{ autogenerate: { directory: 'connect' } }] },
        { label: '편집', items: [{ autogenerate: { directory: 'editor' } }] },
        { label: '실행과 결과', items: [{ autogenerate: { directory: 'run' } }] },
        { label: '계획과 튜닝', items: [{ autogenerate: { directory: 'plan' } }] },
        { label: '스키마와 PL/SQL', items: [{ autogenerate: { directory: 'schema' } }] },
        { label: '데이터 이동', items: [{ autogenerate: { directory: 'transfer' } }] },
        { label: '도구와 모니터', items: [{ autogenerate: { directory: 'tools' } }] },
        { label: '설정', items: [{ autogenerate: { directory: 'settings' } }] },
        { label: 'AI', items: [{ autogenerate: { directory: 'ai' } }] },
        { label: '도움말', items: [{ autogenerate: { directory: 'help' } }] },
      ],
    }),
  ],
});
