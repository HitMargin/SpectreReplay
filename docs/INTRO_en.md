# SpectreReplay

**Record as you play. No need to re-run the level.**

A recording / playback / video-render mod for *A Dance of Fire and Ice*. Leave it on and play normally — the replay is already on disk the moment you clear the level. No second attempt just to have a capture.

Neither playback nor rendering is a re-enactment: both run on the game itself, so the frames are the game's own output and the hits are judged by the game. A replay looks **identical** to the run, not merely similar.

---

## What it does

- **Recording** — capture while you play; auto-saves on clear. F9 saves by hand, another hotkey discards the run you don't want. Filter what gets kept (full clears only / skip runs with a miss / also save on failure), keep backups, and optionally record the tap keysounds.
- **Playback** — replays in-game exactly as it happened, and can jump straight to the level it came from. Expand a file for song, difficulty, BPM, Accuracy, the full judgement spread and a per-hit timing chart.
- **Rendering** — frame-by-frame capture into a video with audio. Two frame sources (clean camera / game screen), four containers (mp4 / mkv / webm / mov), CPU or GPU encoding, four quality tiers. Also batch-converts existing videos.
- **Effect removal** — strips filters, particles, decorations, camera moves, track animations and more, all at decode time and per category. In-memory only; the chart file on disk is untouched.
- **Key remapping** — map the codes your controller or macro keyboard sends onto the keys the game expects.

The whole UI is self-drawn and follows the game's language — **all 14 supported**.

---

## Installation

1. Install [UnityModManager](https://www.nexusmods.com/site/mods/21) (0.33 required)
2. Put `SpectreReplay.dll` and `info.json` into `SpectreReplay/` inside the UMM mods folder
3. Press **Ctrl+F10** in game to open the settings

**Rendering needs ffmpeg**: place `ffmpeg.exe` in the mod folder, the game root, or somewhere on PATH. Recording and playback do not need it.

**Compatibility**: game r150 (2.7.0) · Unity 6000.3.25f1 · standalone, does not require Spectre (both can run together).

---

## Worth knowing

- With "game screen" as the frame source, **collapse the mod window** while rendering or it will be captured.
- Only replays recorded by this mod can be read. Upstream Spectre's older format cannot — that compatibility fallback amounted to leaving the key next to the lock, so it was deliberately removed.
- After turning effect removal off, the loaded level is still the stripped version in memory; saving is blocked with a message, and **re-entering the level** restores the original.

---

## About

Released under the **GNU General Public License v3**. The replay engine and render pipeline were ported from the GPL-3.0 [Spectre](https://github.com/adofaiex/Spectre) project; derived parts and third-party components are documented in `NOTICE`.

A Dance of Fire and Ice and its assets belong to their respective rights holders. This is an unofficial fan-made mod, not affiliated with or endorsed by them.
