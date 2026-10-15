// Generates og.png and PNG favicons into public/ from inline SVG.
// Runs before every build (see package.json), so changing the name in
// src/site.config.ts is enough to refresh the share image.
import { readFile, writeFile } from 'node:fs/promises';
import sharp from 'sharp';

const root = new URL('../', import.meta.url);
const config = await readFile(new URL('src/site.config.ts', root), 'utf8');
const name = config.match(/name:\s*'([^']+)'/)?.[1] ?? 'Readserene';

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');

// Book-page motif: an open page of justified lines on Sepia paper.
const lines = Array.from({ length: 13 }, (_, i) => {
  const w = i === 5 || i === 12 ? 170 : 300;
  const indent = i === 6 ? 28 : 0;
  return `<rect x="${768 + indent}" y="${178 + i * 24}" width="${w - indent}" height="7" rx="3.5" fill="#3B2F24" opacity=".18"/>`;
}).join('');

const og = `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630" viewBox="0 0 1200 630">
  <rect width="1200" height="630" fill="#FFFFFF"/>
  <g transform="rotate(-3 900 315)">
    <rect x="726" y="96" width="384" height="460" rx="14" fill="#F5ECD9" stroke="#E6DAC2" stroke-width="2"/>
    <rect x="841" y="130" width="154" height="10" rx="5" fill="#3B2F24" opacity=".28"/>
    ${lines}
    <text x="918" y="526" text-anchor="middle" font-family="Georgia, serif" font-size="20" fill="#3B2F24" opacity=".6">187</text>
  </g>
  <g transform="translate(96 252)">
    <path transform="scale(2.6)" d="M12 6.5C10 5 7.2 4.5 4 4.75v13.5c3.2-.25 6 .25 8 1.75 2-1.5 4.8-2 8-1.75V4.75C16.8 4.5 14 5 12 6.5Z M12 6.5V20" fill="none" stroke="#7A5A3A" stroke-width="1.5" stroke-linejoin="round"/>
    <text x="80" y="50" font-family="Inter, Helvetica Neue, Helvetica, Arial, sans-serif" font-size="68" font-weight="600" letter-spacing="-1.4" fill="#111111">${esc(name)}</text>
  </g>
  <text x="96" y="380" font-family="Inter, Helvetica Neue, Helvetica, Arial, sans-serif" font-size="28" fill="#4A4A4A">Pick up exactly where you left off.</text>
  <rect x="96" y="424" width="56" height="3" fill="#7A5A3A"/>
</svg>`;

const out = (f) => new URL(`public/${f}`, root).pathname;
await sharp(Buffer.from(og)).png({ compressionLevel: 9 }).toFile(out('og.png'));

const favicon = await readFile(new URL('public/favicon.svg', root));
await sharp(favicon, { density: 300 }).resize(32, 32).png().toFile(out('favicon-32.png'));
await sharp(favicon, { density: 600 })
  .resize(140, 140)
  .extend({ top: 20, bottom: 20, left: 20, right: 20, background: '#ffffff' })
  .png()
  .toFile(out('apple-touch-icon.png'));

console.log('Generated og.png, favicon-32.png, apple-touch-icon.png');
