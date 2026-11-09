
const API = "https://discord.com/api/v10";
const GUILD = "1569078623376515165";
const BOT_ID = "1569084025652650146";
const BUGS = "1569084793256280084", BUG_OPEN_TAG = "1569084793256280085";
const SUGGEST = "1569084794376036575";
const HELP_CH = "1569084792027480075";
const MAX_AGE_MS = 2 * 24 * 3600_000;
const SITE = "https://monkeonyt-glitch.github.io/QuestShade/";

export async function discordTick(env) {
  if (!env.DISCORD_TOKEN) return;
  await env.DB.prepare("CREATE TABLE IF NOT EXISTS answered (thread TEXT PRIMARY KEY, at INTEGER)").run();
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
