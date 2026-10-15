import type { APIRoute } from 'astro';
import { site } from '../site.config';

const paths = ['/', '/privacy', '/terms'];

export const GET: APIRoute = () => {
  const urls = paths
    .map((p) => `  <url><loc>${new URL(p, site.url).href}</loc></url>`)
    .join('\n');
  const body = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls}
</urlset>
`;
  return new Response(body, { headers: { 'Content-Type': 'application/xml' } });
};
