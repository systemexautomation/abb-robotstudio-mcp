/**
 * ABB RobotStudio MCP Server — RWS REST Client
 * v3.1.0 — Robot Web Services client with Digest auth support
 */

import { createHash, randomUUID } from "crypto";
import { request as httpsRequest } from "node:https";
import { getRwsUrl, RWS_USER, RWS_PASS } from "./config.js";

// ── TLS for the virtual controller ──────────────────────────
// RobotWare 7 virtual controllers serve RWS over HTTPS with a self-signed certificate.
// Certificate verification is relaxed ONLY for loopback hosts. Any other host (a real
// controller) goes through the normal fetch(), with full certificate verification.

const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]", "::1"]);

function isLoopbackHttps(url: string): boolean {
  try {
    const u = new URL(url);
    return u.protocol === "https:" && LOOPBACK_HOSTS.has(u.hostname.toLowerCase());
  } catch {
    return false;
  }
}

function loopbackHttpsFetch(
  url: string,
  init: { method: string; headers: Record<string, string>; body: string | null }
): Promise<Response> {
  return new Promise((resolve, reject) => {
    // Send a Content-Length: the controller does not read chunked request bodies
    const headers = init.body
      ? { ...init.headers, "Content-Length": String(Buffer.byteLength(init.body)) }
      : init.headers;
    const req = httpsRequest(
      url,
      { method: init.method, headers, rejectUnauthorized: false, timeout: 30000 },
      (res) => {
        const chunks: Buffer[] = [];
        res.on("data", (c: Buffer) => chunks.push(c));
        res.on("error", reject);
        res.on("end", () => {
          const headers = new Headers();
          for (const [name, value] of Object.entries(res.headers)) {
            if (value === undefined) continue;
            if (Array.isArray(value)) value.forEach((v) => headers.append(name, v));
            else headers.set(name, value);
          }
          const status = res.statusCode ?? 0;
          const noBody = status === 204 || status === 205 || status === 304;
          resolve(new Response(noBody ? null : Buffer.concat(chunks), { status, headers }));
        });
      }
    );
    req.on("timeout", () => req.destroy(new Error(`RWS request timed out: ${init.method} ${url}`)));
    req.on("error", reject);
    if (init.body) req.write(init.body);
    req.end();
  });
}

function rwsFetch(
  url: string,
  init: { method: string; headers: Record<string, string>; body: string | null }
): Promise<Response> {
  return isLoopbackHttps(url) ? loopbackHttpsFetch(url, init) : fetch(url, init);
}

export class RwsError extends Error {
  status: number;
  body: string;
  constructor(message: string, status: number, body: string) {
    super(message);
    this.name = "RwsError";
    this.status = status;
    this.body = body;
  }
}

// ── Auth & Session ──────────────────────────────────────────

let _cookie = "";
let _digestState: { realm: string; nonce: string; qop: string; opaque?: string; nc: number } | null = null;
// RobotWare 7 virtual controllers ask for Basic auth (over HTTPS) instead of Digest.
let _useBasic = false;
// RobotWare 8 refuses POST bodies unless the content type carries the API version (v=2.0).
let _useV2 = false;

export function resetRwsSession(): void {
  _cookie = "";
  _digestState = null;
  _useBasic = false;
  _useV2 = false;
}

// Basic sends the password in clear: allow it only over HTTPS or to a loopback host.
function basicAuthAllowed(url: string): boolean {
  try {
    const u = new URL(url);
    return u.protocol === "https:" || LOOPBACK_HOSTS.has(u.hostname.toLowerCase());
  } catch {
    return false;
  }
}

function md5(s: string): string {
  return createHash("md5").update(s).digest("hex");
}

function rwsBasicAuth(): string {
  return "Basic " + Buffer.from(`${RWS_USER}:${RWS_PASS}`).toString("base64");
}

function rwsDigestAuth(method: string, uri: string): string {
  if (!_digestState) throw new Error("No digest challenge received");
  const { realm, nonce, qop, opaque } = _digestState;
  _digestState.nc++;
  const nc = _digestState.nc.toString(16).padStart(8, "0");
  const cnonce = randomUUID().replace(/-/g, "");

  const ha1 = md5(`${RWS_USER}:${realm}:${RWS_PASS}`);
  const ha2 = md5(`${method}:${uri}`);
  const response = qop
    ? md5(`${ha1}:${nonce}:${nc}:${cnonce}:${qop}:${ha2}`)
    : md5(`${ha1}:${nonce}:${ha2}`);

  let header = `Digest username="${RWS_USER}", realm="${realm}", nonce="${nonce}", uri="${uri}", response="${response}"`;
  if (qop) header += `, qop=${qop}, nc=${nc}, cnonce="${cnonce}"`;
  if (opaque) header += `, opaque="${opaque}"`;
  return header;
}

