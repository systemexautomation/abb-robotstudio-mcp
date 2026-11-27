/**
 * ABB RobotStudio MCP Server — RWS REST Client
 * v3.1.0 — Robot Web Services client with Digest auth support
 */

import { createHash, randomUUID, randomInt } from "crypto";
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
  if (body !== undefined && contentType) {
    headers["Content-Type"] = _useV2 && !/v=2.0/.test(contentType) ? contentType + ";v=2.0" : contentType;
  }

  const res = await rwsFetch(url, { method, headers, body: body ?? null });

  // Track the session cookies (RobotWare 8 sets several; the control station
  // registration is bound to the session, so none of them may be dropped)
  const setCookie = res.headers.getSetCookie();
  for (const c of setCookie) {
    if (!(c.includes("ABBCX") || c.includes("-http-session"))) continue;
    const pair = c.split(";")[0] ?? "";
    const key = pair.split("=")[0];
    const kept = _cookie ? _cookie.split("; ").filter((p) => p.split("=")[0] !== key) : [];
    _cookie = [...kept, pair].join("; ");
  }

  // RobotWare 8: "Content type is not supported. Use ... ;v=2.0" (406, or 415 for a file PUT)
  // — remember and retry once
  if ((res.status === 406 || res.status === 415) && body && contentType && !_useV2) {
    const text = await res.text().catch(() => "");
    if (/v=2.0/.test(text)) {
      _useV2 = true;
      return rws(method, path, body, contentType);
    }
    throw new RwsError(`RWS ${method} ${path} → ${res.status}`, res.status, text);
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

// ── Control station (RobotWare 8) ───────────────────────────

// RobotWare 8 replaced mastership by the control station service: a client must register as a
// remote control station, then request write access, before it may change the configuration
// or the RAPID program. Only one control station holds write access (SPoC, single point of
// control): when another one holds it (for example the FlexPendant), the request is refused.
export async function withWriteAccess<T>(fn: () => Promise<T>): Promise<T> {
  try {
    await rwsPostForm("/rw/controlstation/register/remote", {
      "control-station-name": "ClaudeBridge",
      "control-station-id": `{${randomUUID()}}`,
      pincode: String(randomInt(1_000_000_000)),
      "release-write-access-when-lost": "true",
    });
  } catch (e) {
    // The registration is bound to the RWS session: a second write in the same session
    // finds this client already registered, which is fine
    if (!(e instanceof RwsError && e.status === 403 && /already been registered/i.test(e.body))) throw e;
  }
  // RobotWare 8 wants the form content type even on a POST without parameters
  const form = "application/x-www-form-urlencoded;v=2.0";
  try {
    await rws("POST", "/rw/controlstation/writeaccess/request", "", form);
  } catch (e) {
    if (e instanceof RwsError && e.status === 400) {
      const holder = await writeAccessHolder().catch(() => "");
      throw new Error(
        "Write access refused by the controller" +
        (holder ? `: it is held by the control station "${holder}".` : ".") +
        " Release write access on that control station (or close it), then retry." +
        ` (${e.message}: ${e.body.slice(0, 200)})`
      );
    }
    throw e;
  }
  try {
    return await fn();
  } finally {
    await rws("POST", "/rw/controlstation/writeaccess/release", "", form).catch(() => {});
  }
}

// Name of the control station that holds write access ("" when none)
async function writeAccessHolder(): Promise<string> {
  const r = await rws("/rw/controlstation/writeaccess/status");
  const held = /control-station-write-access-held[^>]*>\s*true|"control-station-write-access-held"\s*:\s*"true"/i.test(r.body);
  if (!held) return "";
  return (
    rwsExtract(r.body, "held-by-control-station-name") ||
    (r.body.match(/"held-by-control-station-name"\s*:\s*"([^"]*)"/)?.[1] ?? "")
  );
}

// Runs a RAPID write with the right kind of access for the controller:
// mastership on RobotWare 6 / 7, control station write access on RobotWare 8
// (where the mastership service answers 410 Gone).
export async function withRapidWriteAccess<T>(fn: () => Promise<T>): Promise<T> {
  let mastership = false;
  const path = "/rw/mastership/rapid?action=request";
  const form = "application/x-www-form-urlencoded";
  try {
    try {
      await rws("POST", path, "", form);
    } catch (e) {
      // RobotWare 8 answers 406 to a content type without the API version, before 410
      if (!(e instanceof RwsError && e.status === 406)) throw e;
      await rws("POST", path, "", form + ";v=2.0");
    }
    mastership = true;
  } catch (e) {
    if (!(e instanceof RwsError && (e.status === 410 || e.status === 404 || e.status === 405))) throw e;
  }
  if (!mastership) return withWriteAccess(fn);
  try {
    return await fn();
  } finally {
    await mastershipRelease("rapid");
  }
}

// RobotWare 8 (RWS 2) moved the actions into the path (".../loadmod"); RobotWare 6 / 7 (RWS 1)
// take them as a query (...?action=loadmod). Try the RWS 2 form first, then the RWS 1 form when
// the resource does not exist or does not accept the method.
export async function rwsPostAction(
  rws2Path: string,
  rws1Path: string,
  params: Record<string, string>
): Promise<{ status: number; body: string }> {
  try {
    return await rwsPostForm(rws2Path, params);
  } catch (e) {
    if (e instanceof RwsError && (e.status === 404 || e.status === 405)) {
      return rwsPostForm(rws1Path, params);
    }
    throw e;
  }
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

// ── Controller and execution state ──────────────────────────

// True when the RWS URL points to this PC (a RobotStudio virtual controller)
export function rwsIsLocal(): boolean {
  try {
    return LOOPBACK_HOSTS.has(new URL(getRwsUrl()).hostname.toLowerCase());
  } catch {
    return false;
  }
}

// Controller state: motoron, motoroff, guardstop, emergencystop... (/rw/panel/ctrl-state on
// RobotWare 8, /rw/panel/ctrlstate on RobotWare 6). When both fail, the first error is thrown.
export async function rwsControllerState(): Promise<string> {
  const r = await rws("/rw/panel/ctrl-state").catch((e1) =>
    rws("/rw/panel/ctrlstate").catch(() => { throw e1; }));
  return rwsExtract(r.body, "ctrlstate") || rwsExtract(r.body, "ctrl-state");
}

export async function rwsExecutionState(): Promise<{ state: string; cycle: string }> {
  const r = await rws("/rw/rapid/execution");
  return { state: rwsExtract(r.body, "ctrlexecstate"), cycle: rwsExtract(r.body, "cycle") };
}

export interface ProgramPointer { module: string; routine: string; line: number }

// Program pointer from a GET /rw/rapid/tasks/{task}/pcp answer, or null when the task has none
// (after a module load, for example). RWS spells the module field "modulemame".
export function parseProgramPointer(body: string): ProgramPointer | null {
  const pp = body.match(/title="progpointer"[^>]*>([\s\S]*?)<\/li>/)?.[1] ?? "";
  const routine = rwsExtract(pp, "routinename");
  if (!routine) return null;
  return {
    module: rwsExtract(pp, "modulemame") || rwsExtract(pp, "modulename"),
    routine,
    line: Number(rwsExtract(pp, "beginposition").split(",")[0]) || 0,
  };
}

export async function rwsProgramPointer(task: string): Promise<ProgramPointer | null> {
  const r = await rws(`/rw/rapid/tasks/${encodeURIComponent(task)}/pcp`);
  return parseProgramPointer(r.body);
}

// ── Event log ───────────────────────────────────────────────

// type: 1 information, 2 warning, 3 error
export interface ElogEntry { seq: number; type: number; code: number; timestamp: string; title: string; description: string }

export function parseElog(body: string): ElogEntry[] {
  const out: ElogEntry[] = [];
  const re = /<li class="elog-message-li"([\s\S]*?)<\/li>/g;
  let m;
  while ((m = re.exec(body)) !== null) {
    const li = m[1] ?? "";
    out.push({
      seq: Number(li.match(/title="[^"]*\/(\d+)"/)?.[1] ?? 0),
      type: Number(rwsExtract(li, "msgtype")) || 0,
      code: Number(rwsExtract(li, "code")) || 0,
      timestamp: rwsExtract(li, "tstamp"),
      title: rwsExtract(li, "title"),
      description: rwsExtract(li, "desc"),
    });
  }
  return out;
}

// Newest entries of an event log domain, newest first (at most 50). RobotWare 8 (RWS 2) pages
// the log oldest first (order=fifo) and links the newest page as rel="last": read that page,
// and the one before when it is not full. Without such a link the first page is used.
export async function rwsRecentEvents(count: number, domain = 0): Promise<ElogEntry[]> {
  const limit = Math.max(1, Math.min(50, Math.floor(count) || 10));
  const first = await rws(`/rw/elog/${domain}?lang=en&limit=${limit}`);
  const link = (rel: string) =>
    first.body.match(new RegExp(`<a\\b[^>]*\\bhref="([^"]*)"[^>]*\\brel="${rel}"`))?.[1]?.replace(/&amp;/g, "&");
  const last = link("last");
  let entries: ElogEntry[];
  if (last && /order=fifo/.test(last)) {
    entries = parseElog((await rws(`/rw/elog/${last}`)).body);
    const page = Number(last.match(/[?&]start=(\d+)/)?.[1] ?? 0);
    if (entries.length < limit && page > 1) {
      const previous = last.replace(/([?&]start=)\d+/, `$1${page - 1}`);
      entries = entries.concat(parseElog((await rws(`/rw/elog/${previous}`)).body));
    }
  } else if (link("next")) {
    // More pages but no usable link to the newest one: the first page may be the OLDEST entries
    throw new Error("Event log: cannot find the page of the newest entries (no rel=\"last\" link)");
  } else {
    entries = parseElog(first.body);
  }
  if (entries.length === 0 && /elog-message-li/.test(first.body)) {
    throw new Error("Event log: entries present but none could be read");
  }
  // A new event between two reads can shift the pages: keep each entry once
  const seen = new Set<number>();
  return entries
    .sort((a, b) => b.seq - a.seq)
    .filter((e) => !seen.has(e.seq) && (seen.add(e.seq), true))
    .slice(0, limit);
}
