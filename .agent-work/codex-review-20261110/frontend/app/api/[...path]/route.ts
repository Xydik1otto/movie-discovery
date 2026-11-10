// Runtime-проксі /api/* → бекенд (замість rewrites: standalone-білд запікає
// rewrite-destination на етапі build і runtime env не підхоплює). Тут
// API_SERVER_URL читається на кожен запит: у compose — http://api:5200,
// локально — http://localhost:5200. Cookie-авторизація лишається same-origin.
export const dynamic = "force-dynamic";

const BASE = process.env.API_SERVER_URL ?? "http://localhost:5200";

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
  const upstreamHeaders = new Headers();
  req.headers.forEach((v, k) => {
    if (!HOP_BY_HOP.has(k.toLowerCase())) upstreamHeaders.set(k, v);
  });

  const hasBody = !["GET", "HEAD"].includes(req.method);
  const body = hasBody ? await req.arrayBuffer() : undefined;

  let upstream: Response;
  try {
    upstream = await fetch(`${BASE}/api/${path.join("/")}${search}`, {
      method: req.method,
      headers: upstreamHeaders,
      body,
      redirect: "manual",
      cache: "no-store",
    });
  } catch {
    return Response.json(
      { error: "api_unavailable", message: "Бекенд недоступний" },
      { status: 502 },
    );
  }

  const resHeaders = new Headers();
  upstream.headers.forEach((v, k) => {
    if (!HOP_BY_HOP.has(k.toLowerCase())) resHeaders.append(k, v);
  });

  const buf = await upstream.arrayBuffer();
  return new Response(buf, { status: upstream.status, headers: resHeaders });
}

export const GET = proxy;
export const POST = proxy;
export const PUT = proxy;
export const PATCH = proxy;
export const DELETE = proxy;
export const HEAD = proxy;
export const OPTIONS = proxy;
