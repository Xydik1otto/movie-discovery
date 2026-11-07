import type { Metadata } from "next";
import "./globals.css";
import { I18nProvider } from "../i18n/I18nContext";
import Header from "../components/Header";
import Footer from "../components/Footer";

export const metadata: Metadata = {
  title: {
    default: "КіноНавігатор",
    template: "%s · КіноНавігатор",
  },
  description:
    "Підбір фільмів і серіалів: пошук за параметрами, добірки «схоже на...», трейлери, де подивитись, списки.",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="uk">
      <body className="flex min-h-screen flex-col bg-bg text-ink antialiased">
        <I18nProvider>
          <Header />
          <main className="mx-auto w-full max-w-7xl flex-1 px-4 py-6">{children}</main>
          <Footer />
        </I18nProvider>
      </body>
    </html>
  );
}
