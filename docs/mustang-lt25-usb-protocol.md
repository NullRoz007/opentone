# Mustang LT25: USB protocol

Source: two USBPcap captures (normal-mode startup, 11.1 s; firmware update, 245 s) and the OpenTone implementation. Only behaviour seen in those captures is listed. Items marked (inferred) are guesses from the wire, not confirmed.

## Devices

| Mode | VID:PID | Product string | Interfaces |
|---|---|---|---|
| Normal | `1ED8:0037` | Mustang LT 25 (manufacturer string FMIC) | HID (control protocol), audio |
| Update | `1ED8:0033` | Mustang LT Updater | HID only |

Both modes: USB 2.00, bcdDevice `0x0200`, one configuration. The HID interface has two interrupt endpoints, 64 bytes, bInterval 1 ms.

| Endpoint | Direction | Use |
|---|---|---|
| `0x01` | OUT (host to amp) | Requests, firmware frames |
| `0x81` | IN (amp to host) | Responses, firmware acks |
| `0x82` | IN, isochronous, 196 B | Audio (normal mode only; 2 ch, 16-bit, 48 kHz) |

The amp re-enumerates at a new device address when it changes mode. Identify it by VID/PID, not by address.

### HID report descriptor (update mode)

32 bytes: vendor usage page `0xFF00`, one application collection, one 64-byte input report and one 64-byte output report, **no report ID**. The normal-mode descriptor has the same length (not captured, so not confirmed identical).

## Report framing (normal mode)

Every report is 64 bytes.

```
Host to amp (EP 0x01):  [type][len][payload ... len bytes][padding]
Amp to host (EP 0x81):  [0x00][type][len][payload ... len bytes][padding]
```

| type | Meaning | Seen on |
|---|---|---|
| `0x33` | First frame of a multi-frame message | IN only |
| `0x34` | Continuation frame | IN only |
| `0x35` | Last frame of a multi-frame message, or a complete single-frame message | IN and OUT |

- `len` is the number of valid payload bytes in the frame. Max 61 on IN. `0x33` and `0x34` frames always carry 61.
- A message is the payloads of `0x33`, `0x34` (n times), `0x35`, joined in order.
- No CRC, frame counter or total-length field at this layer.
- Padding is not zeroed (stale buffer data was seen in both directions). Always use `len`.
- All 72 host messages in the capture are single `0x35` frames (7 or 9 payload bytes). Whether long writes use `0x33`/`0x34` is unknown.
- IN frames have an extra leading `0x00` that OUT frames do not. Not a report ID: the descriptor has none (inferred: added by the host stack or firmware).

## Messages (normal mode)

Payload is protobuf wire format, no schema available, so field numbers are read from the wire.

| Field | Meaning |
|---|---|
| 1 (varint) | `0` on every request, `2` on every response |
| one command field | The field number identifies the command; its value is a sub-message |

Tag bytes: `(field << 3) | 2`, varint encoded. Field 102 is `b2 06`, 105 is `ca 06`, 113 is `8a 07`.

| Request | Response | Meaning | Confidence |
|---|---|---|---|
| `113 {1:0, 2:0}` | `113 {1:0, 2:0}` | Session start. Repeated at the end as `{1:1, 2:0}` | Meaning unknown |
| `102 {1:1}` | `103 {1: string}` | Firmware version. Response was `2.1.4` | High |
| `105 {1:N}` (N = 1 to 60) | `31 {1: JSON, 2: N}` | Read preset slot N. Field 2 echoes N | High |
| `104 {1:1}` | `32 {1: JSON, 2:1, 3:0}` | Read the current preset. Returned slot 1's preset. Fields 2 and 3 unexplained | Medium |
| `62 {1:1}` | `63 {1:0}` | Status query | Unknown |
| `106 {1:1}` | `107 {1: fixed32 0}` | Status query. Could be a float | Unknown |
| `108 {1:1}` | `109 {1: bytes 0c 02}` | Status query | Unknown |
| `202 {1:1}` | `203 {1:1}` | Status query | Unknown |
| `201 {1:1}` | none seen | Sent once per second after startup (inferred: keepalive) | Medium |

Example frames:

