# PreySense fork: battery and RTX power additions

This fork is [PreySense](https://github.com/hammadzaigham/PreySense) with battery-life and RTX power additions.
Everything else is the original app, merged automatically from upstream every day.

**Download:** the latest [Release](../../releases/latest) (`PreySense.exe`). It replaces the original exe; your settings carry over.

## About the removed iGPU Only mode

Earlier builds had an **iGPU Only** GPU mode that disabled the RTX in Windows. It was removed: with the RTX disabled,
NVIDIA's driver is unloaded, and on Windows only that driver can put the RTX into its fully-off state (D3cold), so a
disabled RTX sat in the lighter D3hot sleep and saved nothing over Hybrid with the RTX asleep. If an earlier build
left the RTX disabled, PreySense switches it back on at start.

Use **Hybrid** with the per-mode **Preferred GPU = Integrated graphics** (which also moves apps off the RTX
automatically) to keep the RTX asleep on battery, and **Advanced Optimus** when plugged in for games.

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

- **PCIe power saving**: Windows' PCIe Link State Power Management for that mode (Off, Moderate or Maximum
  savings). Maximum lets the SSD, Wi-Fi and other PCIe links sleep, which the processor needs before it can reach
  its deeper idle states. "Windows default" leaves Windows' own setting alone; the first time PreySense changes it,
  it saves Windows' values and writes them back when you switch to a mode set to "Windows default".

- **Max processor state (P-cores / E-cores)**: Windows' "Maximum processor state" for that mode, set separately
  for the performance and efficiency cores. 99% is the well-known way to turn Turbo Boost off; lower values cap
  further, but how a percentage maps to a clock speed is up to Windows and the CPU. For an exact clock limit, use
  Max CPU frequency instead. "Windows default" works the same way as for PCIe power saving: Windows' values are
  saved the first time and written back afterwards.

- **Preferred GPU (NVIDIA)**: NVIDIA Control Panel's global "Preferred graphics processor" for that mode
  (Integrated graphics, Auto-select or NVIDIA processor). "Integrated graphics" keeps new apps off the RTX so it can
  sleep in Optimus. Games with their own NVIDIA profile, and apps you set in Windows' graphics settings, keep their
  own choice. PreySense only talks to NVIDIA's driver when the value changes, and while iGPU Only has the RTX
  switched off it waits and sends it when the RTX comes back.

  **Automatic moving:** in every mode whose preferred GPU is Integrated graphics, PreySense watches the RTX the
  same way the RTX guard does (without stopping any services) and sets apps it finds on the RTX to "Power saving"
  in Windows' graphics settings, so they use Intel from their next start. It does not restart the RTX by itself:
  on this laptop a restart of the RTX can break the screen's brightness control until Windows restarts. Use
  Settings → Move apps off the RTX when you want them moved right away.

- **PhysX processor**: NVIDIA's PhysX processor for that mode: "CPU", or "NVIDIA default" to leave NVIDIA's own
  setting (normally Auto-select). It's a registry setting that PhysX games read when they start, so changing it
  never wakes the RTX. NVIDIA's own values are saved the first time and written back afterwards.

- **Energy preference (EPP)**: how eagerly the CPU raises its clocks, from 0% (max performance) to 100% (max
  efficiency), for both core types. Higher values keep everyday bursts at low clocks. 70–85% suits battery.

- **Core scheduling**: "Prefer E-cores" sends work to the efficient cores first; "E-cores only" keeps threads on
  them. Both are Windows' own scheduling policies.

- **Keyboard lighting**: "Off" turns the keyboard and lid lighting off while the mode is active and brings your
  lighting back when you switch to a mode set to "Your lighting". Your saved lighting is never changed. The Fn
  brightness keys may still light the keyboard briefly in an "Off" mode.

- **RTX guard**: keeps things from waking the RTX in that mode (Optimus). It stops NVIDIA's polling services
  (NVIDIA app container and FrameView; the NVIDIA app won't open
  meanwhile) and starts them again when you switch to a mode without the guard or quit PreySense. Every 2 seconds
  it checks Windows' cached RTX state; when the RTX is awake it reads Windows' GPU counters to see which apps are
  on it and sets each to "Power saving" in Windows' graphics settings, so it won't use the RTX again after it
  restarts. Apps that already have a graphics choice of yours, Windows' own apps and NVIDIA's are never changed.
  Settings → "Apps moved by RTX guard" lists them and gives them their old setting back.

All of these save and apply as soon as you pick a value.

**Battery widget** (Settings, temporary): a small always-on-top readout of battery drain (now and 30-second
average), battery percent, estimated time left, CPU package power and whether the RTX is awake. It reads every
2 seconds while shown and never wakes the RTX. Drag to move; double-click or right-click to close.

**RTX still awake** (automatic): about 25 seconds after switching to Eco, or to any mode with the RTX guard on,
PreySense checks whether the RTX is still awake with no app on it. That usually means Windows' desktop
compositor (dwm) kept memory on it after the screen was on the RTX. PreySense then asks whether to restart the
RTX, which releases it; the screen stays on Intel.

**Move apps off the RTX** (Settings): lists the apps using the RTX and, after you confirm, restarts it so they
move to the integrated GPU (like Legion Toolkit's "deactivate GPU"). Most apps carry on; games and some apps may
close. Set the mode's preferred GPU to Integrated graphics first, or apps may pick the RTX again. It isn't offered
while the RTX drives a screen.

**Predator key**: also minimizes PreySense when a settings page or dialog is open (it hides it otherwise).

**Power Diagnostics** (Settings → Power Diagnostics): battery drain, CPU package power, the rest of the system
(battery minus CPU), the RTX's power state, and how much of each second the CPU package spends in C2/C3/C6/C8/C10.
It reads once a second only while the window is open. The C-state counters are read only on Intel CPUs known to
have them (12th gen through Core Ultra 200).

**Updates** (Settings → Updates → Check now, and automatically once at startup): checks this fork's releases.
When a newer build is out it offers to install it: PreySense downloads `PreySense.exe` from the release, installs it
only if its SHA-256 matches the checksum GitHub lists for that file, keeps the current version next to it as
`PreySense.previous.exe` (rename it back to roll back), and restarts. Copies not built by the release workflow
don't update themselves.

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
