import { discordTick } from "./discord.js";

const ALLOWED = {
  Color: ["Enabled", "Exposure", "Contrast", "Saturation", "HueShift", "ColorFilter"],
  WhiteBalance: ["Enabled", "Temperature", "Tint"],
  LiftGammaGain: ["Enabled", "Lift", "Gamma", "Gain"],
  ShadowsMidtonesHighlights: ["Enabled", "Shadows", "Midtones", "Highlights", "ShadowsStart", "ShadowsEnd", "HighlightsStart", "HighlightsEnd"],
  SplitToning: ["Enabled", "Shadows", "Highlights", "Balance"],
  ChannelMixer: ["Enabled", "Red", "Green", "Blue"],
  Tonemapping: ["Enabled", "Mode"],
  Sharpen: ["Enabled", "Amount"],
  FilmGrain: ["Enabled", "Intensity", "Response", "Size", "Colored"],
  ChromaticAberration: ["Enabled", "Intensity"],
  LensDistortion: ["Enabled", "Intensity", "Scale"],
  Vignette: ["Enabled", "Intensity", "Smoothness", "Rounded", "Color", "Center"],
  Bloom: ["Enabled", "Intensity", "Threshold", "SoftKnee", "Scatter"],
  DepthOfField: ["Enabled", "Strength", "FocusDistance", "MaxBlur", "FarCurve", "NearCurve", "NearStrength", "BokehIntensity", "AutofocusRadius"],
  Scanlines: ["Enabled", "Intensity", "Count", "Speed", "RollingBar"],
  CRT: ["Enabled", "Curvature"],
  Noise: ["Enabled", "Intensity"],
  Glitch: ["Enabled", "Amount", "Rows", "Speed"],
  Posterize: ["Enabled", "Levels"],
  EyeAdaptation: ["Enabled", "Strength"],
  LensFlare: ["Enabled", "Intensity", "Spacing", "Halo", "Fringe"],
  LensDirt: ["Enabled", "Intensity"],
  GodRays: ["Enabled", "Intensity", "Length", "Threshold"],
};
const WORDS = new Set(["true", "false", "on", "off", "aces", "neutral", "none"]);
const BAD = /(n[i1!]gg|f[a@]g|retard|\bkys\b|rap(e|ist)|nazi|hitler|cunt|whore|porn|\bsex|dick|cock|puss(y|ie)|penis|vagina|f+u+c+k|sh[i1]t|b[i1]tch|slut|\bcum\b)/i;

const SLOP = /^(my preset|new preset|preset|untitled|shared preset|test|testing|copy|custom|reshade|bodycam|cctv|cinematic|clean cam|found footage|natural|night ops|night vision|noir|punchy|vivid|warm)( edit)?( ?\(?\d+\)?)?$/i;
const SLOP_WORD = /slop/i;
export const isSlop = n => { const s = String(n || "").trim().replace(/\s+/g, " "); return s.length < 3 || SLOP.test(s) || SLOP_WORD.test(s); };

const MAX_INI = 6000, MAX_NAME = 40, MAX_AUTHOR = 24, SHARES_PER_HOUR = 5;

export default {
  async scheduled(event, env, ctx) {
    ctx.waitUntil(discordTick(env).catch(e => console.log("discord tick failed", e)));
    ctx.waitUntil(hideSlop(env).catch(e => console.log("slop cleanup failed", e)));
  },

  async fetch(req, env) {
    const url = new URL(req.url);
    const parts = url.pathname.replace(/\/+$/, "").split("/").filter(Boolean);
    try {
      if (parts.length === 0) return json({ ok: true, service: "QuestShade presets" });
      if (parts[0] !== "api" || parts[1] !== "presets") return err(404, "Not found");
      const id = parts[2], action = parts[3];
      if (req.method === "GET" && id === "share" && !action) {
        if (!clientId(req)) return err(400, "Bad request");
        let body = null;
        try { body = JSON.parse(new TextDecoder().decode(b64url(url.searchParams.get("d") || ""))); } catch { }
        return share(req, env, body);
      }
      if (req.method === "GET" && action === "like") return like(req, id, env);
      if (req.method === "GET" && action === "report") return report(req, id, env);
      if (req.method === "GET" && !id) return list(url, env);
      if (req.method === "GET" && id && !action) return getOne(id, env);
      if (req.method === "POST" && !id) return share(req, env);
      if (req.method === "POST" && action === "like") return like(req, id, env);
      if (req.method === "POST" && action === "report") return report(req, id, env);
      if (req.method === "DELETE" && id && !action) return admin(req, env, id, 1);
      if (req.method === "POST" && action === "restore") return admin(req, env, id, 0);
      return err(405, "Not allowed");
    } catch (e) {
      return err(500, "Server error");
    }
  },
};