```
OUT  35 09 | 08 00 8a 07 04 08 00 10 00            113 {1:0, 2:0}
IN   00 35 09 | 08 02 8a 07 04 08 00 10 00         113 {1:0, 2:0}
OUT  35 07 | 08 00 b2 06 02 08 01                    102 {1:1}
OUT  35 07 | 08 00 ca 06 02 08 05                    105 {1:5}  (preset slot 5)
```

Preset responses (field 31) are 32 to 42 frames. Length check on slot 1: field 31 is 2,137 bytes = 3 (header `0a d4 10`) + 2,132 (JSON) + 2 (`10 01`).

## Startup sequence

| t (s) | Event |
|---|---|
| 0.00 | Enumeration, `SET_CONFIGURATION 1` |
| 4.69 | `113` handshake |
| 4.70 | `102`, firmware version |
| 4.70 to 7.00 | `105` for slots 1 to 60, one every ~40 ms |
| 7.04 | `104`, current preset |
| 7.08 to 7.10 | `62`, `106`, `108`, `202` |
| 7.10 | `113 {1:1, 2:0}` |
| 8.10 onward | `201`, once per second, no response |

Request to response latency: 3.0 to 5.5 ms (median 4.0 ms). The amp sends one 64-byte frame per millisecond (the endpoint interval), which is why a 2.1 KB preset takes ~35 ms.

## Preset JSON

Field 1 of the `31` and `32` responses is UTF-8 JSON, 1.9 to 2.5 KB each. Node models and parameters are in the node models document.

| Key | Value |
|---|---|
| `nodeType`, `nodeId` | `preset`, `preset` |
| `version` | `1.1` |
| `numInputs`, `numOutputs` | 2, 2 |
| `info.displayName` | 16 characters, space padded |
| `info.preset_id` | UUID |
| `info.author`, `info.source_id` | strings (empty in all 60) |
| `info.timestamp`, `info.created_at` | integers |
| `info.product_id` | `mustang-lt` |
| `info.is_factory_default` | bool (true in all 60) |
| `info.bpm` | integer |
| `audioGraph.nodes` | 5 nodes: stomp, mod, amp, delay, reverb |
| `audioGraph.connections` | 12 entries, stereo series chain |

Slots seen: 1 to 30 named factory presets, 31 to 59 named `EMPTY` (the smallest, ~1.9 KB), 60 `METAL LEAD` (differs from the same-named preset in slot 13).

## Update mode

The amp must be in update mode (`1ED8:0033`). Same 64-byte reports, but a different, simpler protocol: one OUT report, then wait for one IN ack, then repeat.

```
Host to amp (OUT):  [cmd][0x03][seq][len][data ... len bytes][zero padding]
Amp to host (IN):   [0x00][0x00][cmd][zeros]
```

| cmd | Meaning | len | Notes |
|---|---|---|---|
| `0x02` | Begin | 0 | No parameters: no size, address or checksum is sent |
| `0x03` | Data | 32 | `seq` is an 8-bit counter, starts at 0 for the first data frame, wraps after 255 |
| `0x04` | End | 0 | |

- The ack echoes `cmd` in byte 2. Bytes 0 and 1 were `00 00` in every ack, so no error case was seen.
- Byte 1 is `0x03` on every host frame. Meaning unknown (not a report ID).
- Nothing in a frame gives an address or offset. The image is streamed in order.
- Strict lockstep: no frame was sent before the previous ack arrived (59,258 pairs, no exceptions).

### Timing

| Step | Time |
|---|---|
| Begin sent | 9.58 s |
| Begin ack | 22.69 s (13.1 s later; probably a flash erase, inferred) |
| 59,256 data frames | 22.69 to 200.46 s, ~3.0 ms per frame |
| Data ack latency | 2.1 to 5.0 ms (median 3.0 ms) |
| End ack | 200.48 s (17 ms after End) |

Use a long timeout (30 s used by OpenTone) for the Begin ack and about 1 s for the rest. The capture does not show the amp rebooting into normal mode, so the post-update enumeration is not documented.

### Frame counts

| Item | Count |
|---|---|
| Host reports | 59,258 (1 Begin, 59,256 Data, 1 End) |
| Amp acks | 59,258 |
| Payload per data frame | 32 bytes |
| Image size | 59,256 x 32 = 1,896,192 bytes |

