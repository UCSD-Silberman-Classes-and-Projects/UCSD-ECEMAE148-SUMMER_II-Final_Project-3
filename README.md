# VR Mixed-Reality Teleoperation

An operator in a VR headset sees a live camera feed from a remote RC vehicle,
points at an object the detector has found, and the vehicle follows or avoids
it. Object detection runs on a host PC; the vehicle drives itself from the
selection; GPS supplies a global reference frame.

This repository holds every software layer of that system — the vehicle code,
the host-side hub, the headset scripts, and the printed chassis parts.

```
 OAK-D ── Raspberry Pi ──────── WiFi ──────── Host PC ──── WiFi/USB ──── Quest / Aura
 camera   donkeycar + VESC                    hub (this repo)            Unity APK
          hub_bridge (ws:8765)                YOLOv8 + WebRTC            video + boxes
          hub_udp    (H.264 → udp:5000)       safety supervisor          ray-cast select
```

The hub runs today against a generated video file with **no camera, no Pi and
no GPU**, so most of the system can be validated before the motor is ever
armed. Start there — see [Quickstart](#quickstart).

---

## Contents

- [Quickstart (no hardware)](#quickstart)
- [Repository layout](#repository-layout)
- [Running against the real vehicle](#running-against-the-real-vehicle)
- [The two video paths](#the-two-video-paths)
- [How the hub works](#how-the-hub-works)
- [Hub command-line reference](#hub-command-line-reference)
- [The headset](#the-headset)
- [Tests](#tests)
- [Printed parts](#printed-parts)

---

## Quickstart

Nothing plugged in. Two terminals.

```bash
pip install -r requirements.txt
python tools/make_test_clip.py         # writes assets/test_clip.mp4

python tools/fake_pi.py                # terminal 1 — stands in for the vehicle
python -m hub.main                     # terminal 2 — the hub
```

Open <http://localhost:8080/>, press **Connect**, click a box to select a
target, press **Follow**. Terminal 1 prints the servo and duty-cycle values
the VESC would receive.

`requirements.txt` pulls in torch and ultralytics, which is a large download.
If you only want to see the system move, the hub falls back to a dependency-free
mock detector — `pip install av aiohttp aiortc websockets numpy` is enough, and
`python -m hub.main --detector mock` will run against the generated clip.

Simulate a WiFi blackout with `python tools/fake_pi.py --drop-after 20`.

---

## Repository layout

| Path                                   | What it is                                                                    |
| -------------------------------------- | ----------------------------------------------------------------------------- |
| `hub/`                                 | Host-PC application. Ingest, detection, safety, WebRTC, vehicle link.         |
| `client/index.html`                    | Browser operator console. Stands in for the headset.                          |
| `unity/Assets/Scripts/Teleop/`         | Headset APK scripts. Third client of the same data channel.                   |
| `raspi_pi/vrStreaming_pi/vrStreaming/` | Vehicle-side streaming and command bridge. Deploys to `/home/pi/vrStreaming`. |
| `raspi_pi/donkeycar_modifications_pi/` | Modified donkeycar `manage.py` and `myconfig.py`.                             |
| `tools/`                               | `fake_pi.py` vehicle simulator, `make_test_clip.py` clip generator.           |
| `tests/`                               | Safety, pipeline, protocol and end-to-end checks.                             |
| `stl-files/`                           | Printed chassis and mount parts. See [Printed parts](#printed-parts).         |
| `PROTOCOL.md`                          | The `link` wire format all three operator clients depend on.                  |
| `assets/test_clip.mp4`                 | Generated test video. Regenerate with `tools/make_test_clip.py`.              |
| `pc_webRTC.py`, `port_test.py`         | Bring-up scratch scripts. See [Known issues](#known-issues-and-gotchas).      |

### Inside `hub/`

| File         | Role                                                      |
| ------------ | --------------------------------------------------------- |
| `ingest.py`  | Demux, decode, pace, distribute to slots                  |
| `detect.py`  | YOLOv8 `track(persist=True)`, or the dependency-free mock |
| `state.py`   | `WorldState` — one lock, one writer per field             |
| `safety.py`  | Watchdogs and mode arbitration                            |
| `vehicle.py` | WebSocket to the Pi, fixed-rate command tick              |
| `vr.py`      | Signaling, video track, bidirectional data channel        |
| `main.py`    | Argument parsing and wiring                               |

---

## Running against the real vehicle

Three machines, started in this order.

### 1. Vehicle — video

Copy `raspi_pi/vrStreaming_pi/vrStreaming/` to `/home/pi/vrStreaming` on the Pi.
The donkeycar `manage.py` in this repo hardcodes that path.

```bash
cd /home/pi/vrStreaming
pip install -r requirements_pi.txt
python hub_udp.py
```

`hub_udp.py` encodes H.264 on the OAK-D's own VPU and pipes it through
GStreamer (`h264parse` → `mpegtsmux`) to the host over UDP. Nothing is encoded
on the Pi's CPU.

**Edit `HUB_IP` at the top of `hub_udp.py` before running it** — it is
hardcoded. Defaults: 640×480 at 20 fps, 2 Mbit, baseline profile with B-frames
disabled and a keyframe every second, all of which keeps decode latency down.

Confirm packets are arriving on the host with `python port_test.py`.

### 2. Vehicle — commands

```bash
python manage.py drive
```

The modified `manage.py` starts the hub bridge itself:

```python
sys.path.insert(0, '/home/pi/vrStreaming')
from hub_bridge import HubPart, start_server
start_server()   # WebSocket listener on ws://0.0.0.0:8765, background thread
V.add(HubPart(), inputs=[], outputs=['user/angle', 'user/throttle'])
```

`hub_bridge.py` translates hub commands into donkeycar's steering and throttle
values. It derives steering from the horizontal centre of the bounding box and
speed from the box's **area** as a fraction of the frame — bigger box means
closer, so slow down, and stop above 25% coverage. Throttle is capped at 0.15.
On `stop`, and whenever the hub disconnects, it returns `0.0, 0.0` rather than
`None`; the VESC part cannot handle `None`.

Relevant `myconfig.py` settings: `DRIVE_TRAIN_TYPE = "VESC"` on
`/dev/ttyACM0` at 115200, `DRIVE_LOOP_HZ = 20`, `VESC_STEERING_SCALE = 0.5`
with `VESC_STEERING_OFFSET = 0.5` to map ±1 onto the 0–1 range the VESC servo
output expects.

### 3. Host PC

```bash
python -m hub.main --source udp://0.0.0.0:5000 --live --pi-ws ws://<pi-ip>:8765
```

`--live` is inferred automatically from a `udp://`, `rtp://`, `rtsp://` or
`srt://` source, so it is optional there, but harmless to pass.

### 4. Headset

Build and sideload the Unity APK, set `HubClient.hubUrl` to the host PC, and
put the headset on. See [The headset](#the-headset).

---

## The two video paths

The repository contains two different ways to get pixels into the headset.
They are alternatives, not stages of one pipeline, and it is worth knowing
which one you are running.

**Path A — through the hub (the main system).**
`hub_udp.py` → host PC → `hub/vr.py` → `HubClient.cs`. Video and detections
arrive on one peer connection, the safety supervisor sits in the middle, and
the operator can select targets. This is the path everything else in this
README describes.

**Path B — direct from the Pi.**
`pi_oak_d_streaming.py` serves its own WebRTC endpoint on the Pi at port 8080
with two tracks: RGB, and stereo depth colourised with `COLORMAP_JET` and
aligned into the RGB camera's frame via `stereo.setDepthAlign(CAM_A)`, so
`depth[y][x]` corresponds to `rgb[y][x]`. `StreamReceiver.cs` is its client.
Signaling is inverted relative to path A — the Pi creates the offer, and Unity
`GET`s `/offer` then `POST`s `/answer`.

Path B has no detection, no selection and no safety supervisor. It exists for
camera and headset bring-up, and for looking at the aligned depth map. Depth is
clamped to 0.5–3.0 m and capped at 12 fps, which is the safe ceiling on USB 2.

---

## How the hub works

```
ingest thread ── PyAV demux + decode ──┬── detect slot ── YOLOv8 ── WorldState
                                       └── render slot ── WebRTC video track

event loop ───── aiohttp /offer + RTCPeerConnection   (headset)
                 data channel "link"  detections out, selections in
                 PiLink               20 Hz command tick out, GPS in
```

Both consumers read from a **single-element overwrite slot**, not a queue. A
queue between decode and inference accumulates lag whenever the consumer falls
behind and never gives it back. The slot always holds the newest frame and
silently drops what could not be kept up with — the same intent as
`cv2.CAP_PROP_BUFFERSIZE = 1`, made explicit.

### Three deliberate departures from the design doc

**`target_depth` moved to the Pi.** The doc has the PC sending a depth in
metres. The PC only ever sees a `COLORMAP_JET` image, and that mapping is not
invertible — recovering metres from it is guesswork. The Pi holds the real
16-bit disparity, so the hub sends only the bbox and lets the vehicle work out
distance for itself. (`hub_bridge.py` currently estimates it from bbox area
rather than sampling the depth map; see [Known issues](#known-issues-and-gotchas).)

**Commands are emitted on a clock, not on events.** A burst of VR messages
cannot flood the vehicle, and silence from the operator does not leave the last
command latched — the tick keeps running and the supervisor downgrades it to
`stop`.

**A second watchdog on the PC.** The Pi's 1 s watchdog protects against the PC
dying. It does not protect against the PC staying alive and confidently sending
stale commands. `hub/safety.py` catches the operator link going stale,
detections going stale, and the selected target disappearing — and it commands
`stop` rather than going quiet. Nothing in that file can promote a mode; only
an explicit operator action arms the vehicle, and recovery after an override is
never automatic.

### Overlay lag

Detection finishes 15–25 ms after the frame it describes, but that frame is
already on its way to the display. Drawing the newest boxes on the newest video
makes them trail moving objects — this is the "boxes float off objects" symptom
in the design doc's known-issues table, and FOV mismatch is only half of it.

The console has an **Overlay lag** slider. Drag it until the boxes sit still on
the target. That number is your measured pipeline depth in frames, and it is
what `DetectionOverlay.overlayLagFrames` should be set to.

### Video encoding

`aiortc` re-encodes decoded frames in software here. That is fine on the A5000
and convenient for development, but it is a decode/encode round trip on the
critical path. To put the OAK-D's hardware H.264 on the wire untouched, replace
`SourceVideoTrack` with a track that yields encoded packets and override the
sender's encoder — Luxonis' `EncodedStreamTrack` in `luxonis/oak-examples` is
the reference implementation. Worth doing when you are chasing the last 20 ms of
the 150 ms budget; not worth doing first.

### Detection backends

`--detector auto` uses YOLOv8 if `ultralytics` imports and falls back to a numpy
colour-blob finder otherwise. The mock produces two stable track IDs against the
generated clip, which is enough to exercise selection, the follow control law,
the deadband and every safety rule. Install `ultralytics` and pass
`--detector yolo` when you want the real thing.

If detection cannot keep up with the camera, `--detect-stride N` runs it on
every Nth frame instead of dropping the frame rate. Note that this widens the
gap between telemetry rate and video rate, which is exactly the case the
headset's `frame_id`-based lag compensation handles and the browser console's
index-based one does not.

---

## Hub command-line reference

| Flag                          | Default                | Meaning                                                                          |
| ----------------------------- | ---------------------- | -------------------------------------------------------------------------------- |
| `--source`                    | `assets/test_clip.mp4` | File path, or `udp://0.0.0.0:5000` for the live Pi stream                        |
| `--live`                      | inferred               | Low-latency demux, no pacing. Implied by `udp://`, `rtp://`, `rtsp://`, `srt://` |
| `--http-host` / `--http-port` | `0.0.0.0` / `8080`     | Where the operator console and `/offer` are served                               |
| `--pi-ws`                     | `ws://127.0.0.1:8765`  | Vehicle command WebSocket                                                        |
| `--no-pi`                     | off                    | Skip the vehicle link entirely. Nothing is commanded.                            |
| `--detector`                  | `auto`                 | `auto`, `yolo` or `mock`                                                         |
| `--detect-stride`             | `1`                    | Run the detector every N frames                                                  |
| `--weights`                   | `yolov8n.pt`           | YOLO weights                                                                     |
| `--conf`                      | `0.5`                  | Detection confidence threshold                                                   |
| `--command-hz`                | `20.0`                 | Command tick rate to the Pi                                                      |
| `--vr-timeout`                | `0.5`                  | Operator-link staleness before the supervisor forces `stop`                      |
| `-v`, `--verbose`             | off                    | Debug logging, including per-frame detector timings                              |

`GET /health` returns peer count, frame ID, ingest FPS, current mode and Pi
connection state — useful for checking the hub without opening the console.

---

## The headset

`unity/Assets/Scripts/Teleop/` holds the APK scripts. They speak the same data
channel as the browser console, so the hub has one operator interface, not two.
See [`PROTOCOL.md`](PROTOCOL.md) for the wire format.

| Script                | Role                                                                            |
| --------------------- | ------------------------------------------------------------------------------- |
| `HubClient.cs`        | WebRTC connection to the hub, telemetry in, selections and heartbeats out       |
| `HubMessages.cs`      | Serializable DTOs. Field names must match the JSON keys literally.              |
| `WorldBuffer.cs`      | Ring buffer of recent snapshots, indexed by `frame_id` for lag compensation     |
| `VideoSurface.cs`     | The video plane, and the single source of truth for pixel → world mapping       |
| `DetectionOverlay.cs` | Pooled world-space boxes, generated at runtime — no prefabs                     |
| `TargetSelector.cs`   | Ray-cast selection, smallest-box tie-break                                      |
| `VRTargetSelector.cs` | Touch-controller subclass: right trigger selects, left clears, thumbstick stops |
| `LinkHud.cs`          | Head-locked mode and link-health readout                                        |
| `EyeLayerSetup.cs`    | Per-eye culling masks for stereo quads                                          |
| `StreamReceiver.cs`   | Client for the direct Pi path (path B). Not used with the hub.                  |
| `HubPing.cs`          | One-shot HTTP reachability check against the hub                                |

### Alignment

The overlay does not depend on Unity's camera FOV. The video plane and the
detection boxes are sized from a single `VideoSurface.sensorHFovDeg` constant
and share a parent transform, so alignment is a 2D mapping that cannot drift.
That removes the design doc's "match Unity camera FOV to OAK-D FOV exactly"
failure mode rather than documenting it.

`sensorHFovDeg` defaults to 69°, which is the OAK-D RGB (IMX378) figure. Check
your unit — the OAK-D Lite (IMX214) is close but not identical — and if you crop
or letterbox on the Pi, use the FOV of the _cropped_ image. It fails in exactly
one visible way: boxes uniformly too large or too small, scaling from the frame
centre. Much easier to spot than gradual drift toward the edges.

### Before you build

Set the IP addresses. Three scripts default to lab addresses that are almost
certainly not yours: `HubClient.hubUrl`, `HubPing.hubUrl` and
`StreamReceiver.piIpAddress`.

The heartbeat is a safety mechanism, not a keepalive. Drive it from the render
loop so a frozen client stops beating — a heartbeat on a background thread
survives a hung headset and leaves the vehicle driving for an operator who can
no longer see.

---

## Tests

```bash
python tests/test_safety.py      # supervisor rules — the layer you cannot bench-test
python tests/test_pipeline.py    # ingest -> detect -> state, no network
python tests/test_e2e.py         # needs fake_pi + hub running; headless operator
python tests/test_protocol.py    # wire format vs. the Unity DTOs
```

`test_protocol.py` exists because C# is not compiled by CI here and
`JsonUtility` fails silently: a renamed key becomes `0` or `""` in the headset
with no error anywhere. Run it after touching the payload in `hub/vr.py`.

`test_e2e.py` negotiates a real WebRTC session, selects a target, requests
follow, then stops sending heartbeats and asserts the vehicle is commanded to
stop within the watchdog window. It needs `tools/fake_pi.py` and
`python -m hub.main` already running. A `MediaStreamError` traceback after
`E2E OK` is teardown noise, not a failure.

---

## Printed parts

Seven binary STLs in `stl-files/`. Every one is well under GitHub's 10 MB
render limit, so **clicking any filename below opens GitHub's interactive 3D
viewer** — drag to spin, right-drag to pan, scroll to zoom.

| Part                                       | Bounding box      | Triangles | Size   |
| ------------------------------------------ | ----------------- | --------- | ------ |
| [Front Part](stl-files/front-bumper.stl)   | 221 × 241 × 67    | 3 580     | 175 KB |
| [Back Part](stl-files/rear-bumper.stl)     | 222 × 162 × 60    | 3 972     | 194 KB |
| [Side Part](stl-files/side-cover.stl)      | 20 × 137 × 60     | 816       | 40 KB  |
| [Front Cover](stl-files/front-cover.stl)   | 210 × 255 × 79    | 7 640     | 373 KB |
| [Middle Cover](stl-files/middle-cover.stl) | 210 × 100 × 79    | 3 628     | 177 KB |
| [Back Cover](stl-files/rear-cover.stl)     | 210 × 147 × 75    | 9 094     | 444 KB |
| [Lidar Mount](stl-files/lidar-mount.stl)   | 5.3 × 3.6 × 4.0 ⚠ | 14 552    | 711 KB |

Bounding boxes are in model units; the six chassis parts are consistent with
millimetres. **The Lidar Mount is not** — it is roughly 40× smaller than the
parts it bolts to. Scale it
before slicing.

<details>
<summary><b>Front Part</b> — 221 × 241 × 67 mm</summary>

Front chassis section. Pairs with the Front Cover.

[Open the 3D viewer →](stl-files/front-bumper.stl)

</details>

<details>
<summary><b>Back Part</b> — 222 × 162 × 60 mm</summary>

Rear chassis section. Pairs with the Back Cover.

[Open the 3D viewer →](stl-files/rear-bumper.stl.stl)

</details>

<details>
<summary><b>Side Part</b> — 20 × 137 × 60 mm</summary>

Side rail. The thin 20 mm dimension is the print's weak axis — orient
accordingly. Smallest part in the set at 816 triangles.

[Open the 3D viewer →](stl-files/side-cover.stl)

</details>

<details>
<summary><b>Front Cover</b> — 210 × 255 × 79 mm</summary>

Largest footprint in the set. Check it against your bed size before slicing.

[Open the 3D viewer →](stl-files/front-cover.stl)

</details>

<details>
<summary><b>Middle Cover</b> — 210 × 100 × 79 mm</summary>

Centre section, spanning front and back covers.

[Open the 3D viewer →](stl-files/middle-cover.stl)

</details>

<details>
<summary><b>Back Cover</b> — 210 × 147 × 75 mm</summary>

Filename is marked `reprint`; treat it as superseding any earlier back cover.

[Open the 3D viewer →](stl-files/rear-cover.stl)

</details>

<details>
<summary><b>Lidar Mount</b> — 5.3 × 3.6 × 4.0 ⚠ scale check needed</summary>

Highest triangle count in the set, and the only part whose units look wrong.
Verify the scale against the chassis before printing.

[Open the 3D viewer →](stl-files/lidar-mount.stl)

</details>

---

## Safety

The vehicle starts halted. `WorldState.mode` is `"stop"` by construction and
only an explicit operator action arms it. Recovery after a safety override is
never automatic — the operator must re-arm.

Four rules across two independent layers. The vehicle's own watchdog covers the
host PC dying; the other three cover the host staying alive and sending stale
commands. All four must keep working:

| Watchdog            | Where           | Timeout     | Catches                               |
| ------------------- | --------------- | ----------- | ------------------------------------- |
| Operator link       | `hub/safety.py` | 0.5 s       | Frozen headset, operator out of range |
| Detection freshness | `hub/safety.py` | 0.4 s       | Detector stalled or crashed           |
| Target presence     | `hub/safety.py` | 0.3 s grace | Selected object left the frame        |
| Command silence     | Vehicle         | 1.0 s       | Host PC dead or off the network       |

The reason for every override is sent to the operator in the `reason` field and
shown in the console and the HUD. This matters: a silent downgrade to `stop`
looks exactly like a dead link, and an operator who thinks the link is dead does
the wrong thing next.

Bench-test with the wheels off the ground first. Kill the WiFi mid-run and
confirm the vehicle stops within a second before you put it on the floor.
