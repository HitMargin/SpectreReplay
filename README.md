# SpectreReplay

**English** | [简体中文](README_zh.md)

Replay recording, replay playback and video rendering for *A Dance of Fire and Ice* — record as you play, no re-run needed.

SpectreReplay is a UnityModManager mod (mod id `com.spectre.replay`). It records every run while you play it, plays the recording back inside the live game, and renders a replay to a video file with the audio track included. Nothing is re-simulated behind your back: playback and rendering both run on the game itself, so what you see is what the game produced.

## Features

### Recording

- **Records while you play.** You do not have to replay a level to capture it — the recorder is armed before you start and collects from the first frame of the run.
- **Saves automatically.** A completed run is written to disk on its own; nothing to click.
- **Manual save.** A hotkey saves the current run mid-level, including the runs you abandon on purpose.
- **Cancel save.** Another hotkey drops the run being recorded — for warm-ups and retries you do not want in the folder.
- **Protection against junk and lost runs.** Runs can be skipped when the game played itself (auto) or when the run contains misses, and a run can still be saved when you fail a level instead of finishing it.
- **Backups.** Optionally keep a second copy of every saved replay, so a bad file never costs you the recording. The copy is taken from the finished file rather than saved a second time, and it is skipped when the main save itself failed.
- **Keyboard sound (optional).** Microphone input can be recorded alongside the run and attached to the replay.

### Playback

- **Replays play in-game.** The recording drives the real game: hit timings, floors and lifecycle are all restored frame by frame.
- **Load from file.** A file list shows every replay in the save directory; pick one and it is armed. Two container variants are produced: `.sprp` (encrypted, the default) and `.psprp` (unencrypted, for debugging).
- **Open the level directly.** A loaded replay knows which chart it came from, so the mod can open that level for you — editor charts and official levels alike — and land on the right floor once it is ready.
- **Unload.** Drop the armed replay without leaving the level.
- **Key injection.** Keys are injected into the game's input layer, so the game reacts to them exactly as it reacts to your keyboard. Both the synchronous input path and the asynchronous input path are supported; with async input enabled, timestamps come from the game's own input stamps instead of frame boundaries and are noticeably more accurate.

### Video rendering

- **Renders the replay to a file.** The level is replayed frame by frame with every frame captured deliberately, then encoded together with its audio track.
- **Two frame sources.** *Game view* captures what the game shows, UI included, at window resolution. *Clean camera* renders the game's camera chain into an offscreen texture with no interface at all and at any resolution you like, including supersampling.
- **Encoder and quality presets.** CPU (libx264) or hardware (NVENC) encoding, four speed/quality tiers, automatic fallback to CPU when no usable hardware encoder is found.
- **Four containers: mp4, mkv, webm, mov.** Codec and audio codec are chosen to match the container (webm, for instance, re-encodes to VP9 + Opus).
- **Format conversion.** Every video in the output directory can be batch-converted to another container, independent of the format the next render will use; files already in the target format are skipped.
- **Tail seconds.** Extra seconds recorded after the chart ends, so the ending animation is not cut off.
- **Deterministic render clock (experimental, off by default).** Replaces the time source the game reads with a frame-index-driven clock, which removes audio-clock jitter from the output.

### Effect removal

- **Strips chart effects before they run.** Filters and flashes, particles, decorations, camera moves, planet orbit, track animation and colour, hit and hold sounds, checkpoints, input events, frame-rate changes and repeat events can each be removed independently, so a chart can be watched clean or rendered on a machine that cannot keep up with it.
- **Applied at chart decode.** Events are dropped from the decoded level data in one place rather than by intercepting each event type at runtime, so nothing is left behind and patching stays out of it.
- **Does not touch the file on disk.** It edits the in-memory chart only. Saving from the editor is blocked while it is active, so previewing once does not write the stripped chart back over the original; overwriting is a separate switch that is off by default.
- **Keeps conditional decorations.** Unless you ask for *all* decorations to go, decorations referenced by conditional events are kept — otherwise those branches could never fire and the chart would look broken.

### Audio

- **Exact capture from the mixdown bus.** The soundtrack, hit sounds and hold sounds are captured from the game's final mixing bus, not re-mixed by guesswork, and muxed into the rendered file as its audio track.

### Settings interface

- **Fully self-drawn controls.** Every control is drawn by the mod itself — rounded panels with nine-slice textures, custom sliders, toggles and text fields — so it looks the same regardless of the game's UI scaling.
- **Hover explanations.** Each option explains itself on hover, including the input the game must have enabled for that option to do anything.
- **Five pages** (Recording, Playback, Video Render, Effects, About), opened with a hotkey, and the interface follows the game's language — all **14 languages** the game ships with (Simplified/Traditional Chinese, English, Japanese, Korean, Spanish, Portuguese, French, German, Polish, Czech, Romanian, Russian, Vietnamese).

