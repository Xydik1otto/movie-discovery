import type { MetadataRoute } from "next";

// PWA-маніфест: застосунок встановлюється на Android/iPhone як «ретранслятор» веба.
export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "КіноНавігатор",
    short_name: "КіноНавігатор",
    description:
      "Підбір фільмів і серіалів: пошук за параметрами, добірки «схоже на...», трейлери, де подивитись, списки.",
    id: "/",
    start_url: "/",
    scope: "/",
    display: "standalone",
    orientation: "portrait",
    background_color: "#0a0c10",
    theme_color: "#0a0c10",
    lang: "uk",
    icons: [
      { src: "/icons/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/icons/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      {
        src: "/icons/icon-maskable-512.png",
        sizes: "512x512",
        type: "image/png",
        purpose: "maskable",
      },
    ],
  };
}
