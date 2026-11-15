# OpenTone WebSocket messages

Endpoint: `ws://localhost:8080/` (text frames, one JSON object per message, max 16 KB).

Allowed origins: `https://strudel.cc`, any loopback origin, and clients that send no origin. Anything else gets 403.

## Nodes

`stomp`, `mod`, `amp`, `delay`, `reverb`. Any other node name is logged and dropped.

## Shapes

| Purpose | Message | Sent as |
|---|---|---|
| Set a number | `{"node":"amp","param":"gain","value":0.5}` | float |
| Set a bool | `{"node":"reverb","param":"bypass","value":true}` | bool |
| Swap a model | `{"node":"amp","model":"DUBS_Champ57"}` | model change |
| Set a string | `{"node":"amp","param":"cabsimType","value":"57dlx"}` | slow path (see limits) |

`model` accepts the ID with or without the `DUBS_` prefix (`Champ57` becomes `DUBS_Champ57`).

## Rules

- The JSON type of `value` picks the encoding. `1` is a number, `true` is a bool. Sending `1` to a bool parameter is ignored by the amp.
- A model message needs `node` and `model`. A parameter message needs `node`, `param`, and `value`. If `model` is present, the message is treated as a model swap and `param` and `value` are ignored.
- `volume` is clamped to 0 or below (dB).

## Queueing

- Parameter messages for the same node and parameter are coalesced. Only the latest value is sent.
- Parameter messages go out in the order each parameter was first queued.
- Model swaps run before parameter messages.
- A model swap on a node drops any queued parameter values for that node.
- One worker sends to the amp, waiting for each echo before the next message.

## Errors

Logged to stderr. The connection stays open.

| Cause | Log text |
|---|---|
| Body is not JSON | `not JSON: ...` |
| Unknown node | `unknown node: ...` |
| Missing `param` or `value` | `missing param or value` |
| `value` is not number, bool, or string | `unsupported value for node.param` |
| No echo from the amp | `no echo for node.param` |
| Amp or encoding error | `amp: ...` |

## Limits

- String parameters (`cabsimType`, `gatePreset`, `bypassType`, `type`, `mode`, and others) can't be set yet. The string value encoding hasn't been captured, so these throw and are logged.
- Model swaps reset the node to the new model's defaults.
- Everything affects the current preset only. Nothing is saved to a slot.

## Strudel helpers

```js
const ws = new WebSocket('ws://localhost:8080/');
const send   = (node, param, value) => ws.readyState === 1 && ws.send(JSON.stringify({ node, param, value }));
const swap   = (node, model)        => ws.readyState === 1 && ws.send(JSON.stringify({ node, model }));
const drive  = (node, param, pat)   => pat.onTrigger((hap) => send(node, param, hap.value));
const toggle = (node, param, pat)   => pat.onTrigger((hap) => send(node, param, hap.value > 0));
```
