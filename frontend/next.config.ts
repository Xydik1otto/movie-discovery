import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  // Проксі /api/* реалізований рантайм-роутом app/api/[...path]/route.ts
  // (rewrites не підходять: standalone білд запікає destination при збірці).
};

export default nextConfig;
