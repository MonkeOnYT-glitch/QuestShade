
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
const FLOOD_MSGS = 8, FLOOD_WINDOW_MS = 3 * 60_000;
const NEW_MEMBER_MS = 24 * 3600_000;
const LOCK_MS = 2 * 3600_000;

export async function discordTick(env) {
  if (!env.DISCORD_TOKEN) return;
  try { await raidGuard(env); } catch (e) { console.log("raid guard: " + e); }
  try { await testPings(env); } catch (e) { console.log("test pings: " + e); }
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
  const byAuthor = new Map();
  for (const m of msgs) {
    if (m.author.bot || now - snowflakeTime(m.id) > FLOOD_WINDOW_MS) continue;
    const j = joinedAt.get(m.author.id);
    if (j === undefined || now - j > NEW_MEMBER_MS) continue;
    if (!byAuthor.has(m.author.id)) byAuthor.set(m.author.id, { name: m.author.username, ids: [] });
    byAuthor.get(m.author.id).ids.push(m.id);
  }
  for (const [uid, a] of byAuthor) {
    if (a.ids.length < FLOOD_MSGS) continue;
    await call(env, "PATCH", `/guilds/${GUILD}/members/${uid}`, { communication_disabled_until: new Date(now + 24 * 3600_000).toISOString() });
    if (a.ids.length >= 2) await call(env, "POST", `/channels/${GENERAL}/messages/bulk-delete`, { messages: a.ids.slice(0, 100) });
    await call(env, "POST", `/channels/${MODLOG}/messages`, { allowed_mentions: { parse: [] },
      content: `🔇 **${a.name}** (<@${uid}>) joined in the last day and posted ${a.ids.length} messages in 3 minutes in <#${GENERAL}>: timed out for 24 h, messages deleted. Ban them if it was a raid.` });
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
    lines.push("", "Tip: you can already build your own look in the headset (**Effects > Edit preset > New blank preset**) and share it (**Share publicly**). Shared presets also show up on the site: " + SITE + "#presets");
  if (/camera|mode|fov|zoom|record/.test(t))
    lines.push("", "Camera tip: modes, field of view and the chest cam are in **BoneMenu > QuestShade > Camera > Mode + view**.");
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
