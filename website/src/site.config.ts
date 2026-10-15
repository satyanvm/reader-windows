// Everything a non-developer might need to change lives in this file:
// product name, links, platform availability, prices and FAQ text.
// The layout reads from here, so flipping a platform to `available: true`
// or changing a price needs no markup changes.

export type PlatformId = 'android' | 'mac' | 'windows' | 'iphone';

export interface Platform {
  id: PlatformId;
  name: string;
  /** Shown in the hero button when this platform is detected and available. */
  heroLabel: string;
  available: boolean;
  url: string;
  label: string;
  /** Optional second link, e.g. a direct APK next to Google Play. */
  alt?: { label: string; url: string };
  minOS: string;
  version?: string;
  size?: string;
  note?: string;
  /** Optional "Notify me" mailto link, shown while the platform is unavailable. */
  notifyUrl?: string;
}

export interface FaqItem {
  q: string;
  a: string;
}

const contactEmail = 'hello@example.com'; // TODO: confirm contact address

export const site = {
  name: 'Readserene',
  tagline: 'A calm place to read PDFs and EPUBs, in sync across your devices.',
  description:
    'Readserene is a calm, distraction-free reader for PDFs and EPUBs that remembers exactly where you stopped, on every device you sign in to.',
  url: 'https://readserene.pages.dev', // TODO: confirm production domain
  contactEmail,
  githubUrl: 'https://github.com/satyanvm', // TODO: confirm GitHub link
  launchYear: 2026,

  platforms: [
    {
      id: 'android',
      name: 'Android',
      heroLabel: 'Get it on Android',
      available: true,
      url: 'https://example.com/readserene-android', // TODO: Google Play listing URL
      label: 'Get it on Google Play',
      alt: { label: 'Download APK', url: 'https://example.com/readserene.apk' }, // TODO: APK URL
      minOS: 'Android 8+',
      version: '1.0.0', // TODO: confirm
      size: '18 MB', // TODO: confirm
    },
    {
      id: 'mac',
      name: 'Mac',
      heroLabel: 'Download for Mac',
      available: true,
      url: 'https://example.com/readserene.dmg', // TODO: real .dmg URL
      label: 'Download .dmg',
      minOS: 'macOS 14+',
      version: '1.0.0', // TODO: confirm
      size: '24 MB', // TODO: confirm
      note: 'Requires macOS 14 or later. Apple silicon and Intel.',
    },
    {
      id: 'windows',
      name: 'Windows',
      heroLabel: 'Download for Windows',
      available: false,
      url: 'https://example.com/readserene-setup.exe', // TODO: real installer URL
      label: 'Download for Windows',
      minOS: 'Windows 10/11',
      notifyUrl: `mailto:${contactEmail}?subject=Notify%20me%20about%20Readserene%20for%20Windows`,
    },
    {
      id: 'iphone',
      name: 'iPhone',
      heroLabel: 'Download on the App Store',
      available: false,
      url: 'https://example.com/readserene-ios', // TODO: App Store URL
      label: 'App Store',
      minOS: 'iOS 17+',
      notifyUrl: `mailto:${contactEmail}?subject=Notify%20me%20about%20Readserene%20for%20iPhone`,
    },
  ] satisfies Platform[],

  pricing: {
    pricingIsPlaceholder: true,
    currency: 'USD',
    symbol: '$',
    free: {
      name: 'Free',
      price: 0, // TODO: confirm
      caption: 'forever',
      features: [
        'Read any PDF or EPUB on one device',
        'Flowing text for PDFs',
        'Four reading themes',
        'One page or two',
        'Works offline',
      ],
    },
    sync: {
      name: 'Readserene Sync',
      monthly: 2, // TODO: confirm
      yearly: 18, // TODO: confirm
      recommended: true,
      features: [
        'Everything in Free',
        'Your place synced across all your devices',
        'Your library synced across all your devices',
        'Priority email support',
      ],
    },
    footnote: 'No ads. No tracking. Cancel anytime.',
  },

  // TODO: confirm FAQ wording, especially cancelling and data export.
  faq: [
    {
      q: 'Does it work offline?',
      a: 'Yes. Your books are stored on your device, so you can read without a connection. Your reading position syncs the next time you are online.',
    },
    {
      q: 'Do you upload my books?',
      a: 'Not unless you sign in to sync. Without an account, books stay on your device. When you sign in, Readserene stores your library files and reading positions so they can appear on your other devices. Text recognition for scanned pages always happens on your device.',
    },
    {
      q: 'Which formats?',
      a: 'PDF and EPUB. PDFs can be read as flowing text or as their original pages, and scanned pages are read with on-device text recognition.',
    },
    {
      q: 'How does sync decide which position wins?',
      a: 'The most recent reading wins. If you read further on another device, Readserene offers to take you there. It never jumps without asking.',
    },
    {
      q: 'Is there a Windows / iPhone version?',
      a: 'Both are coming soon. Android and Mac are available now. Use “Notify me” in the download section and we will email you when they are ready.',
    },
    {
      q: 'How do I cancel?',
      a: `You can cancel Readserene Sync at any time from your account, or email ${contactEmail} and we will do it for you. Your books stay on your device and you can keep reading with the Free plan.`,
    },
    {
      q: 'Can I export my data?',
      a: `Your books are your own files and stay on your devices. For a copy of your account data, such as your library list and reading positions, email ${contactEmail}.`,
    },
  ] satisfies FaqItem[],
};

export const yearlySavingPercent = Math.round(
  (1 - site.pricing.sync.yearly / (site.pricing.sync.monthly * 12)) * 100,
);

export const formatPrice = (n: number) =>
  `${site.pricing.symbol}${Number.isInteger(n) ? n : n.toFixed(2)}`;
