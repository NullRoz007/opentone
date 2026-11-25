"""Builds a set of custom LT25 presets from the 2.1.4 firmware's own factory node templates.

Each design lists the model for each node and only the values that differ from Fender's factory
template node for that model, so every node keeps exactly the keys (including hidden ones such as
bypassType, tapTimeBPM, noteDivision) that the firmware's own presets use.
"""
import json, struct, sys, os, re

FW, OUT = sys.argv[1], sys.argv[2]
a = open(FW, 'rb').read()
B = 0x08020200
table = struct.unpack_from('<30I', a, 0x81bb7c0 - B)

def cstr(addr):
    o = addr - B
    return a[o:a.index(b'\0', o)]

factory = [json.loads(cstr(p)) for p in table]
capacity = []
for p in table:
    o = p - B; e = a.index(b'\0', o); pad = 0
    while (e + 1 + pad) % 4 and a[e + 1 + pad] == 0: pad += 1
    capacity.append(e - o + pad)

# First factory node for each (nodeId, model) is the template.
templates = {}
for f in factory:
    for n in f['audioGraph']['nodes']:
        templates.setdefault((n['nodeId'], n['FenderId']), n)

def node(node_id, model, **values):
    t = templates[(node_id, 'DUBS_' + model)]
    # Knobs have 91 steps, so 3 decimals is plenty; Fender's 6-decimal values cost bytes the slots don't have.
    # dB values round to a thousandth of a dB; tapTimeBPM is left exact.
    params = {k: (round(v, 3) if isinstance(v, float) and k != 'tapTimeBPM' else v) for k, v in t['dspUnitParameters'].items()}
    for k, v in values.items():
        if k not in params: raise SystemExit(f'{model} has no parameter {k}')
        params[k] = v
    if 'bypass' in params: params['bypass'] = False        # every effect in these presets is on
    out = {}
    for k in t:                                             # keep the template's key order
        out[k] = params if k == 'dspUnitParameters' else t[k]
    return out

NONE = None
def preset(name, stomp, mod, amp, delay, reverb):
    halves = name.split('|')
    assert len(halves) == 2 and all(len(h) <= 8 for h in halves), name
    display = halves[0].ljust(8) + halves[1].ljust(8)
    assert re.fullmatch(r'[A-Z0-9 ]{16}', display), display
    nodes = [stomp or node('stomp', 'Passthru'), mod or node('mod', 'Passthru'), amp,
             delay or node('delay', 'Passthru'), reverb or node('reverb', 'Passthru')]
    return display, nodes

# tapTimeBPM only matters when noteDivision is on; it is "off" in every template, so it is left as is.
def tape(sec, fb, wet):  return node('delay', 'TapeDelayLite', dlyTime=sec, feedback=fb, wetLvl=wet)
def mono(sec, fb, lvl):  return node('delay', 'MonoDelay', time=sec, feedback=fb, level=lvl)

