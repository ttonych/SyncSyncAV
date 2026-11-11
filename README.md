# SyncSyncAV

[Русский](README.ru.md)

SyncSyncAV helps you align audio or subtitles from two editions of the same film. Open the editions side by side, find the same frame in each, and read the offset in frames and milliseconds. Copy that value into the tool you use to adjust the track.

## First run

On Windows x64 with .NET Framework 4.8, download **SyncSyncAV_0.1.0.zip**, extract the entire archive and run `SyncSyncAV.exe`. Keep the `Engine` folder next to the executable. On first launch the interface uses Russian on Russian-language Windows and English elsewhere. You can change it under **Settings → Language**, even with both videos open. [Download the app](https://github.com/ttonych/SyncSyncAV/releases/download/v0.1.0/SyncSyncAV_0.1.0.zip) · [Download the v0.1.0 sources](https://github.com/ttonych/SyncSyncAV/releases/download/v0.1.0/SyncSyncAV_0.1.0_source.zip)

## Measure an offset

1. Drop the edition you want to sync **to** onto **A**. Drop the edition that supplies the track onto **B**.
2. Find the first frame after the same cut in each pane. Click **Set sync point** (`K`). Step through a few frames with `←` and `→` to check that both editions move together and still match.
3. In **A = B** mode, the displayed offset is frame A minus frame B. If the cut is at frame 100 in A and frame 148 in B at 24 fps, the offset is **−48 frames, or −2000 ms**. Move the track from B two seconds earlier to align it with the picture in A.
4. Click the copy button beside the millisecond value. Click the formula to switch the sign and keep the selected frames. **→ 0** resets the offset. If one edition includes an extra scene, save another sync point at a cut after that scene.

## Open a Blu-ray

| What you have | What to do in A or B |
| --- | --- |
| A Blu-ray ISO image (UDF) | Drop the `.iso` file onto the pane and choose a playlist. You can open the image directly without mounting it. |
| A disc or an extracted copy | Drop the disc root, the folder containing `BDMV`, or `BDMV` itself. The `BDMV` folder needs an `index.bdmv` file and a `PLAYLIST` directory. |
| A specific playlist | Open its `.mpls` file directly. |

With **Open video…** or **Replace…**, select the disc folder or `BDMV` in Explorer and click **Open**. If you have already browsed into that folder, click **Choose playlist...**. The pane header shows the source name and the selected playlist. **Change playlist…** reopens the list with the current playlist selected.

When you drag two sources together, the headers show which one will open in each pane. **Remove** closes the selected preview and keeps the original file on disk.

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| `←` / `→` | Step backward / forward in both linked panes |
| `A` / `S`, `D` / `F` | Step backward / forward in A only or B only |
| Hold `Shift` or `Ctrl+Shift` while stepping | Use the selected large step or twice that step |
| `G`, `H`, `O` | Enter a frame for A, a frame for B, or an offset |
| `K`, `L` | Save a sync point or toggle the link |
| `Ctrl+Z`, `Ctrl+Y` | Undo or redo |

## In the source tree

The shell lives in `source/` and the bridge to VirtualDub2 in `engine/bridge/`. The source archive includes the modified VirtualDub2 host and input plugin. The project's own license is in `LICENSE.txt`. SDK, font and third-party license files are kept with their respective components. [Build and update the Engine](engine/README.md) · [Changelog](docs/en/CHANGELOG.md) · [Roadmap](ROADMAP.md)

[Guidelines for working on the project](AGENTS.md)

SyncSyncAV was developed with the help of Astra and Opus 5.5.
