# PreySense + iGPU Only

This fork is [PreySense](https://github.com/hammadzaigham/PreySense) with one addition: an **iGPU Only** GPU mode.
Everything else is the original app, merged automatically from upstream every day.

**Download:** the latest [Release](../../releases/latest) (`PreySense.exe`). It replaces the original exe; your settings carry over.

## What iGPU Only does

PreySense's other GPU modes decide which GPU drives the screen, but apps can still choose to render on the NVIDIA GPU
(browsers, launchers, overlays, games), which keeps it awake, warm and drawing power.

**iGPU Only** puts the screen on the integrated GPU and then **switches the NVIDIA GPU off in Windows** (the same as
disabling it in Device Manager). No app can use it, so it stays fully powered down.

- **Selecting any other mode** (Hybrid, Auto, Advanced Optimus) switches the NVIDIA GPU back on first, then applies that mode.
  Picking Advanced Optimus is how you move the screen and games to the RTX.
- **On battery** iGPU Only is applied automatically, and your previous mode returns when you plug in.
  Turn this off with `"IgpuOnlyOnBattery": false` under `Session` in `preysense.json`.

## Safety rules

- It never switches the NVIDIA GPU off while a game or other real GPU work is running on it. It waits, names the app,
  and switches off once it's closed. Browsers, Steam, Discord and similar apps don't hold it up: they recover on the integrated GPU.
- It never switches it off while it drives a screen: BIOS dGPU Only, or an external monitor on an NVIDIA-wired port.
- It switches the NVIDIA GPU back on before a dGPU Only (BIOS) change, when you Quit PreySense, and when Windows signs out
  or shuts down, so Windows never starts with it disabled.

- While the RTX is asleep (D3), the iGPU Only checks read its state from Windows and don't call NVIDIA's driver,
  so checking never wakes it.

## Other additions in this fork

**Mode Extras** (in each mode's tuning window):

- **PROCHOT for this mode**: a PROCHOT offset used only in that mode (for example a low throttle point in Eco).
  "Use global setting" keeps the normal behaviour. When a mode has its own offset, modes without one go back to the
  factory offset (or the global one, if that is switched on), so it doesn't stay in place after you leave the mode.
- **Max CPU frequency** (experimental): writes Windows' maximum processor frequency for every core type when the
  mode is selected, and puts it back if Windows rewrites it (it watches the power plan for changes; nothing polls).
  Intel's own power software can still lower clocks further. PreySense only removes a cap it set itself.

- **Max RTX clock**: caps the RTX's core clock in that mode (the same as `nvidia-smi -lgc`), which also keeps it off
  the high-voltage end of its curve, so it runs cooler and quieter. It's only sent while the RTX is awake; while it
  sleeps, PreySense waits for it to wake instead of waking it. It's sent again when iGPU Only switches the RTX back on.

All three save and apply as soon as you pick a value.

**Power Diagnostics** (Settings → Power Diagnostics): battery drain, CPU package power, the rest of the system
(battery minus CPU), the RTX's power state, and how much of each second the CPU package spends in C2/C3/C6/C8/C10.
It reads once a second only while the window is open. The C-state counters are read only on Intel CPUs known to
have them (12th gen through Core Ultra 200).

**Sleep in Background** (Settings, on by default): while PreySense's window is hidden or minimized, it runs in
Windows' efficiency mode, stops sensor polling (also when minimized), releases NVIDIA's monitoring library and checks
the iGPU Only state every 2 minutes instead of every 20 seconds. Everything resumes when the window is shown.

The battery profile (a separate power mode on battery and on AC) and the automatic 60 Hz on battery already come
from PreySense: see Settings.

Developed on the Acer Predator Helios Neo 16S AI (PHN16S-71). Use at your own risk, as with PreySense itself.

## How this fork stays current

`.github/workflows/igpu-sync-build.yml` runs daily: it merges new commits from the original PreySense, builds them,
runs the test suite, and publishes a Release if everything passes. If an upstream change conflicts with iGPU Only,
nothing is published and the run fails (and opens an issue if Issues are enabled), so the last good release keeps working.
