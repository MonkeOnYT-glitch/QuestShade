
const API = "https://discord.com/api/v10";
const GUILD = "1569078623376515165";
const BOT_ID = "1569084025652650146";
const BUGS = "1569084793256280084", BUG_OPEN_TAG = "1569084793256280085";
const SUGGEST = "1569084794376036575";
const HELP_CH = "1569084792027480075";
const MAX_AGE_MS = 2 * 24 * 3600_000;
const SITE = "https://monkeonyt-glitch.github.io/QuestShade/";

const GENERAL = "1569084684430868604", MODLOG = "1569084701615067216";
const JOIN_BURST = 6, JOIN_WINDOW_MS = 10 * 60_000;
const FLOOD_WINDOW_MS = 2 * 60_000, SPAM_REPEATS = 4, SPAM_LINKS = 3, SPAM_MENTIONS = 8, SPAM_BURST = 15;
const NEW_MEMBER_MS = 24 * 3600_000;
const LOCK_MS = 2 * 3600_000;
const SPAM_TIMEOUT_MS = 30 * 60_000;

export async function discordTick(env) {
  if (!env.DISCORD_TOKEN) return;
  try { await raidGuard(env); } catch (e) { console.log("raid guard: " + e); }
  try { await liftOldTimeouts(env); } catch (e) { console.log("lift timeouts: " + e); }
  try { await testPings(env); } catch (e) { console.log("test pings: " + e); }
  try { await roadmap(env); } catch (e) { console.log("roadmap: " + e); }
  try { await collectSuggestions(env); } catch (e) { console.log("suggestions: " + e); }
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS answered (thread TEXT PRIMARY KEY, at INTEGER)").run();
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS bug_reports (thread TEXT PRIMARY KEY, title TEXT, author TEXT, body TEXT, url TEXT, at INTEGER)").run();
  const active = await call(env, "GET", `/guilds/${GUILD}/threads/active`);
  for (const th of active?.threads || []) {
    if (th.parent_id !== BUGS && th.parent_id !== SUGGEST) continue;
    if (Date.now() - snowflakeTime(th.id) > MAX_AGE_MS) continue;
    if (th.owner_id === BOT_ID) continue;
    const done = await env.DB.prepare("SELECT 1 FROM answered WHERE thread = ?1").bind(th.id).first();
    if (done) continue;
    const first = await call(env, "GET", `/channels/${th.id}/messages/${th.id}`);
    const text = `${th.name}\n${first?.content || ""}`;
    const reply = th.parent_id === BUGS ? bugReply(text) : suggestionReply(text);
    const sent = await call(env, "POST", `/channels/${th.id}/messages`, {
      content: reply, allowed_mentions: { parse: [] }, message_reference: first?.id ? { message_id: first.id, fail_if_not_exists: false } : undefined,
    });
    if (!sent?.id) continue;
    if (th.parent_id === BUGS && !(th.applied_tags || []).includes(BUG_OPEN_TAG))
      await call(env, "PATCH", `/channels/${th.id}`, { applied_tags: [...(th.applied_tags || []), BUG_OPEN_TAG].slice(0, 5) });
    await env.DB.prepare("INSERT OR IGNORE INTO answered (thread, at) VALUES (?1, ?2)").bind(th.id, Date.now()).run();
    if (th.parent_id === BUGS)
      await env.DB.prepare("INSERT OR IGNORE INTO bug_reports (thread, title, author, body, url, at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
        .bind(th.id, th.name, first?.author?.username || th.owner_id, (first?.content || "").slice(0, 4000),
              `https://discord.com/channels/${GUILD}/${th.id}`, Date.now()).run();
  }
}

async function raidGuard(env) {
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS raid_state (k TEXT PRIMARY KEY, v INTEGER)").run();
  const now = Date.now();
  const members = await call(env, "GET", `/guilds/${GUILD}/members?limit=1000`);
  if (!Array.isArray(members)) return;
  const joinedAt = new Map(members.map(m => [m.user.id, Date.parse(m.joined_at)]));

  const recent = members.filter(m => !m.user.bot && now - Date.parse(m.joined_at) < JOIN_WINDOW_MS);
  const locked = await env.DB.prepare("SELECT v FROM raid_state WHERE k = 'locked_until'").first();
  if (recent.length >= JOIN_BURST && !(locked && locked.v > now)) {
    const until = new Date(now + LOCK_MS).toISOString();
    const ok = await call(env, "PUT", `/guilds/${GUILD}/incident-actions`, { invites_disabled_until: until, dms_disabled_until: until });
    await env.DB.prepare("INSERT OR REPLACE INTO raid_state (k, v) VALUES ('locked_until', ?1)").bind(now + LOCK_MS).run();
    await call(env, "POST", `/channels/${MODLOG}/messages`, { allowed_mentions: { parse: [] },
      content: `🚨 **Possible raid:** ${recent.length} people joined in the last 10 minutes. ${ok ? "Invites and DMs are **paused for 2 hours**." : "Couldn't pause invites, do it by hand (Server Settings > Safety Setup)."}\n` +
               `Joined: ${recent.map(m => m.user.username).join(", ").slice(0, 1500)}` });
  }

  const msgs = await call(env, "GET", `/channels/${GENERAL}/messages?limit=100`);
  if (!Array.isArray(msgs)) return;
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS bot_timeouts (uid TEXT PRIMARY KEY, until INTEGER, reason TEXT)").run();
  const byAuthor = new Map();
  for (const m of msgs) {
    if (m.author.bot || now - snowflakeTime(m.id) > FLOOD_WINDOW_MS) continue;
    const j = joinedAt.get(m.author.id);
    if (j === undefined || now - j > NEW_MEMBER_MS) continue;
    if (!byAuthor.has(m.author.id)) byAuthor.set(m.author.id, { name: m.author.username, msgs: [] });
    byAuthor.get(m.author.id).msgs.push(m);
  }
  for (const [uid, a] of byAuthor) {
    const why = spamReason(a.msgs);
    if (!why) continue;
    const until = now + SPAM_TIMEOUT_MS;
    await call(env, "PATCH", `/guilds/${GUILD}/members/${uid}`, { communication_disabled_until: new Date(until).toISOString() });
    await env.DB.prepare("INSERT OR REPLACE INTO bot_timeouts (uid, until, reason) VALUES (?1, ?2, ?3)").bind(uid, until, why).run();
    const bad = a.msgs.filter(m => isSpammy(m)).map(m => m.id);
    if (bad.length >= 2) await call(env, "POST", `/channels/${GENERAL}/messages/bulk-delete`, { messages: bad.slice(0, 100) });
    await call(env, "POST", `/channels/${MODLOG}/messages`, { allowed_mentions: { parse: [] },
      content: `🔇 **${a.name}** (<@${uid}>) joined in the last day and looks like a spam bot in <#${GENERAL}> (${why}): timed out for 30 min${bad.length >= 2 ? ", spam deleted" : ""}. Remove the timeout if this was a mistake, ban them if it was a raid.` });
  }
}

function linkCount(m) { return ((m.content || "").match(/https?:\/\/|discord\.gg\/|discord(app)?\.com\/invite/gi) || []).length; }
function mentionCount(m) { return (m.mentions || []).length + (m.mention_everyone ? 5 : 0); }
function isSpammy(m) { return linkCount(m) > 0 || mentionCount(m) >= 3; }
function norm(s) { return (s || "").toLowerCase().replace(/\s+/g, " ").trim(); }

function spamReason(msgs) {
  const counts = new Map();
  for (const m of msgs) { const k = norm(m.content); if (k.length >= 4) counts.set(k, (counts.get(k) || 0) + 1); }
  const repeats = Math.max(0, ...counts.values());
  if (repeats >= SPAM_REPEATS) return `sent the same message ${repeats} times`;
  const links = msgs.reduce((n, m) => n + linkCount(m), 0);
  if (links >= SPAM_LINKS) return `posted ${links} links or invites in 2 minutes`;
  const mentions = msgs.reduce((n, m) => n + mentionCount(m), 0);
  if (mentions >= SPAM_MENTIONS) return `pinged ${mentions} people in 2 minutes`;
  if (msgs.length >= SPAM_BURST) return `posted ${msgs.length} messages in 2 minutes`;
  return null;
}

async function liftOldTimeouts(env) {
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS bot_state (k TEXT PRIMARY KEY, v TEXT)").run();
  const done = await env.DB.prepare("SELECT v FROM bot_state WHERE k = 'old_timeouts_lifted'").first();
  if (done) return;
  const log = await call(env, "GET", `/channels/${MODLOG}/messages?limit=100`);
  if (!Array.isArray(log)) return;
  const lifted = [];
  for (const m of log) {
    if (m.author?.id !== BOT_ID || !(m.content || "").startsWith("🔇") || !/timed out for 24 h/.test(m.content)) continue;
    const uid = (m.content.match(/<@!?(\d+)>/) || [])[1];
    if (!uid || lifted.includes(uid)) continue;
    if (await call(env, "PATCH", `/guilds/${GUILD}/members/${uid}`, { communication_disabled_until: null })) lifted.push(uid);
  }
  if (lifted.length)
    await call(env, "POST", `/channels/${MODLOG}/messages`, { allowed_mentions: { parse: [] },
      content: `✅ The spam filter was too strict and timed out people who were just chatting. Lifted those timeouts: ${lifted.map(u => `<@${u}>`).join(", ")}. It now only acts on repeated messages, link spam or mass pings.` });
  await env.DB.prepare("INSERT OR REPLACE INTO bot_state (k, v) VALUES ('old_timeouts_lifted', ?1)").bind(String(lifted.length)).run();
}

const ROADMAP = [
  "🗺️ **QuestShade roadmap**",
  "What's planned next. Order can change, and ideas from <#" + SUGGEST + "> with the most 👍 move up.",
  "",
  "**🔨 Working on now**",
  "• More ReShade shaders converting 1:1",
  "",
  "**📅 Planned**",
  "• 🎞️ **Timeline support**: line up camera moves and shots over time, then play them back for smooth cinematic clips",
  "• 📡 **Live streaming support**: stream the QuestShade camera view straight from your Quest",
  "• 🌗 Shadows from every light (lamps, flashlights), not just the sun",
  "• 🐢 Slow-mo modes (a little, more, full) with cinematic auto zoom",
  "• 🩸 PCVR-style hurt and death effects",
  "• ⛑️ Helmet cam that shows your helmet, and more bodycam styles",
  "• ✨ Glowing Nullbodies",
  "",
  "**✅ Recently added**",
  "• Vertical (9:16) photos and videos for TikTok / Shorts",
  "• Put the camera straight into your hand from BoneMenu",
  "• Switch off the trigger photo / record controls",
  "• Lens flare in your headset",
  "• Smooth headset bloom (no grain or rings)",
  "• Body shadow, flashlight beams and real flashlight lighting",
  "• ReShade LUT support (prod80 Cinetools + Bonus packs built in)",
  "• Big performance fix (1.7.25)",
  "",
  "Got an idea? Post it in <#" + SUGGEST + ">.",
].join("\n");

async function roadmap(env) {
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS bot_state (k TEXT PRIMARY KEY, v TEXT)").run();
  const get = async k => (await env.DB.prepare("SELECT v FROM bot_state WHERE k = ?1").bind(k).first())?.v;
  const set = (k, v) => env.DB.prepare("INSERT OR REPLACE INTO bot_state (k, v) VALUES (?1, ?2)").bind(k, v).run();
  let ch = await get("roadmap_channel");
  if (!ch) {
    const chans = await call(env, "GET", `/guilds/${GUILD}/channels`);
    if (!Array.isArray(chans)) return;
    ch = chans.find(c => c.type === 0 && /roadmap/i.test(c.name || ""))?.id;
    if (!ch) {
      const parent = chans.find(c => c.id === GENERAL)?.parent_id;
      const everyone = GUILD;
      ch = (await call(env, "POST", `/guilds/${GUILD}/channels`, {
        name: "roadmap", type: 0, topic: "What's coming to QuestShade", parent_id: parent || undefined,
        permission_overwrites: [{ id: everyone, type: 0, deny: "2048", allow: "1024" }, { id: BOT_ID, type: 1, allow: "3072" }],
      }))?.id;
    }
    if (!ch) return;
    await set("roadmap_channel", ch);
  }
  const msg = await get("roadmap_message");
  const hash = String(ROADMAP.length) + ":" + ROADMAP.slice(-40);
  if (msg && (await get("roadmap_hash")) === hash) return;
  if (msg) {
    if (await call(env, "PATCH", `/channels/${ch}/messages/${msg}`, { content: ROADMAP, allowed_mentions: { parse: [] } })) { await set("roadmap_hash", hash); return; }
  }
  const sent = await call(env, "POST", `/channels/${ch}/messages`, { content: ROADMAP, allowed_mentions: { parse: [] } });
  if (!sent?.id) return;
  await call(env, "PUT", `/channels/${ch}/pins/${sent.id}`);
  await set("roadmap_message", sent.id);
  await set("roadmap_hash", hash);
}

async function collectSuggestions(env) {
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS suggestions (thread TEXT PRIMARY KEY, title TEXT, author TEXT, body TEXT, votes INTEGER, at INTEGER)").run();
  const last = await env.DB.prepare("SELECT v FROM bot_state WHERE k = 'suggest_scan'").first();
  if (last && Date.now() - Number(last.v) < 15 * 60_000) return;
  await env.DB.prepare("INSERT OR REPLACE INTO bot_state (k, v) VALUES ('suggest_scan', ?1)").bind(String(Date.now())).run();
  const threads = [];
  const active = await call(env, "GET", `/guilds/${GUILD}/threads/active`);
  for (const th of active?.threads || []) if (th.parent_id === SUGGEST) threads.push(th);
  const archived = await call(env, "GET", `/channels/${SUGGEST}/threads/archived/public?limit=50`);
  for (const th of archived?.threads || []) threads.push(th);
  for (const th of threads.slice(0, 40)) {
    const first = await call(env, "GET", `/channels/${th.id}/messages/${th.id}`);
    const votes = (first?.reactions || []).find(r => r.emoji?.name === "👍")?.count || 0;
    await env.DB.prepare("INSERT OR REPLACE INTO suggestions (thread, title, author, body, votes, at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)")
      .bind(th.id, th.name, first?.author?.username || th.owner_id, (first?.content || "").slice(0, 2000), votes, snowflakeTime(th.id)).run();
  }
}

function bugReply(text) {
  const t = text.toLowerCase();
  const has = re => re.test(t);
  const lines = ["Thanks for the report! 🛠️ A few things help a lot:"];
  if (!has(/\b(quest|pc|steam)\b/)) lines.push("• **Quest or PC?** (and which headset)");
  if (!has(/\b1\.\d+\.\d+\b/)) lines.push("• **QuestShade version**: it's in the popup when the game starts, or at the top of the log");
  if (!has(/\bmods?\b/)) lines.push("• **Other mods** you have installed");
  lines.push("• **What you did right before** it happened");
  if (has(/crash|froze|freez|closed|kicked|black screen/))
    lines.push("• Crashes: please attach **`MelonLoader/Latest.log`** from right after it happened (on Quest: `/sdcard/MelonLoader/com.StressLevelZero.BONELAB/MelonLoader/Latest.log`, SideQuest can grab it)");
  else
    lines.push("• If you can, attach **`MelonLoader/Latest.log`** (on Quest: `/sdcard/MelonLoader/com.StressLevelZero.BONELAB/MelonLoader/`)");
  if (has(/camera|lens|preview|chest|selfie|tripod/)) lines.push("• Camera bugs: which **camera mode** (Hand, Selfie, Chest...) and a screenshot or clip if you have one");
  if (has(/record|video|mp4|audio|sound|gallery/)) lines.push("• Video bugs: how long the clip was, and whether game audio / mic were on");
  if (has(/preset|lens flare|bloom|vignette|grain|look/)) lines.push("• Look bugs: which **preset**, and a photo with effects on vs off helps");
  if (has(/update|updater|version/)) lines.push("• Updates: QuestShade updates itself while the game starts; restart once if it says it downloaded something");
  lines.push("", "It's tagged **Open** until it's fixed. Stuck rather than broken? Try <#" + HELP_CH + ">.");
  return lines.join("\n");
}

function suggestionReply(text) {
  const t = text.toLowerCase();
  const lines = ["Thanks for the idea! 💡 Everyone: react 👍 if you want this too, it helps decide what gets made first."];
  if (/preset|look|filter|reshade|shader/.test(t))
    lines.push("", "Tip: you can already build your own look in the headset (**Presets > Edit preset > New blank preset**) and share it (**Share publicly**). Shared presets also show up on the site: " + SITE + "#presets");
  if (/camera|mode|fov|zoom|record/.test(t))
    lines.push("", "Camera tip: modes, field of view and the chest cam are in **BoneMenu > QuestShade > Camera > View**.");
  return lines.join("\n");
}

async function call(env, method, path, body) {
  const r = await fetch(API + path, {
    method,
    headers: { "Authorization": `Bot ${env.DISCORD_TOKEN}`, "Content-Type": "application/json", "User-Agent": "QuestShadeBot (https://github.com/MonkeOnYT-glitch/QuestShade, 1.0)" },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) { console.log(`discord ${method} ${path} -> ${r.status} ${await r.text()}`); return null; }
  return r.status === 204 ? {} : r.json();
}

function snowflakeTime(id) { return Number(BigInt(id) >> 22n) + 1420070400000; }

const TEST_CHANNEL = "public-testing", TEST_ROLE = "Testing Pings", CHECK = "✅";

async function testPings(env) {
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS bot_state (k TEXT PRIMARY KEY, v TEXT)").run();
  const get = async k => (await env.DB.prepare("SELECT v FROM bot_state WHERE k = ?1").bind(k).first())?.v;
  const set = (k, v) => env.DB.prepare("INSERT OR REPLACE INTO bot_state (k, v) VALUES (?1, ?2)").bind(k, v).run();

  let ch = await get("test_channel");
  if (!ch) {
    const chans = await call(env, "GET", `/guilds/${GUILD}/channels`);
    ch = (chans || []).find(c => c.type === 0 && (c.name || "").includes(TEST_CHANNEL))?.id;
    if (!ch) { console.log("test pings: no #" + TEST_CHANNEL + " channel"); return; }
    await set("test_channel", ch);
  }
  let role = await get("test_role");
  if (!role) {
    const roles = await call(env, "GET", `/guilds/${GUILD}/roles`);
    if (!Array.isArray(roles)) return;
    role = roles.find(r => r.name === TEST_ROLE)?.id;
    if (!role) role = (await call(env, "POST", `/guilds/${GUILD}/roles`, { name: TEST_ROLE, permissions: "0", mentionable: false }))?.id;
    if (!role) return;
    await set("test_role", role);
  }
  let msg = await get("test_message");
  if (!msg) {
    const sent = await call(env, "POST", `/channels/${ch}/messages`, {
      content: `@everyone react with ${CHECK} if you want public testing pings`, allowed_mentions: { parse: ["everyone"] } });
    if (!sent?.id) return;
    msg = sent.id;
    await set("test_message", msg);
  }
  const emoji = encodeURIComponent(CHECK);
  if (!(await get("test_reacted"))) {
    if (await call(env, "PUT", `/channels/${ch}/messages/${msg}/reactions/${emoji}/@me`)) await set("test_reacted", "1");
  }

  const reacted = new Set();
  for (let after = "0", page = 0; page < 20; page++) {
    const users = await call(env, "GET", `/channels/${ch}/messages/${msg}/reactions/${emoji}?limit=100&after=${after}`);
    if (!Array.isArray(users)) return;
    for (const u of users) if (!u.bot) reacted.add(u.id);
    if (users.length < 100) break;
    after = users[users.length - 1].id;
  }
  const members = await call(env, "GET", `/guilds/${GUILD}/members?limit=1000`);
  if (!Array.isArray(members)) return;
  let changes = 0;
  for (const m of members) {
    if (m.user.bot || changes >= 20) continue;
    const has = (m.roles || []).includes(role), wants = reacted.has(m.user.id);
    if (wants && !has) { await call(env, "PUT", `/guilds/${GUILD}/members/${m.user.id}/roles/${role}`); changes++; }
    else if (!wants && has) { await call(env, "DELETE", `/guilds/${GUILD}/members/${m.user.id}/roles/${role}`); changes++; }
  }
}
