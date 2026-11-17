// Only the server-side API proxy imports this module.
import { isIP } from "node:net";

export const PROXY_IP = "x-movie-client-ip";
export const PROXY_TIME = "x-movie-proxy-time";
export const PROXY_SIGNATURE = "x-movie-proxy-signature";
export const PROXY_HEADERS = [PROXY_IP, PROXY_TIME, PROXY_SIGNATURE];

function payload(method: string, url: URL, ip: string, time: string) {
  return new TextEncoder().encode(`${method}\n${url.pathname}${url.search}\n${ip}\n${time}`);
}

async function signingKey(secret: string) {
  return crypto.subtle.importKey(
    "raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false,
    ["sign", "verify"],
  );
}

export async function signProxyIdentity(
  headers: Headers, method: string, url: URL, ip: string, secret: string,
) {
  if (!isIP(ip)) throw new Error("Invalid proxy client IP");
  const time = Math.floor(Date.now() / 1000).toString();
  const signature = await crypto.subtle.sign("HMAC", await signingKey(secret), payload(method, url, ip, time));
  headers.set(PROXY_IP, ip);
  headers.set(PROXY_TIME, time);
  headers.set(PROXY_SIGNATURE, btoa(String.fromCharCode(...new Uint8Array(signature))));
}

export async function verifyProxyIdentity(req: Request, secret: string): Promise<string | null> {
  const ip = req.headers.get(PROXY_IP) ?? "";
  const time = req.headers.get(PROXY_TIME) ?? "";
  const signature = req.headers.get(PROXY_SIGNATURE) ?? "";
  const seconds = Number(time);
  if (!secret || !isIP(ip) || !/^\d+$/.test(time) || !Number.isSafeInteger(seconds)
      || Math.abs(Date.now() / 1000 - seconds) > 60 || signature.length > 100) return null;
  try {
    const bytes = Uint8Array.from(atob(signature), (char) => char.charCodeAt(0));
    const valid = await crypto.subtle.verify(
      "HMAC", await signingKey(secret), bytes, payload(req.method, new URL(req.url), ip, time),
    );
    return valid ? ip : null;
  } catch {
    return null;
  }
}
