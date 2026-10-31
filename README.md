# Open Delulu

A reimplementation of the **Sexy Beach 3** player (Illusion, 2006) in C# + Avalonia UI.
It reads the data of a game the user has installed, **does not need the original executable**, and
**does not contain or distribute any game assets**.

## Solution layout (`OpenDelulu.slnx`, open with Visual Studio 2026)

| Project | Role | Platform |
|---|---|---|
| `OpenDelulu.Formats` | Parsers for every game format (`.pp`, `.lst`, `.xx`, `.xa`, `.sdt`, `.sav`, audio…) | net10.0, plain C# |
| `OpenDelulu.Engine` | Install detection, virtual file system, models, animation, audio, scripts, stages, game progress | net10.0, plain C# |
| `OpenDelulu` | Shared Avalonia UI, 3D viewer (OpenGL ES / Vulkan), UI translations | net10.0 |
| `OpenDelulu.Desktop` | Windows / Linux / macOS | net10.0 |
| `OpenDelulu.Android` | Android | net10.0-android |
| `OpenDelulu.iOS` | iOS (building needs a Mac; not a priority yet) | net10.0-ios |
| `OpenDelulu.Cli` | `delulu` command line tool to inspect and verify the data | net10.0 |
| `tools/OpenDelulu.ShaderCompiler` | Compiles the Vulkan shaders (GLSL → SPIR-V) | net10.0 |

Platform priority: Windows, Linux and Android first; iOS and macOS later.

## Running

```
dotnet run --project OpenDelulu.Desktop
dotnet run --project OpenDelulu.Desktop -- --game "D:\Sexy Beach 3" --play 1 1 2
dotnet run --project OpenDelulu.Desktop -- --game "D:\Sexy Beach 3" --character 3 --swimsuit 2
dotnet run --project OpenDelulu.Cli -- verify "D:\Sexy Beach 3"
```

The game folder is the one that contains `SexyBeach3` (and optionally `SexyBeach3Plus`, `SexyBeach3PlusG`).
Other desktop options: `--open <archive.pp> <file>`, `--script <name>`, `--backend auto|gl|vulkan`, `--lang <code>`,
`--mute`, `--zoom <factor>`.

**Android:** copy the `Sexy Beach 3` folder into `Android/data/com.opendelulu.app/files/` (over USB), then press *Open*.
That folder needs no storage permission.

## Languages

The interface is in English by default; pick another language in the top right corner (or run with `--lang vi`).
Translations are JSON files in `OpenDelulu/Assets/Lang/`. Contributions are welcome:
see [docs/TRANSLATING.md](docs/TRANSLATING.md).

## Graphics

The 3D viewer has two backends, chosen in the top bar ("Graphics: …") or with `--backend auto|gl|vulkan`:

| Backend | Platforms | How frames reach the window |
|---|---|---|
| **Vulkan** | Windows, Linux (Android: tried, falls back to OpenGL when the compositor rejects Vulkan images) | Own Vulkan device; frames are handed to Avalonia's compositor through `ICompositionGpuInterop` (Windows: shared D3D11 texture + keyed mutex; Linux: file descriptor + semaphore), no CPU copy |
| **OpenGL ES** | Everywhere (Windows runs it on ANGLE → Direct3D 11) | `OpenGlControlBase` |

- **Automatic**: the UI runs on ANGLE (Direct3D 11) as Avalonia does by default; the 3D view tries Vulkan first
  (shared D3D11 texture) and falls back to OpenGL ES.
- **Vulkan** ("pure Vulkan"): from the next start the whole Avalonia UI runs on Vulkan too
  (`Win32RenderingMode.Vulkan` / `X11RenderingMode.Vulkan`) and frames are shared as Vulkan images. OpenGL is not
  available in this mode.
- **OpenGL ES**: `OpenGlControlBase` only.

A backend that does not start within 3 seconds is replaced by the other one, and the viewer says why. Skinning runs
on the CPU for both. Blend modes (additive light shafts, darkening shadow decals…) follow the flags stored in the
models, as the original executable does. After editing `OpenDelulu/Rendering/Vulkan/Shaders/*.vert|frag`, run:

```
dotnet run --project tools/OpenDelulu.ShaderCompiler -- OpenDelulu/Rendering/Vulkan/Shaders
```

## Done so far

- [x] Every `.pp` archive of all three editions (52 archives, 18,412 files)
- [x] `.lst` tables decrypted (XOR key found; 278/278 files) with typed readers for the gameplay tables
- [x] `.xx` models: frames, meshes, bones, materials, embedded textures (393/393); `.tga`, `.bmp`, `.ema` images
- [x] Data browser with previews of tables, images, 3D models, sounds and scripts
- [x] Character assembly from the tables: 8 girls, 5 hairstyles, 168 swimsuits (**Characters** tab)
- [x] `.xa` animation: skeletal clips, facial morphs, blinking, eyes following the camera
- [x] Vulkan backend (Silk.NET) next to OpenGL ES, with automatic fallback
- [x] Audio: software mixer (wav/ogg, resampling, music/ambience/effects/voice buses) on waveOut (Windows),
      PulseAudio/PipeWire (Linux) and AudioTrack (Android); voices with lip sync (`.vss` + level)
- [x] `.sdt` script player (**Scripts** tab): voiced lines with lip sync, menus, Goto/SetVar/Switch; all 180 scripts run
- [x] Stages: 9 maps × 5 times of day (`ld02_01`), the girl stands at a date point, lit for the time of day
- [x] Basic game loop (**Play** tab): pick girl/stage/time → meeting → talk, dates (21 activities), swimsuit change,
      move, go to the next intimacy level, say goodbye; progress saved in `%LOCALAPPDATA%/OpenDelulu/progress.json`
- [x] Sun oil scene (first version): 4 poses, press and drag on her to rub oil in (the player's hand, touch areas and
      refusals by intimacy level, reactions, voices, oily shine on the skin)
- [x] All other formats parse: `.vss`, `.xl`, `.kys`, `.eyes`/`.neck`, `.tty`, `.ptcl`/`.glare`, `.xm`, `.sav`
- [x] UI translations (English, Vietnamese) with runtime switching

## Roadmap

1. **Graphics**: GPU skinning, Vulkan on Android, shadows and lighting closer to the original.
2. **Characters**: accessories (`lc*`), pareo, tan lines.
3. **Gameplay**: dates / oil / H scenes at the `Point_*` spots, clickable spots on the map, background music per scene,
   stage animation (waves), particles and glare, swimsuit choice in game, an in-game UI like the original.
4. **Saves**: the structure of `data/save/*.sav` is read (`delulu saves`); field meanings and writing remain.
5. **Video**: the `.mpg` files (MPEG-1) with a cross-platform decoder.

## License

Open Delulu is free software under the GNU General Public License v3.0 or later ([LICENSE.txt](LICENSE.txt)).
The license covers this project's code, documentation and translations only. No game data is included: you need your
own copy of Sexy Beach 3, whose files (models, textures, voices, scripts) remain the property of their owners.
