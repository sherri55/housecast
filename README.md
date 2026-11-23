# AirPlay tray

A Windows tray app that sends whatever your PC is playing to AirPlay devices, one or several at once, kept in sync. Click the tray icon, tick the devices, and the sound plays there.

## How it works

```
Windows audio ──(WASAPI loopback)──> AirPlayTray.exe ──(wsl.exe stdin)──> /srv/music/pc-audio
                                           │                                       │
                                           └──(HTTP 127.0.0.1:3689)──> OwnTone ────┘──> AirPlay 2 devices
```

- **AirPlayTray.exe** captures the PC's sound, converts it to 44.1 kHz 16-bit stereo, and writes it into OwnTone's pipe through a `wsl.exe` process the moment Windows delivers it. That process also keeps WSL awake. While Windows is silent the app sends silence, so OwnTone gets a steady stream. On start it removes any pipe writer left by an earlier copy, and every 5 seconds it restarts OwnTone's playback of the pipe if it has stopped.
- **OwnTone** runs in the `airplay` WSL system. It finds the devices, pairs with them, and keeps them in sync.
- WSL uses mirrored networking (`%USERPROFILE%\.wslconfig`), so OwnTone is on the home network and its API is on localhost.

## Using it

The flyout opens with a short rise-and-fade, like Windows 11's Quick Settings. A pinned header holds the AirPlay mark, a line saying where the sound is playing ("Playing on Bedroom 1 and Bedroom 2"), and the volume for every speaker at once. Below it, the list scrolls: This PC, then **Groups** and **Speakers** sections. Colours use Windows 11's flyout tones and your Windows accent colour, and follow Windows' light or dark setting.

The list shows devices like the iPhone AirPlay picker. Each row shows an icon for the kind of device (speaker, TV, Apple TV, home theater, receiver), the name, and the model underneath. A round check on the right shows whether it's selected, and clicking anywhere on the row toggles it. A selected speaker opens into a card with a volume row (speaker icon), a delay row (stopwatch icon), round − and + buttons, and accent-filled sliders. The model comes from each device's own network announcement, read with `avahi-browse` inside WSL.

| Control | What it does |
|---|---|
| Group | Listed above the speakers. Its house icon sits in an accent circle, the name is bold, and the second line gives the speaker count and names. Ticking it plays on all its speakers and leaves your other speakers as they are; unticking stops them. It shows as ticked when all its speakers that are on the network are playing. A ticked group opens into a card: a pill per speaker (filled while it plays) and one volume slider for all its speakers. Every group row has a round pencil button that opens the editor without ticking the group, and right-clicking a group offers **Edit group** and **Delete group**. The dashed **New group** row below the list opens the same editor: a name, the speakers as rows with round checks, **Save**, **Cancel**, and a red **Delete** when editing. Deleting a group doesn't change its speakers. Groups are saved between runs. |
| Device tick box | Plays on that device. A device that has never been paired shows a PIN box instead: type the PIN shown on the device and press Pair. The app remembers what you tick. If OwnTone ever ends up with nothing selected, for example after the app or OwnTone restarts, the app reselects the remembered speakers within 5 seconds. Untick everything and it remembers that too. |
| Volume | That device's volume. |
| Delay | Shifts that device earlier or later, in 100 ms steps on the − and + buttons, within ±2 seconds, OwnTone's limit. A wider ±2.5 s range was tried and removed: Bedroom 2 doesn't play beyond ±2 s. |
| This PC | Untick to mute the PC's own speakers, which play ahead of the AirPlay ones. |
| Volume (header) | Volume of every AirPlay device at once. |

Each slider has − and + buttons (hold to repeat) and shows its value. Changing a playing device's Delay briefly reconnects it, because OwnTone only reads the delay when a device connects.

The flyout and tray icon follow Windows' light or dark setting.

The tray icon's right-click menu has **Start with Windows** and **Quit**. Quitting stops the AirPlay devices and puts the PC's mute back the way it was.

## Installing and updating

```powershell
D:\Projects\2026\Extra\airplay-tray\install.ps1
```

This builds a release copy into `%LOCALAPPDATA%\Programs\AirPlayTray`, adds an **AirPlay Tray** Start menu shortcut, and starts it. Run it again after any code change. Tick **Start with Windows** from the installed copy, since the setting remembers the path of the copy that set it.

## Latency

Measured with the microphone, Bedroom 2 at Delay −2000 plays about **340 ms** after the PC.

- **The Delay floor:** OwnTone sends audio 2.25 s ahead (`start_buffer_ms`, default 2250), and a speaker's Delay can take back up to 2 s of that. At a `start_buffer_ms` of 1000 the Bedroom speakers went silent, so keep the default.
- **Backlog:** `engine/owntone-small-input-buffer.patch` caps OwnTone's input at 0.3 s (upstream holds 2.2 s). Otherwise a pause, such as the app restarting, leaves seconds of lasting delay.
- **The app:** it sends audio the moment Windows delivers it, reading the loopback every ~10 ms. Its own buffer holds at most 0.5 s, and only if the pipe stalls.

For video, set the player to delay its picture by the remaining amount, e.g. VLC: *Tools → Track Synchronization → Audio track synchronization*, about −0.35 s.

## Setting up the engine on a new PC

1. Turn on mirrored networking in `%USERPROFILE%\.wslconfig`:
   ```ini
   [wsl2]
   networkingMode=mirrored
   ```
2. Create the WSL system and build OwnTone:
   ```powershell
   wsl --shutdown
   wsl --install Debian --name airplay --no-launch
   wsl -d airplay -u root -- bash /mnt/d/Projects/2026/Extra/airplay-tray/engine/setup-engine.sh
   ```
3. Install the app with `install.ps1` (see Installing and updating).

`engine/owntone-samsung-pin-pairing.patch` fixes PIN pairing with Samsung TVs. OwnTone sent the PIN on a new connection, and Samsung rejects a PIN that doesn't arrive on the connection that asked for it. `engine/owntone-small-input-buffer.patch` is explained under Latency.
## Known limits

- Sound reaches the devices about a third of a second late (see Latency). That's fine for music, but video needs the player's own delay to line up.
- It only captures the default playback device.
