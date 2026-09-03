"""
hub_bridge.py
Receives hub commands over WebSocket and injects steering/throttle
into the donkeycar vehicle memory.

Run alongside manage.py:
    python hub_bridge.py &
    python manage.py drive
"""

import asyncio
import json
import logging

import websockets

log = logging.getLogger("hub_bridge")
logging.basicConfig(level=logging.INFO,
                    format="%(asctime)s %(levelname)-7s %(message)s")

# ── Shared state ─────────────────────────────────────────────────────────────
# These are written by the WebSocket handler and read by HubPart below.
global _steering, _throttle, _mode
_steering  = 0.0
_throttle  = 0.0
_mode      = "stop"


class HubPart:
    """
    Donkeycar part that bridges hub commands into the vehicle loop.
    Add to your Vehicle like:

        bridge = HubPart()
        V.add(bridge, inputs=[], outputs=['user/steering', 'user/throttle'])
    """

    def run(self):
        global _steering, _throttle, _mode
        if _mode == "stop":
            return 0.0, 0.0   # was None, None — VESC can't handle None
        print(f"HubPart: mode={_mode} steer={_steering:.2f} throttle={_throttle:.2f}", flush=True)
        return _steering, _throttle

    def shutdown(self):
        pass


# ── Command translation ───────────────────────────────────────────────────────
def bbox_to_steering(bbox, frame_width):
    if bbox is None or frame_width == 0:
        return 0.0
    x1, y1, x2, y2 = bbox
    centre_x = (x1 + x2) / 2.0
    error = (centre_x / frame_width) - 0.5
    return max(-1.0, min(1.0, error * 2.0))

def bbox_to_throttle(bbox, frame_width, frame_height):
    """
    Estimate distance from bbox area. Larger bbox = closer = slower.
    Returns throttle 0.0 to 0.25.
    """
    if bbox is None:
        return 0.0
    x1, y1, x2, y2 = bbox
    bbox_area = (x2 - x1) * (y2 - y1)
    frame_area = frame_width * frame_height

    ratio = bbox_area / frame_area  # 0.0 (far) to 1.0 (fills frame)

    STOP_THRESHOLD  = 0.25   # stop if bbox fills >25% of frame
    SLOW_THRESHOLD  = 0.12   # slow down if bbox fills >12%
    MAX_THROTTLE    = 0.15
    SLOW_THROTTLE   = 0.10

    if ratio >= STOP_THRESHOLD:
        return 0.0            # too close — stop
    elif ratio >= SLOW_THRESHOLD:
        return SLOW_THROTTLE  # getting close — slow
    else:
        return MAX_THROTTLE   # far away — full follow speed


def cmd_to_controls(cmd):
    global _steering, _throttle, _mode

    mode = cmd.get("mode", "stop")
    _mode = mode

    if mode == "stop":
        _steering = 0.0
        _throttle = 0.0
        return

    bbox        = cmd.get("bbox")
    frame_width = cmd.get("frame_width", 640)
    frame_height = cmd.get("frame_height", 480)

    if mode == "follow":
        _steering = bbox_to_steering(bbox, frame_width)
        _throttle = bbox_to_throttle(bbox, frame_width, frame_height)

    elif mode == "avoid":
        _steering = -bbox_to_steering(bbox, frame_width)
        _throttle = bbox_to_throttle(bbox, frame_width, frame_height)

    elif mode == "manual":
        _steering = 0.0
        _throttle = 0.0


# ── WebSocket server ──────────────────────────────────────────────────────────
def start_server():
    """Start the WebSocket server in a background thread."""
    import threading
    def _run():
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        loop.run_until_complete(main())
    t = threading.Thread(target=_run, daemon=True)
    t.start()
    log.info("Hub bridge thread started")

async def handler(websocket):
    global _mode
    log.info("Hub connected from %s", websocket.remote_address)
    try:
        async for raw in websocket:
            log.info("RAW: %s", raw)   # ← add this
            try:
                cmd = json.loads(raw)
            except json.JSONDecodeError:
                continue
            cmd_to_controls(cmd)
    except websockets.ConnectionClosed:
        pass
    finally:
        log.info("Hub disconnected — stopping vehicle")
        _mode = "stop"

async def main():
    log.info("Hub bridge listening on ws://0.0.0.0:8765")
    async with websockets.serve(handler, "0.0.0.0", 8765):
        await asyncio.Future()   # run forever


if __name__ == "__main__":
    asyncio.run(main())