# Mustang LT25: node models and parameters

Source: 60 presets read from the amp. Only models and values seen in those presets are listed.

## Chain

`stomp -> mod -> amp -> delay -> reverb` (identical in all 60 presets). Model IDs are prefixed `DUBS_`.

## Models by slot

| Slot | Models |
|---|---|
| stomp | SimpleCompressor, Overdrive, MythicDrive, Greenbox, BigFuzz, VariFuzz, Sustain, ChromeGate, Octobot, MustangFiveBandEq1, Passthru |
| mod | ChorusTriangle, Vibratone, SineTremolo, Phaser, TriangleFlanger, StepFilter, EcFilter, Passthru |
| amp | Twin65, Twin57, Deluxe65, Deluxe57, Champ57, Princeton65, Silvertone, Bassman59, Excelsior, Ac30Tb, SuperSonic, Plexi87, Jcm800, Rect2, MetalRect2, DR103, MetalEvh3, Or120, LinearGain |
| delay | TapeDelayLite, MonoDelay, Passthru |
| reverb | Spring65, SmallRoomReverb, LargePlate, LargeHallReverb, ArenaReverb, Passthru |

## Amps

All amps: `volume`, `gatePreset`, `gateDetectorPosition`, `cabsimType`, `gain`, `treb`, `mid`, `bass`.
All except LinearGain also: `sag`, `bias`.

| Model | cabsimType | Extra parameters |
|---|---|---|
| Twin65 | 65twn | bright |
| Twin57 | 65twn | presence |
| Deluxe65 | 65dlx | |
| Deluxe57 | 57dlx | |
| Champ57 | 57champ | |
| Princeton65 | 65prince | |
| Silvertone | 57dlx | |
| Bassman59 | 59bman | presence, blend |
| Excelsior | exlsr | bright |
| Ac30Tb | 2x12c | master, cut, bright |
| SuperSonic | 1x12ss | gain2, master |
| Plexi87 | 4x12g, 4x12g2 | presence, blend |
| Jcm800 | 4x12m | master, presence |
| Rect2 | 4x12v2 | master, presence |
| MetalRect2 | 4x12r | master, presence |
| DR103 | 4x12v | master, presence |
| MetalEvh3 | 4x12frd | master, presence |
| Or120 | 4x12m2 | master |
| LinearGain | none | (no sag, bias) |

## Effects

All effect nodes (not passthru) also have `bypass` (bool) and `bypassType` (string). Listed: extras only.

### Stomp

| Model | Parameters |
|---|---|
| SimpleCompressor | type (low, medium, high, super) |
| Overdrive | gain, level, low, mid, high |
| MythicDrive | gain, outputLevel, treble |
| Greenbox | gain, level, tone, blend |
| BigFuzz | gain, level, tone |
| VariFuzz | gain, level, tone (string, "normal") |
| Sustain | sensitivity, outputLevel |
| ChromeGate | threshold, attenuation, hysteresis, gateDetectorPosition |
| Octobot | level, octdown, octup |
| MustangFiveBandEq1 | low, lowmid, mid, highmid, high, gain |

### Mod

| Model | Parameters |
|---|---|
| ChorusTriangle | level, rateHz, tapTimeBPM, noteDivision, depth, lrPhase, avgDelay |
| Vibratone | level, rotor, tapTimeBPM, noteDivision, depth, feedback, phase |
| SineTremolo | level, rate, tapTimeBPM, noteDivision, duty, dist, shape (number) |
| Phaser | level, rate, tapTimeBPM, noteDivision, depth, feedback, shape (string, "sine") |
| TriangleFlanger | level, rate, tapTimeBPM, noteDivision, depth, feedback, phase |
| StepFilter | level, rate, tapTimeBPM, noteDivision, hiFrq, loFrq, reson |
| EcFilter | level, q, thresh, mode ("High Up"), type ("Band Pass") |

### Delay

| Model | Parameters |
|---|---|
| TapeDelayLite | wetLvl, dlyTime, tapTimeBPM, noteDivision, feedback, stereoSpread, wowLevel |
| MonoDelay | level, time, tapTimeBPM, noteDivision, feedback, brite, attenuate |

### Reverb

| Model | Parameters |
|---|---|
| Spring65 | wetLvl, decay, dwell, diffuse, tone |
| SmallRoomReverb, LargePlate, LargeHallReverb, ArenaReverb | level, decay, dwell, diffuse, tone |

## Enums seen

| Parameter | Values |
|---|---|
| bypassType | Pre, Post, PostNoPreKill (Sustain only) |
| gatePreset | off, low, mid, high, super |
| gateDetectorPosition | jack |
| sag | match |
| noteDivision | off |

## Notes

- Same name, different type by model: `tone` (string on VariFuzz, number elsewhere), `shape` (string on Phaser, number on SineTremolo), `type` (SimpleCompressor vs EcFilter).
- Not everything is 0 to 1: `volume`, EQ bands, ChromeGate threshold/attenuation, and Greenbox/BigFuzz `level` are in dB-like ranges.
- Wet level is `wetLvl` on TapeDelayLite and Spring65, `level` elsewhere. Treble is `treble` on MythicDrive, `treb` on amps. Delay time is `dlyTime` on TapeDelayLite, `time` on MonoDelay.
- Passthru nodes may have empty parameters, so `bypass` can be absent.