async function list(url, env) {
  const sort = url.searchParams.get("sort") === "top" ? "likes DESC, created DESC" : "created DESC";
  const q = (url.searchParams.get("q") || "").trim().slice(0, 40);
  const where = q ? "AND (name LIKE ?1 OR author LIKE ?1)" : "";
  const stmt = env.DB.prepare(`SELECT id, name, author, likes, downloads FROM presets WHERE hidden = 0 ${where} ORDER BY ${sort} LIMIT 40`);
  const { results } = await (q ? stmt.bind(`%${q.replace(/[%_]/g, "")}%`) : stmt).all();
  return json({ presets: results });
}

async function getOne(id, env) {
  const p = await env.DB.prepare("SELECT id, name, author, ini, likes, downloads FROM presets WHERE id = ?1 AND hidden = 0").bind(id).first();
  if (!p) return err(404, "That preset isn't available any more");
  await env.DB.prepare("UPDATE presets SET downloads = downloads + 1 WHERE id = ?1").bind(id).run();
  return json(p);
}

async function hideSlop(env) {
  const { results } = await env.DB.prepare("SELECT id, name, author FROM presets WHERE hidden = 0").all();
  for (const p of results || []) {
    if (!isSlop(p.name)) continue;
    await env.DB.prepare("UPDATE presets SET hidden = 2 WHERE id = ?1").bind(p.id).run();
    await notify(env, `🧹 Hid slop-named preset **${esc(p.name)}** by **${esc(p.author)}** (\`${p.id}\`)`);
  }
}

async function share(req, env, given) {
  const body = given !== undefined ? given : await req.json().catch(() => null);
  if (!body) return err(400, "Bad request");
  const name = oneLine(body.name, MAX_NAME), author = oneLine(body.author, MAX_AUTHOR) || "Anonymous";
  if (!name) return err(400, "Give your preset a name first");
  if (SLOP_WORD.test(name)) return err(400, "Pick a different name");
  if (isSlop(name)) return err(400, "Rename it first: \"" + name + "\" is a default name (Edit preset > Rename)");
  if (BAD.test(name) || BAD.test(author)) return err(400, "Pick a different name");
  const ini = cleanIni(String(body.ini || "").slice(0, MAX_INI));
  if (!ini) return err(400, "This preset doesn't change anything yet");

  const hash = await sha(ini);
  const same = await env.DB.prepare("SELECT id FROM presets WHERE hash = ?1").bind(hash).first();
  if (same) return json({ id: same.id, existing: true });

  const who = await whoHash(req);
  const mine = await env.DB.prepare("SELECT id FROM presets WHERE who = ?1 AND name = ?2 COLLATE NOCASE AND author = ?3 COLLATE NOCASE AND hidden = 0 ORDER BY created DESC LIMIT 1")
    .bind(who, name, author).first();
  if (mine) {
    await env.DB.prepare("UPDATE presets SET ini = ?1, hash = ?2 WHERE id = ?3").bind(ini, hash, mine.id).run();
    await notify(env, `✏️ Updated public preset **${esc(name)}** by **${esc(author)}** (\`${mine.id}\`)`);
    return json({ id: mine.id, updated: true });
  }
  const recent = await env.DB.prepare("SELECT COUNT(*) AS n FROM presets WHERE who = ?1 AND created > ?2")
    .bind(who, Date.now() - 3600_000).first();
  if (recent && recent.n >= SHARES_PER_HOUR) return err(429, "That's a lot of sharing. Try again in an hour");

  const id = newId();
  await env.DB.prepare("INSERT INTO presets (id, name, author, ini, hash, who, created) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)")
    .bind(id, name, author, ini, hash, who, Date.now()).run();
  await notify(env, `🎨 New public preset **${esc(name)}** by **${esc(author)}** (\`${id}\`)`);
  return json({ id });
}

async function like(req, id, env) {
  const client = clientId(req);
  if (!client) return err(400, "Bad request");
  const exists = await env.DB.prepare("SELECT 1 FROM presets WHERE id = ?1 AND hidden = 0").bind(id).first();
  if (!exists) return err(404, "That preset isn't available any more");
  const r = await env.DB.prepare("INSERT OR IGNORE INTO likes (preset, client) VALUES (?1, ?2)").bind(id, client).run();
  if (r.meta.changes > 0) await env.DB.prepare("UPDATE presets SET likes = likes + 1 WHERE id = ?1").bind(id).run();
  const p = await env.DB.prepare("SELECT likes FROM presets WHERE id = ?1").bind(id).first();
  return json({ likes: p ? p.likes : 0 });
}

