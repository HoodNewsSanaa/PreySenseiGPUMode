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

Developed on the Acer Predator Helios Neo 16S AI (PHN16S-71). Use at your own risk, as with PreySense itself.

## How this fork stays current

`.github/workflows/igpu-sync-build.yml` runs daily: it merges new commits from the original PreySense, builds them,
runs the test suite, and publishes a Release if everything passes. If an upstream change conflicts with iGPU Only,
nothing is published and the run fails (and opens an issue if Issues are enabled), so the last good release keeps working.