## Firmware image

The image is the data payloads joined in order. SHA-256 of the captured image: `0f48ee0286c619aec2772949f1adeb6fdb0a206925a4a902d950d656a8c036ac`.

| Property | Value |
|---|---|
| Size | 1,896,192 bytes (multiple of 32) |
| Encryption | None seen: starts with an ARM Cortex-M vector table (initial SP `0x24080000`), readable C++ strings throughout |
| Trailer | Last 4 bytes: CRC-32, little-endian (`81 bd 48 4b` = `0x4B48BD81`) |
| Version string | `2.1.4` at offset `0x1875B8`, ASCII, once in the image |
| Next string | `mustang-lt-25` at `0x1875C0` |

### Trailer checksum

STM32 hardware CRC unit settings: polynomial `0x04C11DB7`, initial value `0xFFFFFFFF`, input fed as 32-bit little-endian words, no bit reflection, no final XOR. Computed over every byte except the last 4.

| Implementation | How |
|---|---|
| Direct | XOR each little-endian word into the register, then shift left 32 times, applying the polynomial when the top bit is set |
| Byte-wise CRC-32/MPEG-2 library | Reverse each 4-byte group first, then hash. Plain MPEG-2 over the raw bytes gives `7285A201` (wrong) |
| Standard zlib CRC-32 | Does not match (`53AAE5EB`) |

The reflect options (`ReverseDataBytes`, `ReverseResultBeforeFinalXor`) do not substitute for the word reversal: all four combinations on raw bytes fail.

### Patching

| Edit | Rule |
|---|---|
| Version string | 8-byte slot (5 characters plus 3 NULs). Up to 7 characters with a NUL terminator. Longer needs relocating the string and fixing every reference to it, which needs the load address (unconfirmed) |
| Any in-place edit | Recompute the trailer CRC afterwards. The old trailer is stale, so validation fails until it is rewritten |
| Embedded JSON text | Edits must keep the same byte length (no relocation) |

The only integrity check verified is the trailer CRC. Whether the bootloader or the application checks anything else (a signature, a second checksum) is not known.

## Reading the captures

USBPcap (pcapng link type 249). Each record is a pseudo-header, then the USB payload. All fields little-endian.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 2 | headerLen | 27, or 28 for control transfers |
| 2 | 8 | irpId | Pairs a submit with its completion |
| 10 | 4 | status | |
| 14 | 2 | function | |
| 16 | 1 | info | Bit 0: 0 = request, 1 = completion |
| 17 | 2 | bus | |
| 19 | 2 | device | |
| 21 | 1 | endpoint | Bit 7 = direction (1 = IN) |
| 22 | 1 | transfer | 0 isochronous, 1 interrupt, 2 control, 3 bulk |
| 23 | 4 | dataLength | Requested length, not the payload size |
| 27 | 1 | stage | Control transfers only |

- Each interrupt transfer is logged twice. For OUT only the submit has data, for IN only the completion does. Filter on a non-empty payload to count each report once.
- Find the amp from the 18-byte device descriptor read (starts `12 01`; VID at bytes 8 to 9, PID at 10 to 11), then filter by bus and device address. Addresses change between captures (10, 8 and 11 were seen).
- Expected counts for the amp: startup capture 2,218 reports (72 OUT, 2,146 IN); update capture 118,516 (59,258 each way).

## Notes

- Normal mode and update mode are different protocols. Normal mode is protobuf over `0x33`/`0x34`/`0x35` frames; update mode is a fixed 4-byte header with no protobuf.
- IN frames carry an extra leading `0x00` in normal mode and in update mode, but the OUT frames do not.
- Requests have no sequence numbers. Normal mode is request, then response, then the next request. Do not pipeline.
- The JSON text is not stable: compact in some presets and spaced in others, with the key order varying. Parse it, do not match on text.
- In `connections`, `input` is the source node and `output` is the destination.
- Not captured: any host write that changes state in normal mode (knob changes, preset saves), unsolicited messages from the amp, update error codes, and the post-update reboot.
- Load address and MCU of the image are unconfirmed. The checksum matches the STM32 hardware algorithm, which suggests an STM32-class part (inferred).
