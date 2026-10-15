# Readserene website

The marketing site for Readserene: a single page (`index.html`) plus `privacy.html` and `terms.html`. It's built with Astro as a static site with plain CSS and a few small vanilla scripts (platform detection, mobile menu, pricing toggle and reading demo).

## Develop

You need Node 22.12 or later.

```bash
npm install
```

```bash
npm run dev
```

The dev server runs at http://localhost:4321.

```bash
npm run build
```

`npm run build` regenerates `public/og.png` and the PNG favicons from `scripts/generate-images.mjs`, then writes the static site to `dist/`. To check the production build locally, run `npm run preview`.

## Deploy to Cloudflare Pages

```bash
npm run build
```

```bash
npx wrangler pages deploy dist
```

The first deploy asks you to sign in and pick or create a Pages project. To deploy from Git instead, connect the repository in the Cloudflare dashboard with the build command `npm run build`, output directory `dist`, and root directory `website`. The `.nvmrc` file tells Pages to build with Node 22.

`public/_headers` sets long cache lifetimes for fonts and hashed assets. Cloudflare Pages serves `privacy.html` at `/privacy`, which is the URL the site links to.

## Change links, prices and text

Everything you're likely to change is in `src/site.config.ts`. Values that still need confirming are marked `// TODO`.

- **Name, tagline, contact email, GitHub link and domain:** `name`, `tagline`, `contactEmail`, `githubUrl` and `url`. `url` is used for canonical links, Open Graph tags, the sitemap and `robots.txt`.
- **Platforms:** each entry in `platforms` has `available`, `url`, `label`, `minOS`, `version`, `size` and an optional `notifyUrl`, `note` or `alt` link. When Windows or iPhone ships, set `available: true` and fill in `url` and `label`. The hero button, the download card and the structured data all update from that.
- **Prices:** `pricing.free.price`, `pricing.sync.monthly` and `pricing.sync.yearly`. The yearly saving is calculated from these. While `pricingIsPlaceholder` is `true`, the page shows a "Prices are placeholders" note under the cards; set it to `false` once prices are final.
- **FAQ:** edit the `faq` list. Answers are plain text.

The privacy and terms text is in `src/pages/privacy.astro` and `src/pages/terms.astro`. Both are templates and need review before launch.

## Structure

```
src/site.config.ts       product facts, links, prices, FAQ
src/layouts/Base.astro   <head>, SEO tags, platform detection, scroll reveal
src/components/          Header, Hero, SyncStrip, Features, ReadingDemo, Pricing, Download, Faq, Footer
src/pages/               index, privacy, terms, sitemap.xml, robots.txt
src/styles/global.css    design tokens and shared styles
public/fonts/            self-hosted Inter and Literata (latin subset)
scripts/generate-images.mjs  og.png and favicon PNGs
```