designs = [
    preset('GLASS|CLEAN',
           node('stomp', 'SimpleCompressor', type='medium'),
           node('mod', 'ChorusTriangle', level=0.4, rateHz=0.35, depth=0.6),
           node('amp', 'Twin65', gain=0.3, treb=0.62, mid=0.45, bass=0.5),
           NONE,
           node('reverb', 'LargePlate', level=0.22, decay=0.55)),
    preset('VELVET|LEAD',
           node('stomp', 'Overdrive', gain=0.45, level=0.55, mid=0.6),
           NONE,
           node('amp', 'SuperSonic', gain=0.55, treb=0.6, mid=0.7, bass=0.6),
           tape(0.42, 0.28, 0.25),
           NONE),
    preset('DELTA|BLUES',
           node('stomp', 'Greenbox', gain=0.4, tone=0.45),
           NONE,
           node('amp', 'Bassman59', gain=0.5, treb=0.55, mid=0.6, bass=0.5, presence=0.55),
           NONE,
           node('reverb', 'Spring65', wetLvl=0.35, decay=0.4)),
    preset('STADIUM|ROCK',
           NONE, NONE,
           node('amp', 'Plexi87', gain=0.72, treb=0.8, bass=0.55),
           tape(0.36, 0.3, 0.2),
           node('reverb', 'ArenaReverb', level=0.25, decay=0.55)),
    preset('DREAM|POP',
           node('stomp', 'SimpleCompressor', type='medium'),
           node('mod', 'ChorusTriangle', level=0.55, rateHz=0.6, depth=0.8),
           node('amp', 'Deluxe65', gain=0.28, treb=0.65, mid=0.55, bass=0.45),
           mono(0.48, 0.4, 0.3),
           node('reverb', 'LargeHallReverb', level=0.35, decay=0.7)),
    preset('CHICKEN|PICKIN',
           node('stomp', 'SimpleCompressor', type='medium'),
           NONE,
           node('amp', 'Twin57', gain=0.3, treb=0.8, mid=0.45, bass=0.5),
           tape(0.11, 0.05, 0.3),
           node('reverb', 'Spring65', wetLvl=0.25, decay=0.3)),
    preset('THREE|CHORDS',
           NONE, NONE,
           node('amp', 'Jcm800', gain=0.8, treb=0.65, mid=0.55, bass=0.6),
           NONE,
           NONE),
    preset('MIDNIGHT|JAZZ',
           NONE, NONE,
           node('amp', 'LinearGain', gain=0.35, treb=0.35, mid=0.55, bass=0.6),
           NONE,
           node('reverb', 'SmallRoomReverb', level=0.25, decay=0.4)),
    preset('WALL OF|FUZZ',
           node('stomp', 'BigFuzz', gain=0.7, tone=0.45),
           NONE,
           node('amp', 'Or120', gain=0.55, treb=0.55, mid=0.5, bass=0.7),
           NONE,
           node('reverb', 'LargeHallReverb', level=0.25, decay=0.6)),
    preset('SURF|SPRING',
           NONE,
           node('mod', 'SineTremolo', rate=6.5, level=0.75),
           node('amp', 'Twin65', gain=0.32, treb=0.7, mid=0.45, bass=0.5),
           NONE,
           node('reverb', 'Spring65', wetLvl=0.8, decay=0.6, dwell=0.7)),
    preset('BRITPOP|JANGLE',
           node('stomp', 'SimpleCompressor', type='medium'),
           node('mod', 'ChorusTriangle', level=0.3, rateHz=0.25, depth=0.4),
           node('amp', 'Ac30Tb', gain=0.35, treb=0.75, mid=0.6, bass=0.55),
           tape(0.3, 0.15, 0.15),
           node('reverb', 'SmallRoomReverb', level=0.2)),
    preset('DOOM|CLOUD',
           node('stomp', 'Octobot', octdown=1, octup=0, level=0.6),
           NONE,
           node('amp', 'Or120', gain=0.85, treb=0.45, mid=0.55, bass=0.85),
           NONE,
           node('reverb', 'ArenaReverb', level=0.3, decay=0.7)),
    preset('DJENT|MACHINE',
           node('stomp', 'ChromeGate'),
           NONE,
           node('amp', 'MetalRect2', gain=0.7, treb=0.65, mid=0.3, bass=0.6, presence=0.5),
           NONE, NONE),
    preset('SHRED|LEAD',
           node('stomp', 'Overdrive', gain=0.3, level=0.6, mid=0.65),
           NONE,
           node('amp', 'MetalEvh3', gain=0.8, treb=0.65, mid=0.45, bass=0.4),
           mono(0.38, 0.3, 0.25),
           node('reverb', 'LargeHallReverb', level=0.2, decay=0.5)),
    preset('SPACE|ECHO',
           NONE, NONE,
           node('amp', 'Deluxe65', gain=0.35, treb=0.55, mid=0.6, bass=0.5),
           tape(0.5, 0.6, 0.35),
           NONE),
    preset('FUNKY|WAH',
           node('stomp', 'SimpleCompressor', type='medium'),
           node('mod', 'EcFilter', q=0.7, thresh=0.4, level=0.8),
           node('amp', 'Twin65', gain=0.3, treb=0.6, mid=0.55, bass=0.45),
           NONE,
           node('reverb', 'SmallRoomReverb', level=0.15)),
    preset('SOUL|TREMOLO',
           NONE,
           node('mod', 'SineTremolo', rate=4.2, level=0.7),
           node('amp', 'Deluxe57', gain=0.4, treb=0.6, mid=0.55, bass=0.5),
           NONE,
           node('reverb', 'Spring65', wetLvl=0.4, decay=0.45)),
    preset('TINY|CRUNCH',
           NONE, NONE,
           node('amp', 'Champ57', gain=0.75, treb=0.7, mid=0.6, bass=0.3),
           NONE,
           NONE),
    preset('TEXAS|FLOOD',
           node('stomp', 'MythicDrive', gain=0.45, treble=0.6, outputLevel=0.6),
           NONE,
           node('amp', 'Bassman59', gain=0.6, treb=0.6, mid=0.55, bass=0.55, presence=0.6),
           NONE,
           node('reverb', 'Spring65', wetLvl=0.3, decay=0.35)),
    preset('PHASE|SHIFT',
           NONE,
           node('mod', 'Phaser', rate=0.35, depth=0.8, feedback=0.5, level=0.9),
           node('amp', 'Plexi87', gain=0.45, treb=0.75, bass=0.5),
           NONE,
           node('reverb', 'LargePlate', level=0.18)),
    preset('JET|FLANGE',
           NONE,
           node('mod', 'TriangleFlanger', rate=0.08, depth=0.8, feedback=0.75),
           node('amp', 'Jcm800', gain=0.7, treb=0.65, mid=0.5, bass=0.6),
           NONE,
           node('reverb', 'SmallRoomReverb', level=0.15)),
    preset('ROTARY|SPEAKER',
           NONE,
           node('mod', 'Vibratone', rotor=4.5, depth=0.5, level=0.9),
           node('amp', 'Twin57', gain=0.4, treb=0.6, mid=0.55, bass=0.55),
           NONE,
           node('reverb', 'LargeHallReverb', level=0.18)),
    preset('RAW|FUZZ',
           node('stomp', 'VariFuzz', gain=0.85, level=0.15),
           NONE,
           node('amp', 'Silvertone', gain=0.6, treb=0.6, mid=0.7, bass=0.6),
           NONE,
           NONE),
    preset('SYNTH|STEPS',
           node('stomp', 'Octobot', octdown=1, octup=1, level=0.5),
           node('mod', 'StepFilter', rate=3.0, reson=0.7, level=0.8),
           node('amp', 'LinearGain', gain=0.45, treb=0.55, mid=0.5, bass=0.55),
           NONE,
           node('reverb', 'LargePlate', level=0.2)),
    preset('OLD|SCHOOL',
           NONE,
           NONE,
           node('amp', 'Excelsior', gain=0.4, treb=0.4, mid=0.55, bass=0.7),
           NONE,
           node('reverb', 'Spring65', wetLvl=0.45, decay=0.5)),
    preset('SEATTLE|GRUNGE',
           node('stomp', 'Greenbox', gain=0.6, tone=0.4),
           NONE,
           node('amp', 'Rect2', gain=0.55, treb=0.5, mid=0.35, bass=0.65),
           NONE,
           NONE),
    preset('SCOOPED|METAL',
           node('stomp', 'MustangFiveBandEq1', low=3, lowmid=0, mid=-4, highmid=1, high=3),
           NONE,
           node('amp', 'MetalRect2', gain=0.75, treb=0.7, mid=0.25, bass=0.7),
           NONE, NONE),
    preset('ENDLESS|SUSTAIN',
           node('stomp', 'Sustain', sensitivity=0.6),
           NONE,
           node('amp', 'Princeton65', gain=0.4, treb=0.7, mid=0.6, bass=0.45),
           mono(0.4, 0.35, 0.25),
           node('reverb', 'LargePlate', level=0.25, decay=0.6)),
    preset('AMBIENT|SWELL',
           node('stomp', 'SimpleCompressor', type='medium'),
           node('mod', 'ChorusTriangle', level=0.45, rateHz=0.2, depth=0.7),
           node('amp', 'DR103', gain=0.3, treb=0.6, mid=0.6, bass=0.6),
           tape(0.6, 0.55, 0.4),
           node('reverb', 'LargeHallReverb', level=0.4, decay=0.85)),
    preset('ACOUSTIC|STRUM',
           node('stomp', 'SimpleCompressor', type='medium'),
           NONE,
           node('amp', 'LinearGain', gain=0.4, treb=0.65, mid=0.45, bass=0.5),
           NONE,
           node('reverb', 'SmallRoomReverb', level=0.3, decay=0.45)),
]
assert len(designs) == 30 and len({d for d, _ in designs}) == 30