async function report(req, id, env) {
  const client = clientId(req);
  if (!client) return err(400, "Bad request");
  const r = await env.DB.prepare("INSERT OR IGNORE INTO reports (preset, client) VALUES (?1, ?2)").bind(id, client).run();
  if (r.meta.changes > 0) {
    await env.DB.prepare("UPDATE presets SET reports = reports + 1, hidden = CASE WHEN reports + 1 >= 3 THEN 1 ELSE hidden END WHERE id = ?1").bind(id).run();
    const p = await env.DB.prepare("SELECT name, author, reports, hidden FROM presets WHERE id = ?1").bind(id).first();
    if (p) await notify(env, `🚩 Report on **${esc(p.name)}** by **${esc(p.author)}** (\`${id}\`): ${p.reports} report(s)${p.hidden ? ", now hidden" : ""}`);
  }
  return json({ ok: true });
}

async function admin(req, env, id, hidden) {
  if (!env.ADMIN_KEY || req.headers.get("Authorization") !== `Bearer ${env.ADMIN_KEY}`) return err(403, "Not allowed");
  await env.DB.prepare("UPDATE presets SET hidden = ?2 WHERE id = ?1").bind(id, hidden).run();
  return json({ ok: true, id, hidden: !!hidden });
}


function cleanIni(text) {
  const data = {};
  let sec = null;
  for (let line of text.split("\n")) {
    line = line.trim();
    const sc = line.indexOf(";");
    if (sc >= 0) line = line.slice(0, sc).trim();
    if (!line || line.startsWith("#") || line.startsWith("//")) continue;
    if (line.startsWith("[") && line.endsWith("]")) {
      const name = Object.keys(ALLOWED).find(k => k.toLowerCase() === line.slice(1, -1).trim().toLowerCase());
      sec = name || null;
      if (sec) data[sec] = data[sec] || {};
      continue;
    }
    const eq = line.indexOf("=");
    if (!sec || eq <= 0) continue;
    const key = ALLOWED[sec].find(k => k.toLowerCase() === line.slice(0, eq).trim().toLowerCase());
    const val = line.slice(eq + 1).trim().replace(/^"|"$/g, "");
    if (key && safeValue(val)) data[sec][key] = val;
  }
  let out = "";
  for (const sec of Object.keys(ALLOWED)) {
    const kv = data[sec];
    if (!kv || !Object.keys(kv).length) continue;
    out += `[${sec}]\n` + ALLOWED[sec].filter(k => k in kv).map(k => `${k} = ${kv[k]}\n`).join("");
  }
  return out;
}

function safeValue(v) {
  if (!v || v.length > 64) return false;
  if (WORDS.has(v.toLowerCase())) return true;
  if (v[0] === "#") return /^#([0-9a-f]{6}|[0-9a-f]{8})$/i.test(v);
  return v.split(",").every(p => /^\s*-?(\d+\.?\d*|\.\d+)(e-?\d+)?\s*$/i.test(p) && Math.abs(parseFloat(p)) <= 100000);
}

function oneLine(s, max) {
  return String(s || "").replace(/[\u0000-\u001f\u007f<>\[\];=\\/:*?"|]/g, "").replace(/\s+/g, " ").trim().slice(0, max).trim();
}
function b64url(s) {
  const b = atob(s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4));
  return Uint8Array.from(b, c => c.charCodeAt(0));
}
function esc(s) { return String(s).replace(/[*_`~|>@]/g, ""); }
function clientId(req) { const c = req.headers.get("X-QuestShade-Client") || ""; return /^[0-9a-f]{16,64}$/i.test(c) ? c.toLowerCase() : null; }
async function whoHash(req) { return (await sha((req.headers.get("CF-Connecting-IP") || "") + "|" + (clientId(req) || ""))).slice(0, 24); }
async function sha(s) {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map(b => b.toString(16).padStart(2, "0")).join("");
}
function newId() {
  const a = "abcdefghijkmnpqrstuvwxyz23456789", b = crypto.getRandomValues(new Uint8Array(8));
  return [...b].map(x => a[x % a.length]).join("");
}
async function notify(env, content) {
  if (!env.DISCORD_WEBHOOK) return;
  try {
    await fetch(env.DISCORD_WEBHOOK, { method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: "QuestShade Presets", content, allowed_mentions: { parse: [] } }) });
  } catch { }
}
function json(o, status = 200) {
  return new Response(JSON.stringify(o), { status, headers: {
    "Content-Type": "application/json; charset=utf-8", "Access-Control-Allow-Origin": "*" } });
}
function err(status, message) { return json({ error: message }, status); }
