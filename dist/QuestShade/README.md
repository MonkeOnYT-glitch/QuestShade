# QuestShade

ReShade, but for BONELAB on Quest. Plus a camera you can actually pick up and film with.

I wanted my Quest footage to look like the stuff people make on PC with ReShade, so I made this. It runs on standalone Quest (I play on a 3S). It works on PC too.

## What it does

**Effects you can see in-game:**
- Colour grading, LUTs, vignette, sharpening, grain, chromatic aberration, lens distortion, CRT, scanlines and glitch.
- 10 presets to start with: Bodycam, Night Ops, Night Vision, CCTV, Punchy, Cinematic, Natural, Noir, Vivid and Warm.

**Bloom and depth of field** on everything the camera shoots:
- The focus follows what you point at.
- It doesn't pump in and out while you're running around.
- Bloom adjusts itself to each map, so bright maps don't turn into a white mess.

**The camera:**
- A camera rig you grab with either hand. Squeeze near a handle and it snaps into your hand, straight, every time. Let go and it just floats there. It can't be knocked around and won't hurt you.
- Tap trigger to take a photo. Hold trigger to record. There's a little label on the camera so you don't forget.
- Modes: handheld, selfie, first person, chest (bodycam), third person and tripod.
- Videos record with game audio and your mic if you want, and they're encoded the same way the Quest's own recordings are. They come out looking like the camera's preview screen, not lighter.
- Everything goes straight into your Quest gallery (same way LIV's camera does it). No file manager, no extra permissions.
- Your own head and menus never show up in the shot.

**Make your own presets in the headset.** Effects > Edit preset > **New preset**, then move the sliders: exposure, contrast, shadows, midtones, highlights, saturation, warmth, tint, hue, sharpness, bloom, vignette, film grain and colour fringe. You see every change live and it saves as you go. "Copy this preset" starts from the one you have on.

**Bring your ReShade presets.** Drop a ReShade `.ini` into `UserData/QuestShade/ReShade/` and it shows up in the preset list.
- If it uses LUT textures, throw them in `ReShade/Textures/`.
- The standard ReShade colour shaders are matched exactly.
- Limits' presets get the hand-tuned Quest port instead of the automatic translation, so they look the way they were meant to.
- Everything else gets the closest thing that can run on a Quest.
- Stuff that just can't run on Quest (AO, RTGI, reflections and so on) gets skipped, and the preset file tells you what was skipped.

**Works with Timeline** (the replay mod), if you have it. A **Timeline** folder shows up in the QuestShade menu (only when Timeline is installed) with Timeline's own Play / Pause / Stop / Record, takes, saving and settings, so you never have to bounce between two menus. Hit **Next person** and the take pops up paused with everyone loaded in. The person you're filming gets a red outline and a "FILMING 1/3" marker (only you see those, never the video). Pick a view (their POV, chest cam, or over the shoulder), then press Play. The camera records the playback and stops when it ends, your own body is left out of the shot, and it never records while Timeline is recording a take. Timeline isn't required.

**Fusion lobbies:** the camera turns itself off in a Fusion lobby so it stays effects-only (you can switch that off in Camera → Settings).

**It won't tank your game.** If a map gets really heavy, QuestShade backs off on its own and comes back when things calm down.

## Install

- **Quest:** put `QuestShade.dll` in `/sdcard/MelonLoader/com.StressLevelZero.BONELAB/Mods/`
- **PC:** put it in `BONELAB/Mods/`
- **Optional, recommended:** also put `QuestShadeUpdater.dll` in the `Plugins/` folder next to `Mods/`. It updates QuestShade while the game boots, so new versions load right away. (QuestShade adds it for you on its next update if you skip this.)

You'll want **BoneLib**, which gives you the menu and the grabbable camera.

Your settings live in `UserData/QuestShade/`. You can edit the ini while the game is running and it updates live.

## How to use it

Everything is in **BoneMenu → QuestShade**:
- **Camera:** open it, take photos, record, pick a mode, audio settings.
- **Effects:** turn things on and off, pick a preset, and set how strong the bloom and blur are.

Shortcuts:
- Click both thumbsticks: open / close the camera.
- Hold the right stick: effects on / off.
- Double-tap the right stick: next preset.

## Thanks
- **BONELAB Cinematic Camera** by GravWarden, for the idea of using a real item as the camera's body.
- The **reshade-shaders** / **SweetFX** folks (crosire, CeeJay.dk and everyone else), whose colour shaders are ported here.
- **BoneLib**, for the menu.
