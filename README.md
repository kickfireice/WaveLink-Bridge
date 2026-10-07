# WaveLink Bridge for Macro Deck

Drive Elgato Wave Link 3 from your Macro Deck: channel volumes, mutes, mix
masters, input gain, outputs and channel FX — with dropdowns that follow what
Wave Link actually has, and honest errors when it cannot do something.

> **Unofficial community plugin.** Not affiliated with, endorsed by, or maintained
> by Macro Deck or Elgato. Those names are used only to describe what this plugin
> integrates with.

---

## What it is

A single-half plugin: C# on .NET 10 talks to Wave Link 3 over a **localhost-only**
WebSocket (JSON-RPC 2.0, the same protocol Elgato's own Stream Deck plugin uses).
Nothing leaves your machine.

| Half | Runs as | Language | Distributed via |
|---|---|---|---|
| **Macro Deck plugin** | Macro Deck 3 integration | C# / .NET 10 | Macro Deck Store / this repo |
| **Wave Link 3** | Elgato's audio app (must be running) | — | Elgato |

There is no second half to install and no account, token or pairing step: Wave Link
accepts local clients, and the plugin finds its port on its own.

## Features

- **9 actions**: set/adjust/mute channel volume, set/toggle mix master,
  toggle channel FX, set input gain, set output (volume, mute, switch main
  output), and a status action for troubleshooting.
- **Per-action mix routing**: Overall (master), All mixes, or one mix — your
  Monitor and Stream mixes are controlled independently.
- **Live dropdowns**: channels, mixes, effects, inputs and outputs are read from
  Wave Link, and raw ids stay typeable so hand-built buttons keep working.
- **3 live `wavelink-*` variables**: connected, version, channel count.
- **Show-status diagnostics**: connection state, call counters, last error and
  recent events, in one place.
- **Honest errors**: not-connected and not-found instead of silently doing nothing.

## Setup

### Prerequisites

| Tool | Needed for | Check |
|---|---|---|
| Macro Deck 3 ≥ `3.0.0-beta.15` | running the plugin | admin UI footer |
| Elgato Wave Link 3 | the thing being controlled | running on the same PC |
| .NET 10 SDK | building from source | `dotnet --version` → `10.x` |

### Install

Install from the Macro Deck Store once published, **or** build it yourself:

```powershell
dotnet build WaveLinkBridgeUnofficial.slnx
macrodeck-plugin build --source src/WaveLinkBridgeUnofficial --output ./artifacts
```

then install the resulting `.macroDeckPlugin` (see
[Building](#building)). Add buttons in Macro Deck, e.g. *Set channel volume*
for Discord on the Stream mix, or *Mute channel* for your mic overall.

No configuration is needed: the plugin discovers Wave Link's port from its
`ws-info.json` (falling back to a local port scan) and connects on its own.

## Actions reference

| Action | Needs | Notes |
|---|---|---|
| Set channel volume | channel, mix, volume 0–100 | Overall sets the master, All mixes sets every mix |
| Adjust channel volume | channel, mix, change −100…+100 | relative nudge, clamped at 0/100 |
| Mute channel | channel, mix, toggle/mute/unmute | toggle on All mixes mutes unless everything is already muted |
| Set mix volume | mix, volume 0–100 | mix master, e.g. Stream mix |
| Mute mix | mix, toggle/mute/unmute | mix master mute |
| Toggle channel effect | channel, effect, toggle/enable/disable | effect list follows the selected channel |
| Set input gain | input, gain 0–100, mute mode | gain and mute are independent; leave either alone |
| Set output | output, volume, mute mode, set-as-main | volume/mute/main-output switch in one press |
| Show Wave Link status | nothing | reports diagnostics; always answers, even offline |

Every channel/mix/effect/input/output picker accepts a raw id typed by hand,
so buttons survive renames and stay copy-pasteable between profiles.

## Variables

| Variable | Type | Meaning |
|---|---|---|
| `{{ vars.wavelink_connected }}` | boolean | whether the plugin currently reaches Wave Link |
| `{{ vars.wavelink_version }}` | text | Wave Link version, e.g. `3.3.0.4529` |
| `{{ vars.wavelink_channel_count }}` | number | how many channels Wave Link currently reports |

The three above are read-only and polled by the host. Unknown ids read as
unavailable, never as a faked empty value.

### Slider binding (two-way volume)

Each channel appears in the variable browser twice: once overall
(`wavelink_vol_game`) and once per mix (`wavelink_vol_game_headphones`,
`wavelink_vol_game_stream`, ...). **Bind the Slider widget to the per-mix one**
(BINDING → Variable) and dragging mirrors the exact cell you see in the Wave
Link UI — slide to 5, Wave Link shows 5; slide to 100, it shows 100. The
binding writes live while you drag and follows outside changes
(Wave Link UI, other buttons) within seconds. No action needed on the slider —
use its tap events for extras (e.g. Double Tap → *Mute channel*).

The overall entry drives the channel master. Per-mix control beyond sliders
stays on the actions. Do not feed a slider's absolute value into *Adjust channel
volume* — that action takes a relative nudge; use *Set channel volume* for
absolute values.

## Privacy and data handling

- **Localhost only.** Everything is `127.0.0.1`; there is no cloud, no telemetry,
  no analytics, no update pings, and no outbound traffic of any kind.
- **No credentials.** Wave Link's local protocol needs no token; the plugin stores
  nothing (no settings file, no secrets). The only file it reads is Wave Link's
  own `ws-info.json` to learn the current port.
- Logs never contain audio content or personal data — counters, ports and channel
  names at most.

## Known limitations

- **Windows only, end to end.** The plugin ships for Windows (x64) because Wave
  Link itself is Windows-only software — there is nothing to talk to anywhere
  else.
- **Button and label visuals can lag a few seconds.** Multi-state button states
  and `wavelink-*` variable labels refresh on Macro Deck's own widget cadence
  (up to ~5 s observed on 3.0.0-beta.15), even though the bridge pushes state in
  milliseconds and direct reads are instant. Static buttons react instantly.
  This is host-side refresh behavior, not something the plugin can set —
  reported upstream.
- **First press after an install/restart can hit the reconnect window.** The send
  path waits ~8 s for Wave Link to come back and retries once; only then does it
  fail. Passive state stays instant.
- Wave Link picks a **new port every launch**; a stale port is never trusted —
  the plugin re-discovers it each time it reconnects, and pings every 15 s so
  idle connections are not closed.
- Channel bindings survive restarts: a channel that is merely gone reads as
  unavailable and resumes on its own; only an id that never named a channel is
  dropped.

## Multi-state mute buttons

*Mute channel* and *Mute mix* drive button state: set the button to multi-state
and it follows muted vs unmuted live, including changes made in
the Wave Link UI or by other buttons. Style each state yourself (label, colours,
icon). State refreshes on the host's widget cadence (a few seconds), same as variables.

## Repository layout

```
src/WaveLinkBridgeUnofficial/   the plugin (C#/.NET) - the store package
  WaveLink/                     localhost JSON-RPC client: connection, cache, diagnostics
  Actions/                      9 actions + shared dropdown/error plumbing
  Localization/Strings.resx     every user-facing string (single language so far)
  manifest.json                 identity, icon, per-platform entrypoints
  macrodeck-build.json          one publish target per declared entrypoint
tests/WaveLinkBridgeUnofficial.Tests/
                                harness tests: build, wiring, offline behaviour
```

## Building

```powershell
dotnet build WaveLinkBridgeUnofficial.slnx
dotnet test WaveLinkBridgeUnofficial.slnx
macrodeck-plugin build --source src/WaveLinkBridgeUnofficial --output ./artifacts
macrodeck-plugin validate --artifact ./artifacts/<id>-<version>-win-x64.macroDeckPlugin --level Publication
macrodeck-plugin test --project src/WaveLinkBridgeUnofficial --report markdown --output conformance.md
```

Dependencies are **NuGet packages only** (`MacroDeck.*`, pinned to the host's
`3.0.0-beta.15`). No third-party DLLs are bundled, and the Wave Link client has
zero external dependencies.

## Releasing

The Store builds from GitHub, not from uploads: push a tag matching the version,
publish a GitHub release, and `.github/workflows/release.yml` builds,
conformance-tests and uploads to the portal. The workflow pins `cli-version` to
the platform minimum (`3.0.0-beta.15`).

## Licensing

**MIT** (see `LICENSE`).

All graphics in this repository (`src/WaveLinkBridgeUnofficial/Assets/icon.png`
and `AuthorIco.png`) were created by the author with AI image generation.

## AI disclosure

This project was developed with AI assistance (code written with AI agents), and
the plugin icon is AI-generated. The plugin contains **no AI functionality at
runtime**, sends nothing to any AI service, and makes no network connections
other than loopback. Declared in the manifest and in the Creator Portal
submission, per store guidelines section 9.

## Issues and contributions

Bug reports and pull requests are welcome. When reporting a problem, please include:

- your Macro Deck version and Wave Link version,
- the **Show Wave Link status** output (it carries the build identity, counters
  and recent events, so there is no need to guess which build is installed),
- whether the button reports not-connected (Wave Link unreachable) or not-found
  (a channel/mix/effect that Wave Link does not currently have).
