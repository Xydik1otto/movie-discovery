import type { Metadata, Viewport } from "next";
import "./globals.css";
import { I18nProvider } from "../i18n/I18nContext";
import { AuthProvider } from "../lib/auth";
import Header from "../components/Header";
import Footer from "../components/Footer";
import ServiceWorkerRegister from "../components/ServiceWorkerRegister";

export const metadata: Metadata = {
  title: {
    default: "КіноНавігатор",
    template: "%s · КіноНавігатор",
  },
  description:
    "Підбір фільмів і серіалів: пошук за параметрами, добірки «схоже на...», трейлери, де подивитись, списки.",
  applicationName: "КіноНавігатор",
  manifest: "/manifest.webmanifest",
  appleWebApp: {
    capable: true,
    title: "КіноНавігатор",
    statusBarStyle: "black-translucent",
  },
  icons: {
    icon: [
      { url: "/icons/icon-32.png", sizes: "32x32", type: "image/png" },
      { url: "/icons/icon-192.png", sizes: "192x192", type: "image/png" },
    ],
    apple: "/icons/apple-touch-icon.png",
  },
};

export const viewport: Viewport = {
  themeColor: "#0a0c10",
  width: "device-width",
  initialScale: 1,
  viewportFit: "cover",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="uk">
      <body className="flex min-h-screen flex-col bg-bg text-ink antialiased">
        <I18nProvider>
          <AuthProvider>
            <Header />
            <main className="mx-auto w-full max-w-7xl flex-1 px-4 py-6 sm:px-6">{children}</main>
            <Footer />
            <ServiceWorkerRegister />
          </AuthProvider>
        </I18nProvider>
      </body>
    </html>
  );
}