# ---- serialise exactly like the factory data (": " after keys, no other whitespace) ------------------
def num(v):
    if isinstance(v, bool): return 'true' if v else 'false'
    if isinstance(v, int): return str(v)
    r = round(v, 6)
    return str(int(r)) if r == int(r) else repr(r)

def ser(x):
    if isinstance(x, dict): return '{' + ','.join(json.dumps(k) + ': ' + ser(v) for k, v in x.items()) + '}'
    if isinstance(x, list): return '[' + ','.join(ser(v) for v in x) + ']'
    if isinstance(x, str):  return json.dumps(x)
    return num(x)

def build(display, nodes, slot_index):
    base = factory[slot_index]
    out = {}
    for k, v in base.items():
        if k == 'info':
            info = dict(v); info['displayName'] = display; out[k] = info
        elif k == 'audioGraph':
            g = {}
            for gk in v: g[gk] = nodes if gk == 'nodes' else v[gk]
            out[k] = g
        else:
            out[k] = v
    return ser(out)

# Match presets to slots so each fits: largest preset to the slot with the most room.
size = {i: [len(build(d, n, s)) for s in range(30)] for i, (d, n) in enumerate(designs)}
order = sorted(range(30), key=lambda i: -max(size[i]))
free = set(range(30)); assign = {}
for i in order:
    fits = [s for s in free if size[i][s] <= capacity[s]]
    if not fits: raise SystemExit(f'{designs[i][0]!r} ({min(size[i])} bytes) fits no remaining slot')
    s = min(fits, key=lambda s: capacity[s] - size[i][s])     # best fit: leave big slots for big presets
    assign[i] = s; free.remove(s)

os.makedirs(OUT, exist_ok=True)
for f in os.listdir(OUT):
    if f.endswith('.json'): os.remove(os.path.join(OUT, f))
for i, (d, n) in sorted(designs and enumerate(designs), key=lambda x: assign[x[0]]):
    s = assign[i]
    text = build(d, n, s)
    name = re.sub(r' +', ' ', d).strip()
    path = os.path.join(OUT, f'{s + 1:02d} {name}.json')
    with open(path, 'w', encoding='utf-8', newline='\n') as fh: fh.write(text + '\n')
    amp = n[2]['FenderId'][5:]
    fx = [x['FenderId'][5:] for x in (n[0], n[1], n[3], n[4]) if x['FenderId'] != 'DUBS_Passthru']
    print(f'{s + 1:2}|{name}|{amp}|{", ".join(fx) or "-"}|{len(text)}/{capacity[s]}|{path}')
