import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  // Проксі /api/* на бекенд: браузер завжди ходить same-origin (cookie без CORS-пасток).
  // API_SERVER_URL — runtime env Next-сервера (у compose: http://api:5200; локально http://localhost:5200).
  async rewrites() {
    const dest = `${process.env.API_SERVER_URL ?? "http://localhost:5200"}/api/:path*`;
    return [{ source: "/api/:path*", destination: dest }];
  },
};

export default nextConfig;
