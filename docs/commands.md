# Commands

Commands that talk to the amp take `-d, --device <device>`. Run `opentone list` to see the attached amps and their names: `mustang-lt25` in normal mode, `lt-dfu` in update mode. Fender devices OpenTone doesn't support yet are listed as `unsupported`, with their VID:PID.

| Command | Description |
|---|---|
| `list` | Attached Fender devices and their mode, plus the audio devices for `listen` |
| `connect` | Open a live session (see below) |
| `listen <playbackid> [captureid]` | Play the amp's USB audio through a playback device; press any key to stop. IDs come from `opentone list`. Without a capture ID, the amp given with `-d` is found by its USB VID, PID and serial (Windows/WASAPI only for now) |
| `firmware` | Show the firmware version |
| `get <type> ...` | Read something from the amp (types below) |
| `set <type> ...` | Change something on the amp (types below) |
| `call <fn> [args]` | Call a firmware function with up to 8 arguments, and print `r0`/`r1` plus any messages the amp sent meanwhile. Needs the debug hook |
| `dump <capture> <output>` | Extract the firmware from a capture of an official update. No amp needed |
| `patch <input> [output] [options]` | Inspect a firmware image or write a patched copy ([firmware](firmware.md)). No amp needed except for `--pull` |
| `flash <path>` | Flash a firmware file (update mode only). `--yes` skips the prompt |

## get and set

| Type | `get` | `set` |
|---|---|---|
| `param` | `get param <node> <param>` | `set param <node> <param> <value>` |
| `preset` | `get preset <slot>`: the preset as JSON | `set preset <slot>`: select it |
| `node` | `get node <node>`: the node as JSON | |
| `model` | | `set model <node> <model>` |
| `mem` | `get mem <addr> [len]`: hex dump. Needs the debug hook | `set mem <addr> <hex>`. Needs the debug hook |
| `context` | `get context`: the firmware objects hooks work on. Needs the debug hook | |
| `asset` | `get asset <slot> <file>`: save a data-flash asset. Needs the debug hook | `set asset <slot> <file>`: store a file as an asset and verify it. Needs the debug hook |

Nodes are `stomp`, `mod`, `amp`, `delay` and `reverb`. Presets and nodes print as plain JSON so they can be saved or piped. Addresses and numbers can be hex (`0x...`) or decimal. The debug hook is described in [hooks](hooks.md).

```
opentone list
opentone get preset 1 -d mustang-lt25 > preset1.json
opentone get param amp gain -d mustang-lt25
opentone set preset 3 -d mustang-lt25
opentone listen <playbackid> -d mustang-lt25
opentone get mem 0x08020200 16 -d mustang-lt25
```

Every command connects to the amp first (hello and sync). Connecting makes the amp redraw its screen.

## Live session

```
opentone connect -d mustang-lt25
```

This connects to the amp, starts the keepalive and a WebSocket server on port 8080, syncs with the amp, and opens the terminal UI. The amp commands (`firmware`, `get`, `set`, `call`, `listen`) can be run inside the session, and `quit` leaves it.

The messages used by the WebSocket server are documented in [WebSocket messages](websocket-messages.md).
