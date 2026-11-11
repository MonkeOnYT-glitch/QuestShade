# QuestShade

ReShade for BONELAB on Quest, plus a camera you can grab and film with. Works on PC too.

## Features

- Colour grading, bloom, vignette, sharpening, grain, chromatic aberration, fisheye, CRT and scanlines
- 12 built-in presets, a preset editor in the headset, and a public preset browser
- Imports ReShade presets (drop the .ini in `UserData/QuestShade/ReShade/`)
- Eye adaptation, brighter dark maps, stronger flashlights and sun shadows
- A physical camera with hand, selfie, first person, helmet, chest, police bodycam and third person modes
- Photos and videos (with game audio and mic) go straight to your Quest gallery

## Install

- Quest: put `QuestShade.dll` in `/sdcard/MelonLoader/com.StressLevelZero.BONELAB/Mods/`
- PC: put it in `BONELAB/Mods/`
- Optional: put `QuestShadeUpdater.dll` in `Plugins/` so updates install when the game starts

Needs BoneLib. Everything is in BoneMenu under QuestShade. On the camera, tap the trigger for a photo and hold it to record.

## Thanks

- GravWarden's BONELAB Cinematic Camera, for the idea of using a real item as the camera body
- The reshade-shaders and SweetFX authors, whose colour shaders are ported here
- BoneLib

## Building

- Shaders: `Unity.exe -batchmode -nographics -projectPath unity -executeMethod BuildBundles.Build -quit` (Unity 2021.3.16f1)
- Mod: `dotnet build QuestShade.csproj -c Release -p:QSTarget=Quest` (or `PC`)
