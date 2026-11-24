# Vocal Tuner

A fast, native Windows app for singers: see your pitch live, record yourself over a karaoke or backing track, and export a clean, high-quality audio file.

<p align="center">
  <img src="docs/piano.png" width="420" alt="Piano page: finding a starting note, the tuner shows C4 −2 cents">
  <img src="docs/tuner.png" width="420" alt="Tuner page with the Record button">
</p>

## Features

- **Live tuner**: note name, cents and Hz, plus a scrolling pitch graph with colour-coded note bars (green ≤ 5¢, amber ≤ 15¢, red beyond). Starts listening as soon as the app opens.
- **Record over a backing track**
  - *Audio file*: an MP3/WAV/M4A/FLAC plays while you sing. The export mixes in the **original file**, so the music stays perfectly clean.
  - *System audio*: records whatever plays on your output (YouTube, Spotify, a karaoke video…) together with your mic.
  - *None*: voice only.
- **Latency calibration**: measures your mic/headphone round-trip once, then lines your voice up with the music automatically. Fine-tune it by ear in the review window.
- **Review window** after each take: waveform, pitch graph, playback, voice and backing levels, and sync nudge.
- **Export**: WAV 24-bit, WAV 32-bit float or MP3 320 kbps, with optional normalisation to −1 dB and an optional voice-only file.
- **Piano** to find your starting note, playable with the mouse or the computer keyboard.
- **Record/stop shortcut**: one key (F9 by default, configurable) that can work even while another app is in front.
- **Sound output picker**: follow the Windows default or pin a specific device.
- Dark mode by default, with a light theme too.

<p align="center">
  <img src="docs/review.png" width="420" alt="Review window for the last take">
  <img src="docs/settings.png" width="330" alt="Settings: devices, backing track, shortcut, pitch detection">
</p>

## Download

Get the latest **`VocalTuner-win-x64.zip`** from the [Releases page](../../releases/latest), unzip it anywhere and run `VocalTuner.exe`. Nothing to install: .NET is included.

- Windows 10 or 11, 64-bit.
- The app isn't code-signed, so Windows SmartScreen may say *"Windows protected your PC"*. Click **More info → Run anyway**.
- Settings are stored in `%LOCALAPPDATA%\VocalTuner\`.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone <this repository's URL>
cd vocal-tuner
dotnet run                                          # debug run
dotnet publish -c Release -o dist\VocalTuner        # self-contained build in dist\VocalTuner
```

## How it works

| Part | File | Notes |
|---|---|---|
| UI | `MainWindow`, `SettingsWindow`, `ReviewWindow`, `Controls.cs`, `Styles.xaml`, `Theme.cs` | WPF with custom-drawn controls (pitch roll, tuner bar, faders, keyboard). No WebView, so it opens in well under a second. |
| Live pitch | `LiveInput.cs`, `PitchAnalyzer.cs` | WASAPI capture → downmix to mono, decimate to ≤ 48 kHz → McLeod Pitch Method (NSDF) with SIMD dot products, about 60 analyses per second. |
| Recording and export | `AudioEngine.cs` | Mic, backing playback and loopback run on separate WASAPI streams. Sync is found by cross-correlation against the system-audio copy or from the calibration, and the export is rendered from the original backing file. MP3 export uses Windows Media Foundation. |
| Piano | `PianoSynth.cs` | Wavetable additive synth with a voice limit and a soft limiter, cheap even on 384 kHz devices. |
| Shortcut | `Shortcut.cs` | In-app key handling, plus a `RegisterHotKey` global hotkey. |

### Diagnostics

The exe has a few headless test modes that write a report into the given folder:

```text
VocalTuner.exe --selftest <dir>                   pitch accuracy, recording in all modes, preview, export
VocalTuner.exe --pianotest <dir>                  offline piano stress render: CPU time, clipping, clicks
VocalTuner.exe --pianolive <dir> [output name]    plays the piano on a real output and checks it via loopback
VocalTuner.exe --outtest <dir> [output name]      sine through different WASAPI setups
VocalTuner.exe --mictest <file>                   microphone mute/volume and capture levels
```

## Credits

- Audio I/O: [NAudio](https://github.com/naudio/NAudio) (MIT).
- Built with AI pair-programming assistance (Claude by Anthropic).

## License

[MIT](LICENSE)

---

### ภาษาไทย

**Vocal Tuner** แอปสำหรับคนร้องเพลงบน Windows ดูได้ว่าร้องตรงคีย์ไหมแบบเรียลไทม์ อัดเสียงร้องพร้อมเพลงคาราโอเกะ (จากไฟล์เพลงหรือเสียงในเครื่อง) แล้ว export เป็นไฟล์ WAV/MP3 คุณภาพสูง มีเปียโนไว้หาโน้ตเริ่มต้น และตั้งปุ่มลัดอัด/หยุดได้

ดาวน์โหลดไฟล์ `VocalTuner-win-x64.zip` ได้จากหน้า [Releases](../../releases/latest) แตกไฟล์แล้วเปิด `VocalTuner.exe` ได้เลย ไม่ต้องติดตั้งอะไรเพิ่ม ถ้า Windows ขึ้นหน้า "Windows protected your PC" ให้กด More info แล้วกด Run anyway
