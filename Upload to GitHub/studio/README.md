# AnyPS5 Studio

A native Windows app (WPF on .NET 8) around AnyPS5. It converts a game, runs it, and records each run and conversion so you can see why a game is slow, crashes or won't convert.

This folder is separate from the AnyPS5 engine and doesn't change it. The app calls the engine's `relinker.exe` and `libs` folder, and turns on the recording the engine already has.

You need your own decrypted game dump. No game files are included here, and `.pkg` packages can't be opened.

## What it does

- **Converts** a decrypted game folder or `.exfat` image, unwrapping SELF files on the way. It reads the image directly and never changes it.
- **Keeps a library** of converted games with Play and Stop. Stop closes the game properly so the engine can write its report.
- **Records every run** in the game's `diagnostics\runs` folder: the log with timestamps, frame and GPU figures, CPU, memory, disk and GPU load, the busiest threads, and the engine's own report.
- **Explains each run** in plain findings, in the app and in `diagnosis.md`: what limited the frame rate, the heaviest shaders, stutter, freezes, crashes, and missing functions or unsupported shaders.
- **Compares each run with the last one** and lists what changed.
- **Records every conversion** in `%LOCALAPPDATA%\AnyPS5 Studio\logs\conversions`, with what the failure means and what to try.
- **Engine switches**: per-game `APS5_` and `ANYPS5_` settings for experiments. Nothing else is passed to the game.
- **Test mode**: finds the game's heaviest shader and skips it while the game runs, with a button to turn it back on. This is for finding out what a shader does. It isn't a fix, and the picture may look wrong.

## Build

You need the .NET 8 SDK and an AnyPS5 build or release (`relinker.exe` plus `libs`).

```bash
dotnet publish studio/src/AnyPS5Studio.csproj -c Release -r win-x64 -o studio/dist
```

Put `AnyPS5 Studio.exe` beside `relinker.exe` and `libs`, or choose that folder when the app asks. Copy `patches` there too if you want a game's patch applied during conversion.

## Tests

The tests take the folder of a real AnyPS5 install, and a stand-in relinker compiled from `tests/FakeRelinker.cs.txt`:

```bash
dotnet run -c Release --project studio/tests/Tests.csproj -- <anyps5 folder> <stand-in relinker.exe>
```

## Patches

`patches/PPSA32236-libc-wait.toml` is an experimental patch for Tokyo Xtreme Racer. It replaces a raw `_umtx_op` system call in the game's bundled `libc.prx` (AnyPS5 issue #12123) so the game can start. It hasn't been tested beyond the title and menu screens.

## Tokyo Xtreme Racer notes

Measured on a Ryzen 7 5800XT and RTX 4070 with engine `v0.1.4-7280-g3cb1cd29ec`, on the intro and title screens only:

- The game runs at about 7.7 FPS. One compute shader takes about 82 ms of each 128 ms frame.
- Skipping that shader while the game ran gave 48 ms frames, about 20 FPS, and restoring it returned to 128 ms.
- It's a half-resolution pass of about 1281x721, which reads depth and one half-size buffer and writes two half-size buffers. What it draws isn't confirmed.
- It's about as slow with `APS5_NO_HOST_IMPORT=1` (81 ms), so imported memory isn't the cause. `APS5_OUTPUT_RESOLUTION=1920x1080` had no effect.
- The game renders internally at around 2564x1444 to 2688x1512, not 4K.
- With the shader off, frames are still 48 ms with only 22 ms of GPU work, so the rest is CPU or engine time.

The root cause isn't known yet, and none of this has been measured in a race.

## License

GPL-2.0, the same as AnyPS5. `src/SelfUnwrapper.cs` is a C# port of AnyPS5's `tools/unwrap_self.py`.
