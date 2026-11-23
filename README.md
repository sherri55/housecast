# AirPlay tray

A Windows tray app that sends whatever your PC is playing to AirPlay devices, one or several at once, kept in sync. Click the tray icon, tick the devices, and the sound plays there.

## How it works

```
Windows audio ──(WASAPI loopback)──> AirPlayTray.exe ──(AirPlay 2: RTSP, PTP, RTP)──> AirPlay 2 devices
```

- **AirPlayTray.exe** captures the PC's sound, converts it to 44.1 kHz 16-bit stereo, and hands it to its built-in AirPlay engine the moment Windows delivers it.
- **The engine** (`src/AirPlay.Engine`, a C# port of OwnTone's AirPlay 2 sender) finds the devices with Windows' own DNS-SD, pairs with them, and sends the audio. It is the PTP clock every device follows, which keeps them in sync, and it paces the audio by that clock, filling quiet spells with silence.
- It needs UDP ports 319 and 320 for PTP, so it can't run alongside another AirPlay sender on the same PC.

## Using it

The flyout opens with a short rise-and-fade, like Windows 11's Quick Settings. A pinned header holds the AirPlay mark, a line saying where the sound is playing ("Playing on Bedroom 1 and Bedroom 2"), and the volume for every speaker at once. Below it, the list scrolls: This PC, then **Groups** and **Speakers** sections. Colours use Windows 11's flyout tones and your Windows accent colour, and follow Windows' light or dark setting.

The list shows devices like the iPhone AirPlay picker. Each row shows an icon for the kind of device (speaker, TV, Apple TV, home theater, receiver), the name, and the model underneath. A round check on the right shows whether it's selected, and clicking anywhere on the row toggles it. A selected speaker opens into a card with a volume row (speaker icon), a delay row (stopwatch icon), round − and + buttons, and accent-filled sliders. The model comes from each device's own network announcement.

| Control | What it does |
|---|---|
| Group | Listed above the speakers. Its house icon sits in an accent circle, the name is bold, and the second line gives the speaker count and names. Ticking it plays on all its speakers and leaves your other speakers as they are; unticking stops them. It shows as ticked when all its speakers that are on the network are playing. A ticked group opens into a card: a pill per speaker (filled while it plays) and one volume slider for all its speakers. Every group row has a round pencil button that opens the editor without ticking the group, and right-clicking a group offers **Edit group** and **Delete group**. The dashed **New group** row below the list opens the same editor: a name, the speakers as rows with round checks, **Save**, **Cancel**, and a red **Delete** when editing. Deleting a group doesn't change its speakers. Groups are saved between runs. |
| Device tick box | Plays on that device. A device that has never been paired shows a PIN box instead: type the PIN shown on the device and press Pair. The app remembers what you tick. If nothing is playing, for example after the app restarts or the speakers dropped it, the app plays the remembered speakers again within 5 seconds of finding them. Untick everything and it remembers that too. |
| Volume | That device's volume, remembered between runs. |
| Delay | Shifts that device earlier or later, in 100 ms steps on the − and + buttons, within ±2 seconds, remembered between runs. A wider ±2.5 s range was tried and removed: Bedroom 2 doesn't play beyond ±2 s. |
| This PC | Untick to mute the PC's own speakers, which play ahead of the AirPlay ones. |
| Volume (header) | Scales every AirPlay device's volume at once. |

Each slider has − and + buttons (hold to repeat) and shows its value. A playing device's Delay changes as you move it.

The flyout and tray icon follow Windows' light or dark setting.

The tray icon's right-click menu has **Start with Windows** and **Quit**. Quitting stops the AirPlay devices and puts the PC's mute back the way it was.

## Installing and updating

```powershell
D:\Projects\2026\Extra\airplay-tray\install.ps1
```

This builds a release copy into `%LOCALAPPDATA%\Programs\AirPlayTray`, adds an **AirPlay Tray** Start menu shortcut, and starts it. Run it again after any code change. Tick **Start with Windows** from the installed copy, since the setting remembers the path of the copy that set it.

## Latency

The engine hands each device its audio 2 s ahead of when it plays, as OwnTone does by default, and a device's Delay can take back up to 2 s of that. With OwnTone, Bedroom 2 at Delay −2000 measured about **340 ms** behind the PC; the native engine uses the same timing but hasn't been measured yet.

For video, set the player to delay its picture by the remaining amount, e.g. VLC: *Tools → Track Synchronization → Audio track synchronization*, about −0.35 s.

## Known limits

- Sound reaches the devices about a third of a second late (see Latency). That's fine for music, but video needs the player's own delay to line up.
- It only captures the default playback device.