function parseDigestChallenge(header: string): { realm: string; nonce: string; qop: string; opaque?: string } | null {
  const parts: Record<string, string> = {};
  const re = /(\w+)=["']?([^"',;]+)["']?/g;
  let m;
  while ((m = re.exec(header)) !== null) {
    parts[m[1]!] = m[2]!;
  }
  if (!parts.realm || !parts.nonce) return null;
  return {
    realm: parts.realm!,
    nonce: parts.nonce!,
    qop: parts.qop || "",
    opaque: parts.opaque,
  };
}

// ── Core RWS Call ───────────────────────────────────────────

export async function rws(
  methodOrPath: string,
  pathOrBody?: string,
  bodyOrCt?: string,
  ct?: string
): Promise<{ status: number; body: string }> {
  let method: string,
    path: string,
    body: string | undefined,
    contentType: string | undefined;
  if (
    methodOrPath === "GET" ||
    methodOrPath === "POST" ||
    methodOrPath === "PUT" ||
    methodOrPath === "DELETE"
  ) {
    method = methodOrPath;
    path = pathOrBody!;
    body = bodyOrCt;
    contentType = ct;
  } else {
    method = "GET";
    path = methodOrPath;
    body = pathOrBody;
    contentType = bodyOrCt;
  }

  const url = `${getRwsUrl()}${path}`;

  // Build headers — start with no auth (Digest negotiation), use Digest if already challenged
  const headers: Record<string, string> = {
    Accept: "application/xhtml+xml;v=2.0",
  };
  if (_useBasic) {
    headers["Authorization"] = rwsBasicAuth();
  } else if (_digestState) {
    headers["Authorization"] = rwsDigestAuth(method, path);
  }
  if (_cookie) headers["Cookie"] = _cookie;
  if (body && contentType) {
    headers["Content-Type"] = _useV2 && !/v=2.0/.test(contentType) ? contentType + ";v=2.0" : contentType;
  }

  const res = await rwsFetch(url, { method, headers, body: body ?? null });

  // Track session cookie
  const setCookie = res.headers.getSetCookie();
  for (const c of setCookie) {
    if (c.includes("ABBCX") || c.includes("-http-session"))
      _cookie = c.split(";")[0] ?? "";
  }

  // RobotWare 8: "Content type is not supported. Use ... ;v=2.0" — remember and retry once
  if (res.status === 406 && body && contentType && !_useV2) {
    const text = await res.text().catch(() => "");
    if (/v=2.0/.test(text)) {
      _useV2 = true;
      return rws(method, path, body, contentType);
    }
    throw new RwsError(`RWS ${method} ${path} → 406`, 406, text);
  }

  // Handle 401 — negotiate or refresh Digest auth
  if (res.status === 401) {
    const wwwAuth = res.headers.get("WWW-Authenticate") || "";
    if (
      wwwAuth.toLowerCase().startsWith("basic") &&
      !headers["Authorization"] && // never retry after Basic was already rejected
      basicAuthAllowed(url)
    ) {
      await res.text().catch(() => {});
      _useBasic = true;
      return rws(method, path, body, contentType);
    }
    if (wwwAuth.toLowerCase().includes("digest")) {
      const challenge = parseDigestChallenge(wwwAuth);
      if (challenge) {
        // Drain the 401 body so the connection can be reused
        await res.text().catch(() => {});
        _digestState = { ...challenge, nc: 0 };
        // Retry with fresh digest
        return rws(method, path, body, contentType);
      }
    }
  }

  const resBody = await res.text();
  if (res.status >= 400) {
    throw new RwsError(
      `RWS ${method} ${path} → ${res.status}`,
      res.status,
      resBody
    );
  }
  return { status: res.status, body: resBody };
}

// ── Convenience ─────────────────────────────────────────────

export async function rwsPostForm(
  path: string,
  params: Record<string, string>
): Promise<{ status: number; body: string }> {
  const formBody = Object.entries(params)
    .map(([k, v]) => `${k}=${encodeURIComponent(v)}`)
    .join("&");
  return rws("POST", path, formBody, "application/x-www-form-urlencoded");
}

// ── Mastership ──────────────────────────────────────────────

export async function mastershipRequest(
  domain: string = "rapid"
): Promise<void> {
  try {
    await rws("POST", `/rw/mastership/${domain}?action=request`);
  } catch (e) {
    if (e instanceof RwsError && e.status === 410) return; // RobotWare 8: no mastership service
    throw e;
  }
}

export async function mastershipRelease(
  domain: string = "rapid"
): Promise<void> {
  await rws("POST", `/rw/mastership/${domain}?action=release`).catch(() => {});
}

// ── XML Extraction ──────────────────────────────────────────

export function rwsExtract(xml: string, tag: string): string {
  const m =
    xml.match(new RegExp(`class="${tag}"[^>]*>([^<]*)<`, "i")) ??
    xml.match(new RegExp(`<${tag}[^>]*>([^<]*)</${tag}>`, "i"));
  return m?.[1]?.trim() ?? "";
}

export function rwsExtractAll(xml: string, field: string): string[] {
  const results: string[] = [];
  const re = new RegExp(`class="${field}"[^>]*>([^<]*)<`, "gi");
  let m;
  while ((m = re.exec(xml)) !== null) results.push((m[1] ?? "").trim());
  return results;
}
