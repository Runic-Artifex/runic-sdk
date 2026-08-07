import type { Metadata } from "next";
import Link from "next/link";
import { Geist, Geist_Mono, Newsreader } from "next/font/google";
import { headers } from "next/headers";
import "./globals.css";

const sans = Geist({ variable: "--font-sans", subsets: ["latin"] });
const mono = Geist_Mono({ variable: "--font-mono", subsets: ["latin"] });
const serif = Newsreader({ variable: "--font-serif", subsets: ["latin"] });

export async function generateMetadata(): Promise<Metadata> {
  const requestHeaders = await headers();
  const forwardedHost = requestHeaders.get("x-forwarded-host")?.split(",")[0].trim();
  const host = forwardedHost ?? requestHeaders.get("host") ?? "localhost:3000";
  const forwardedProtocol = requestHeaders.get("x-forwarded-proto")?.split(",")[0].trim();
  const protocol = forwardedProtocol === "http" || forwardedProtocol === "https"
    ? forwardedProtocol
    : host.startsWith("localhost") || host.startsWith("127.0.0.1")
      ? "http"
      : "https";
  const metadataBase = new URL(`${protocol}://${host}`);
  const description =
    "Documentation for the independent Runic Artifex products and their explicit integration boundaries.";
  const socialImage = new URL("/og.png", metadataBase).toString();

  return {
    metadataBase,
    title: { default: "Runic Artifex Documentation", template: "%s · Runic Artifex" },
    description,
    openGraph: {
      type: "website",
      siteName: "Runic Artifex Documentation",
      title: "Runic Artifex Documentation",
      description,
      images: [{ url: socialImage, width: 1200, height: 630, alt: "Runic Artifex documentation" }],
    },
    twitter: {
      card: "summary_large_image",
      title: "Runic Artifex Documentation",
      description,
      images: [socialImage],
    },
  };
}

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body className={`${sans.variable} ${mono.variable} ${serif.variable}`}>
        <a className="skip-link" href="#content">Skip to content</a>
        <header className="site-header">
          <div className="shell header-inner">
            <Link className="brand" href="/" aria-label="Runic Artifex documentation home">
              <span className="brand-mark" aria-hidden="true">ᚱ</span>
              <span><strong>Runic Artifex</strong><small>Documentation</small></span>
            </Link>
            <nav aria-label="Primary navigation">
              <Link href="/getting-started">Start</Link>
              <Link href="/products">Products</Link>
              <Link href="/application-bridge">Application Bridge</Link>
              <Link href="/architecture">Architecture</Link>
              <Link href="/packages">Packages</Link>
              <Link href="/releases">Releases</Link>
            </nav>
          </div>
        </header>
        <div id="content">{children}</div>
        <footer className="site-footer">
          <div className="shell footer-grid">
            <div>
              <div className="brand footer-brand"><span className="brand-mark" aria-hidden="true">ᚱ</span><span><strong>Runic Artifex</strong><small>Independent tools. Explicit seams.</small></span></div>
              <p>NativeAOT-minded building blocks for modern .NET applications.</p>
            </div>
            <div><strong>Explore</strong><Link href="/products">Products</Link><Link href="/packages">Package catalog</Link><Link href="/architecture">Architecture</Link></div>
            <div><strong>Project</strong><a href="https://github.com/Runic-Artifex">GitHub organization</a><Link href="/releases">Release policy</Link><a href="https://github.com/Runic-Artifex/.github/blob/main/SECURITY.md">Security</a></div>
          </div>
          <div className="shell footer-bottom"><span>© 2026 Runic Artifex</span><span>MIT where possible · Preview documentation</span></div>
        </footer>
      </body>
    </html>
  );
}