## How it works

### The patch layer

Everything the mod does to the game goes through patches, and patches are declared rather than registered by hand. Each patch method carries an attribute naming its target type and member, the injection point (prefix, postfix, transpiler, finalizer), a priority, the game-version window it applies to, and the feature key it belongs to. A host scans this at load time, resolves each target against the live game assembly, and mounts only what resolves and is in range.

The important part is how feature switches are implemented: **the feature predicate is evaluated when mounting, and a false predicate means the patch is never mounted at all.** It is not an early `return` inside the patch body, and it is not a flag checked on every call. That distinction is what makes the switches trustworthy:

- Flipping a switch takes effect immediately, in the same frame; nothing lingers from the previous state.
- A disabled feature has *no* patched IL in the game, so there is no residual overhead and no half-applied state to reason about.
- Unmounting restores the original method body exactly, which means toggling repeatedly cannot drift.

Version gating is part of the same mechanism. A patch that targets an API that only exists from build X onwards, or a signature that changed at build Y, carries its own version window; the host compares it against the loaded game build and skips patches outside it. Targets that fail to resolve are collected and reported in the settings page rather than swallowed, because a patch that silently fails to mount looks exactly like a feature that does not work.

### Recording

While you play, the recorder samples once per frame and encodes the run into a compact binary stream: for each frame, the key state and the judgement results — hit error, floor index, timestamps — plus the run metadata needed to reproduce the context (chart identity, difficulty, speed, and the per-hit context needed to recompute margins). The stream is then compressed (Brotli quality 6, in a small wrapper that records the algorithm and the original length) and encrypted before it is written to disk. Payloads under 512 bytes are stored raw, and so is anything that fails to compress smaller — the wrapper makes the choice per file, so the reader never has to guess.

The container begins with a magic value, a format version, and — from version 3 on — the key-derivation iteration count. A replay from a newer build is rejected cleanly instead of being half-parsed into something misleading.

#### Design note: the container format

Encryption here exists to keep casual edits out of the file, not to defend against someone determined to read it — the passphrase ships inside the DLL, and no amount of iteration count changes that. So the design targets the three things that *are* fixable:

- **Key derivation is salted and iterated** (PBKDF2-HMAC-SHA256, per-file random salt), which raises the cost of recovering the key from the assembly and defeats precomputed tables.
- **A fresh random IV per file.** The upstream design used a fixed IV derived from a constant string, which made encryption deterministic: two identical runs produced byte-identical files. That is a worse leak than a weak key, because it turns "I have two identical results" into an observable fact.
- **Encrypt-then-MAC.** HMAC-SHA256 covers magic, version, iteration count, salt, IV and ciphertext, and the MAC is verified *before* decryption — so a tampered file is rejected at the first step instead of being fed to CBC to produce garbage (or, one time in ~256, garbage that passes the padding check).

Version 3 writes the iteration count into the header so it can be tuned later without another format break; version 2 files pin the old 60 000 iterations, because that value is not stored in their header.

The compressor is versioned independently of the container: the wrapper's first byte names the algorithm (raw / gzip / Brotli), and a payload whose first byte is `0x1F` is treated as a bare gzip stream from the pre-wrapper layout. That older layout is still read, because replays written before Brotli was introduced carry it.

The read path accepts only this container. Replays recorded by upstream Spectre with its own fixed-key format, and replays saved by this mod before the 2026-10-18 rewrite, are **not readable** — that compatibility fallback was removed deliberately, since a decryption routine compiled into the DLL keeps the key right next to the lock.

Keeping the payload compact matters beyond file size: the recorder runs on the main thread during gameplay, so the per-frame cost is bounded and the file stays small enough to keep hundreds of runs.

### Playback

On load, the file is decrypted (MAC verified first), decompressed and decoded into a timeline of key events and hit contexts. During playback the mod replays that timeline against the game's own frame update: the recorded state overrides the live state each frame (planet angle, floor, auto flag, judgement state), and keys are injected into the input layer at the timestamps the recording says they happened. This is why a replay looks identical to the run rather than merely similar: the game's real hit detection runs on the injected input, so the visuals are produced by the same code that produced them the first time.

Two input paths are supported. The legacy path injects key events into the game's keyboard state; the asynchronous path feeds the game's async input queue, which is what the game uses for accurate hit timing when that option is on. Injecting through the queue rather than synthesising key state directly is what makes the timing match a human run instead of the frame boundary.

### Video rendering

Rendering treats the replay as a film strip rather than a live session:

