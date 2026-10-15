import { defineConfig } from 'astro/config';
import { site } from './src/site.config';

export default defineConfig({
  site: site.url,
  output: 'static',
  trailingSlash: 'ignore',
  build: { format: 'file', inlineStylesheets: 'always' },
});
