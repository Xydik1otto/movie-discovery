// Runtime-проксі /api/* → бекенд (замість rewrites: standalone-білд запікає
// rewrite-destination на етапі build і runtime env не підхоплює). Тут
// API_SERVER_URL читається на кожен запит: у compose — http://api:5200,
// локально — http://localhost:5200. Cookie-авторизація лишається same-origin.
import { PROXY_HEADERS, signProxyIdentity, verifyProxyIdentity } from "@/lib/proxyIdentity";

export const dynamic = "force-dynamic";

const HOP_BY_HOP = new Set([
  "connection",
  "keep-alive",
  "transfer-encoding",
  "upgrade",
  "content-encoding",
  "content-length",
  "host",
]);

async function proxy(req: Request, ctx: { params: Promise<{ path: string[] }> }) {
  const { path } = await ctx.params;
  const search = new URL(req.url).search;
  const cloudflare = process.env.DEPLOYMENT_TARGET === "cloudflare";
  const base = process.env.API_SERVER_URL || (cloudflare ? "" : "http://localhost:5200");
  const secret = process.env.API_PROXY_SHARED_SECRET ?? "";
  let target: URL;
  try {
    target = new URL(`/api/${path.map(encodeURIComponent).join("/")}${search}`, base);
    if (!["http:", "https:"].includes(target.protocol) || target.username || target.password
        || (cloudflare && (target.protocol !== "https:" || !secret))) throw new Error("Invalid API origin");
    if (cloudflare && target.origin === new URL(req.url).origin) throw new Error("API proxy loop");
  } catch {
    return Response.json({ error: "api_not_configured", message: "Сервіс тимчасово недоступний" }, { status: 502 });
  }
  const upstreamHeaders = new Headers();
  req.headers.forEach((v, k) => {
    if (!HOP_BY_HOP.has(k.toLowerCase()) && !PROXY_HEADERS.includes(k.toLowerCase())) upstreamHeaders.set(k, v);
  });

  if (cloudflare) {
    const ip = req.headers.get("cf-connecting-ip") ?? "";
    try {
      await signProxyIdentity(upstreamHeaders, req.method, target, ip, secret);
      upstreamHeaders.set("x-forwarded-for", ip);
      upstreamHeaders.set("x-forwarded-proto", "https");
    } catch {
      return Response.json({ error: "invalid_proxy_request" }, { status: 400 });
    }
  } else if (PROXY_HEADERS.some((header) => req.headers.has(header))) {
    const ip = await verifyProxyIdentity(req, secret);
    if (!ip) return Response.json({ error: "invalid_proxy_signature" }, { status: 403 });
    upstreamHeaders.set("x-forwarded-for", ip);
    upstreamHeaders.set("x-forwarded-proto", "https");
  }

  const hasBody = !["GET", "HEAD"].includes(req.method);
  const body = hasBody ? await req.arrayBuffer() : undefined;

  let upstream: Response;
  try {
    upstream = await fetch(target, {
      method: req.method,
      headers: upstreamHeaders,
      body,
      redirect: "manual",
      cache: "no-store",
      signal: req.signal,
    });
  } catch {
    return Response.json(
      { error: "api_unavailable", message: "Бекенд недоступний" },
      { status: 502 },
    );
  }

  const resHeaders = new Headers();
  upstream.headers.forEach((v, k) => {
    if (!HOP_BY_HOP.has(k.toLowerCase()) && k.toLowerCase() !== "set-cookie") resHeaders.append(k, v);
  });
  for (const cookie of upstream.headers.getSetCookie()) resHeaders.append("set-cookie", cookie);

  return new Response(upstream.body, { status: upstream.status, headers: resHeaders });
}

export const GET = proxy;
export const POST = proxy;
export const PUT = proxy;
export const PATCH = proxy;
export const DELETE = proxy;
export const HEAD = proxy;
export const OPTIONS = proxy;