1. The game timeline is pinned. The render job fixes a song position and advances it by exactly one output frame per captured frame, so the game's systems (track motion, camera events, judgements, hit-sound scheduling) are stepped deterministically instead of being driven by the wall clock.
2. Each step, one frame is captured — either through the screen-capture path or by rendering the game's camera chain into an offscreen texture — converted to the byte layout ffmpeg expects and pushed into the encoder's stdin. Writing to that pipe happens on a background thread so a slow disk never stalls the game loop.
3. Each step, audio is captured too, from the game's mixdown bus, and written as 32-bit float WAV.
4. At the end, the video track and the WAV are muxed into the requested container, with a codec pair that container actually accepts.

The optional deterministic clock goes one step further and replaces the time source itself: a transpiler rewrites the game's reads of the audio DSP clock and unscaled time so they return frame-index-derived values. The game then drives its own beat tracking, camera events and countdowns from the pinned clock, which removes the last few milliseconds of jitter at the cost of touching game time globally — which is why it is off by default and marked experimental.

#### Design note: audio capture buffers must match the frame

Unity's `AudioRenderer` consumes exactly as many samples from its capture ring buffer as the length of the array you hand it. It is tempting to allocate one generously sized buffer and reuse it every frame to avoid per-frame allocations — and that is a silent failure: if the array is longer than the samples available this frame, the read pointer is pushed past the write pointer, and from that moment on the capture reports zero available samples and the audio clock freezes. The output is a video whose remaining audio is silence, with no error anywhere.

So the buffer length has to be *exactly* this frame's sample count, per channel, times the channel count. The real frame size fluctuates between 128, 256 and 512 samples as the audio callback jitters, so the native buffer is reallocated when it changes; it lives in unmanaged memory, which makes that cheap. Reusing the buffers that only copy and flush the data — the managed `float[]` and `byte[]` — is fine, because their length carries no meaning for the renderer. The distinction between "buffer I hand to Unity" and "buffer I copy into" is the whole design.

### Audio track

The mixdown capture is started when the render job starts and stopped when it ends. Silence is not treated as failure immediately: the track restarts the capture a bounded number of times (the audio callback can miss its first frames during a scene transition) and only then declares the track dead, which surfaces as a specific error message instead of a silent video with no audio.

## Tech stack

- **C# / .NET Standard 2.1** for the mod assembly — the target the game's Mono runtime loads.
- **Unity 6000.3.25f1**, game build **r150**, **UnityModManager 0.33**.
- **Harmony** for runtime method patching, including transpilers.
- **Self-drawn IMGUI** for the whole settings UI: rounded nine-slice background textures, custom sliders, toggles and text fields — no stock controls.
- **ffmpeg** as an external encoder, distributed with the mod: libx264 for CPU encoding, with hardware (NVENC) fallback when available.
- **Offline verification gate**: a `net10.0-windows` test harness that links the real replay codec and audio/format logic and asserts behaviour without launching the game, plus a mutation suite that reintroduces each previously fixed bug and checks that the assertions actually go red. An assertion that cannot fail is treated as a bug in the test.

## Build and install

Requirements: the .NET SDK **10.0 or newer** (the solution file `SpectreReplay.slnx` and the
`net10.0-windows` test harness both require SDK 10), and the managed assemblies of the installed game.

1. Place the game's managed assemblies (`Assembly-CSharp.dll`, `UnityEngine.*.dll`, `0Harmony.dll`, `UnityModManager.dll`, `Newtonsoft.Json.dll`, …) in a folder named `adofai-libs` next to this repository — that is where the project file expects them. They are referenced with `Private=false`, so they are never copied into the build output.
2. Build:

   ```sh
   dotnet build SpectreReplay/SpectreReplay.csproj -c Release
   ```

3. Install: create `Mods\SpectreReplay\` in the game directory and copy in

   - `SpectreReplay.dll` (from `SpectreReplay/bin/Release/`)
   - `info.json` (from the project directory — the mod loader discovers the mod through it, and without it the DLL is simply never loaded)
   - `ffmpeg.exe` (optional but required for video rendering; it is looked up in the mod folder, then the game root, then `PATH`)

4. Start the game and enable the mod in UnityModManager, then open the settings panel with its hotkey.

If you update the DLL while the game is closed, also delete any `SpectreReplay.dll.*.cache*` files the mod loader left behind — it prefers that cache on the next start and will otherwise keep running the previous build.

## License

SpectreReplay is free software, licensed under the **GNU General Public License, version 3**. See [LICENSE](LICENSE) for the full text.

This mod is a derivative work: the replay engine and the video-render pipeline were ported from the GPL-3.0 **Spectre** project. That origin, the derived parts, and the third-party components involved (ffmpeg, x264, Harmony, UnityModManager, Newtonsoft.Json) are documented in [NOTICE](NOTICE). Attribution there is a condition of the license, not a courtesy — keep it if you redistribute.

A Dance of Fire and Ice and its assets belong to their respective rights holders. SpectreReplay is an unofficial fan-made mod and is not affiliated with or endorsed by them.