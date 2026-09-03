# """
# OAK-D Lite → WebRTC Streaming Server
# DepthAI v3 compatible
# """

# import asyncio
# import fractions
# import time
# import numpy as np
# import cv2
# import depthai as dai
# from aiohttp import web
# from aiortc import RTCPeerConnection, RTCSessionDescription, VideoStreamTrack
# from av import VideoFrame

# HOST_IP = "0.0.0.0"
# PORT    = 8080
# FPS     = 30
# WIDTH   = 640
# HEIGHT  = 480

# pcs = set()
# pipeline = None
# rgb_queue = None


# class OakRgbTrack(VideoStreamTrack):
#     kind = "video"

#     def __init__(self, queue):
#         super().__init__()
#         self.queue = queue
#         self._pts = 0
#         self._time_base = fractions.Fraction(1, FPS)
#         self._frame_duration = 1.0 / FPS
#         self._next_time = time.monotonic()
#         self._frame_count = 0

#     async def recv(self):
#         try:
#             self._frame_count += 1
#             if self._frame_count % 30 == 0:
#                 print(f"[Track] Frame #{self._frame_count}")

#             in_frame = self.queue.tryGet()
#             if in_frame is None:
#                 await asyncio.sleep(0.008)
#                 in_frame = self.queue.tryGet()

#             if in_frame is not None:
#                 bgr = in_frame.getCvFrame()
#             else:
#                 bgr = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)
#                 bgr[:] = (0, 60, 0)

#             if bgr.shape[1] != WIDTH or bgr.shape[0] != HEIGHT:
#                 bgr = cv2.resize(bgr, (WIDTH, HEIGHT))

#             frame = VideoFrame.from_ndarray(bgr, format="bgr24")
#             frame.pts = self._pts
#             frame.time_base = self._time_base
#             self._pts += 1

#             now = time.monotonic()
#             wait = self._next_time - now
#             if wait > 0:
#                 await asyncio.sleep(wait)
#             self._next_time = max(now, self._next_time) + self._frame_duration

#             return frame

#         except Exception as e:
#             print(f"[Track] Error: {e}")
#             black = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)
#             frame = VideoFrame.from_ndarray(black, format="bgr24")
#             frame.pts = self._pts
#             frame.time_base = self._time_base
#             self._pts += 1
#             return frame


# def start_oak():
#     global pipeline, rgb_queue
#     if pipeline is not None:
#         return

#     print("[OAK] Starting DepthAI v3 pipeline...")

#     pipeline = dai.Pipeline()

#     # DepthAI v3 style (same as your working script)
#     cam = pipeline.create(dai.node.Camera).build()

#     output = cam.requestOutput(
#         size=(WIDTH, HEIGHT),
#         type=dai.ImgFrame.Type.BGR888p,
#         fps=FPS
#     )

#     rgb_queue = output.createOutputQueue(maxSize=4, blocking=False)

#     pipeline.start()
#     print("[OAK] Pipeline started successfully")


# async def offer(request):
#     start_oak()

#     pc = RTCPeerConnection()
#     pcs.add(pc)

#     @pc.on("connectionstatechange")
#     async def on_connectionstatechange():
#         print(f"[WebRTC] connectionState → {pc.connectionState}")
#         if pc.connectionState in ("failed", "closed"):
#             await pc.close()
#             pcs.discard(pc)

#     track = OakRgbTrack(rgb_queue)
#     pc.addTrack(track)
#     print("[WebRTC] Track added")

#     offer = await pc.createOffer()
#     await pc.setLocalDescription(offer)
#     print("[WebRTC] Offer created")

#     return web.json_response({
#         "sdp": pc.localDescription.sdp,
#         "type": pc.localDescription.type
#     })


# async def answer(request):
#     data = await request.json()
#     print("[WebRTC] Answer received")

#     if not pcs:
#         return web.json_response({"error": "no peer connection"}, status=400)

#     pc = next(iter(pcs))
#     desc = RTCSessionDescription(sdp=data["sdp"], type=data["type"])
#     await pc.setRemoteDescription(desc)
#     print("[WebRTC] Streaming started")

#     return web.json_response({"status": "ok"})


# async def on_shutdown(app):
#     for pc in list(pcs):
#         await pc.close()
#     pcs.clear()
#     if pipeline is not None:
#         try:
#             pipeline.stop()
#         except:
#             pass


# if __name__ == "__main__":
#     app = web.Application()
#     app.on_shutdown.append(on_shutdown)
#     app.router.add_get("/offer", offer)
#     app.router.add_post("/answer", answer)

#     print(f"[Server] Running on http://{HOST_IP}:{PORT}")
#     web.run_app(app, host=HOST_IP, port=PORT, print=None)


# REFINED CODE BELOW, ABOVE IS COMMENTED OUT FOR REFERENCE (WORKING CODE BELOW)
# """
# =============================================================
#   pi_stream_final_v2.py  —  OAK-D Lite WebRTC Server
#   DepthAI v3.9.0  |  RGB + Aligned Depth  |  aiortc  |  aiohttp
# =============================================================

# DEPTHAI v3.9.0 PIPELINE DESIGN:
#   Camera.build()                    ← v3 unified camera node (replaces ColorCamera)
#     └─ requestOutput(BGR)           ← RGB stream via v3 OutputQueue
#     └─ requestOutput(RAW/GRAY)      ← feeds StereoDepth align target

#   Camera (left CAM_B)  ┐
#   Camera (right CAM_C) ┘→ StereoDepth → depth aligned to RGB FOV
#                                       └─ createOutputQueue directly on stereo.depth

#   KEY CHANGE FROM v2:
#     Depth is aligned to the RGB camera using:
#       stereo.setDepthAlign(dai.CameraBoardSocket.CAM_A)
#     This means every depth pixel maps 1:1 to the RGB pixel at
#     the same (x, y) — essential for Unity overlay accuracy.

# RUN:
#   python pi_stream_final_v2.py
#   Unity GET  http://<PI_IP>:8080/offer
#   Unity POST http://<PI_IP>:8080/answer
# =============================================================
# """

import asyncio
import fractions
import time
import threading

import numpy as np
import cv2
import depthai as dai

from aiohttp import web
from aiortc import RTCPeerConnection, RTCSessionDescription, VideoStreamTrack
from av import VideoFrame

# ─────────────────────────────────────────────────────────────
#  CONFIG
# ─────────────────────────────────────────────────────────────
HOST_IP  = "0.0.0.0"
PORT     = 8080

FPS      = 12           # USB2 safe — do not exceed 15 on USB2
WIDTH    = 640
HEIGHT   = 480

# Depth clamp — OAK-D Lite reliable stereo range
DEPTH_MIN_MM = 500      # 0.5 m
DEPTH_MAX_MM = 3000     # 3.0 m

# Colourmap: COLORMAP_JET  = blue(far) → red(close)
#            COLORMAP_TURBO = better perceptual uniformity
DEPTH_COLORMAP = cv2.COLORMAP_JET

# ─────────────────────────────────────────────────────────────
#  GLOBALS
# ─────────────────────────────────────────────────────────────
_pipeline  = None
_rgb_queue = None
_dep_queue = None
_pcs       = set()
_oak_lock  = threading.Lock()


# ═════════════════════════════════════════════════════════════
#  DEPTHAI v3.9.0 PIPELINE
# ═════════════════════════════════════════════════════════════

def start_oak():
    """
    Build and start the DepthAI v3.9.0 pipeline with:
      - RGB via Camera.build() + requestOutput()   (v3 API)
      - Depth aligned to RGB FOV via StereoDepth   (v3 direct queue API)

    DEPTH ALIGNMENT:
      stereo.setDepthAlign(dai.CameraBoardSocket.CAM_A)
      This reprojects the stereo depth map into the RGB camera's
      perspective so that depth[y][x] corresponds to rgb[y][x].
    """
    global _pipeline, _rgb_queue, _dep_queue

    with _oak_lock:
        if _pipeline is not None:
            return

        print(f"[OAK] Starting pipeline — DepthAI {dai.__version__}")
        _pipeline = dai.Pipeline()

        # ── RGB camera (v3 API) ───────────────────────────────
        cam = _pipeline.create(dai.node.Camera).build()

        rgb_out = cam.requestOutput(
            size=(WIDTH, HEIGHT),
            type=dai.ImgFrame.Type.BGR888p,
            fps=FPS
        )

        _rgb_queue = rgb_out.createOutputQueue(maxSize=4, blocking=False)
        print(f"[OAK] RGB ready — {WIDTH}x{HEIGHT}@{FPS}fps")

        # ── Stereo depth (v3.9.0 API) ─────────────────────────
        # Camera node replaces MonoCamera in v3
        mono_l = _pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_B)
        mono_r = _pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_C)

        stereo = _pipeline.create(dai.node.StereoDepth)

        # Get mono outputs from Camera nodes
        mono_l_out = mono_l.requestOutput(
            size=(640, 400),
            type=dai.ImgFrame.Type.GRAY8,
            fps=FPS
        )
        mono_r_out = mono_r.requestOutput(
            size=(640, 400),
            type=dai.ImgFrame.Type.GRAY8,
            fps=FPS
        )

        mono_l_out.link(stereo.left)
        mono_r_out.link(stereo.right)

        stereo.setDefaultProfilePreset(dai.node.StereoDepth.PresetMode.DENSITY)
        stereo.initialConfig.setMedianFilter(
            dai.StereoDepthConfig.MedianFilter.KERNEL_7x7
        )
        stereo.setLeftRightCheck(True)
        stereo.setSubpixel(False)
        stereo.setOutputSize(640, 400)
        # CAM_A = RGB camera in v3.9.0 (replaces deprecated CameraBoardSocket.RGB)
        stereo.setDepthAlign(dai.CameraBoardSocket.CAM_A)

        # Start pipeline — v3 uses pipeline.start() not 'with Device' block
        _dep_queue = stereo.depth.createOutputQueue(maxSize=2, blocking=False)
        _pipeline.start()

        # v3.9.0 — no XLinkOut, use createOutputQueue directly on stereo.depth
       
        print("[OAK] Depth ready — aligned to RGB FOV ✓")
        print("[OAK] Pipeline running ✓")


# ═════════════════════════════════════════════════════════════
#  WEBRTC VIDEO TRACKS
# ═════════════════════════════════════════════════════════════

class OakRgbTrack(VideoStreamTrack):
    """WebRTC track serving OAK-D RGB frames."""
    kind = "video"

    def __init__(self, queue):
        super().__init__()
        self._queue     = queue
        self._pts       = 0
        self._tb        = fractions.Fraction(1, FPS)
        self._frame_dur = 1.0 / FPS
        self._next_ts   = time.monotonic()
        self._count     = 0
        self._black     = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)

    async def recv(self):
        self._count += 1
        if self._count % (FPS * 5) == 0:
            print(f"[RGB] Sent {self._count} frames "
                  f"({self._count // (FPS * 5) * 5}s elapsed)")

        bgr = await asyncio.get_event_loop().run_in_executor(
            None, self._get_latest
        )

        ts = time.strftime("%H:%M:%S.") + f"{int(time.time() * 1000) % 1000:03d}"
        cv2.putText(bgr, f"RGB {ts}", (8, 32),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.75, (0, 255, 0), 2)

        vf = VideoFrame.from_ndarray(bgr, format="bgr24")
        vf.pts = self._pts
        vf.time_base = self._tb
        self._pts += 1

        now = time.monotonic()
        wait = self._next_ts - now
        if wait > 0:
            await asyncio.sleep(wait)
        self._next_ts = max(now, self._next_ts) + self._frame_dur

        return vf

    def _get_latest(self):
        latest = None
        for _ in range(8):
            pkt = self._queue.tryGet()
            if pkt is not None:
                latest = pkt

        if latest is None:
            return self._black.copy()

        bgr = latest.getCvFrame()

        if bgr.shape[:2] != (HEIGHT, WIDTH):
            bgr = cv2.resize(bgr, (WIDTH, HEIGHT))

        return bgr


class OakDepthTrack(VideoStreamTrack):
    """WebRTC track serving colourised depth aligned to RGB FOV."""
    kind = "video"

    def __init__(self, queue):
        super().__init__()
        self._queue     = queue
        self._pts       = 0
        self._tb        = fractions.Fraction(1, FPS)
        self._frame_dur = 1.0 / FPS
        self._next_ts   = time.monotonic()
        self._black     = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)

    async def recv(self):
        colored = await asyncio.get_event_loop().run_in_executor(
            None, self._get_depth
        )

        cv2.putText(colored, "DEPTH (aligned)", (8, 32),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.75, (255, 255, 255), 2)

        vf = VideoFrame.from_ndarray(colored, format="bgr24")
        vf.pts = self._pts
        vf.time_base = self._tb
        self._pts += 1

        now = time.monotonic()
        wait = self._next_ts - now
        if wait > 0:
            await asyncio.sleep(wait)
        self._next_ts = max(now, self._next_ts) + self._frame_dur

        return vf

    def _get_depth(self):
        latest = None
        for _ in range(8):
            pkt = self._queue.tryGet()
            if pkt is not None:
                latest = pkt

        if latest is None:
            return self._black.copy()

        depth_mm = latest.getFrame()
        depth_mm = np.clip(depth_mm, DEPTH_MIN_MM, DEPTH_MAX_MM)

        norm = (
            (DEPTH_MAX_MM - depth_mm.astype(np.float32)) /
            (DEPTH_MAX_MM - DEPTH_MIN_MM) * 255.0
        ).astype(np.uint8)

        colored = cv2.applyColorMap(norm, DEPTH_COLORMAP)

        if colored.shape[:2] != (HEIGHT, WIDTH):
            colored = cv2.resize(colored, (WIDTH, HEIGHT))

        return colored


# ═════════════════════════════════════════════════════════════
#  HTTP SIGNALLING
# ═════════════════════════════════════════════════════════════

async def handle_offer(request):
    print(f"[Signal] GET /offer from {request.remote}")
    start_oak()

    pc = RTCPeerConnection()
    _pcs.add(pc)

    @pc.on("connectionstatechange")
    async def on_state():
        state = pc.connectionState
        print(f"[WebRTC] State → {state}")
        if state in ("failed", "closed"):
            await pc.close()
            _pcs.discard(pc)

    pc.addTrack(OakRgbTrack(_rgb_queue))
    pc.addTrack(OakDepthTrack(_dep_queue))
    print("[WebRTC] RGB + aligned Depth tracks added")

    offer = await pc.createOffer()
    await pc.setLocalDescription(offer)

    return web.json_response({
        "sdp":  pc.localDescription.sdp,
        "type": pc.localDescription.type,
    })


async def handle_answer(request):
    data = await request.json()
    print(f"[Signal] POST /answer from {request.remote}")

    if not _pcs:
        return web.json_response({"error": "no peer connection"}, status=400)

    pc   = next(iter(_pcs))
    desc = RTCSessionDescription(sdp=data["sdp"], type=data["type"])
    await pc.setRemoteDescription(desc)
    print("[WebRTC] Handshake complete — streaming RGB + Depth ✓")

    return web.json_response({"status": "ok"})


async def handle_shutdown(app):
    print("[Server] Shutting down...")
    await asyncio.gather(*[pc.close() for pc in list(_pcs)],
                         return_exceptions=True)
    _pcs.clear()
    global _pipeline
    if _pipeline is not None:
        try:
            _pipeline.stop()
            print("[OAK] Pipeline stopped ✓")
        except Exception as e:
            print(f"[OAK] Stop error (non-fatal): {e}")


# ═════════════════════════════════════════════════════════════
#  ENTRY POINT
# ═════════════════════════════════════════════════════════════

if __name__ == "__main__":
    app = web.Application()
    app.on_shutdown.append(handle_shutdown)
    app.router.add_get("/offer",   handle_offer)
    app.router.add_post("/answer", handle_answer)

    print("=" * 55)
    print(f"[Server] Pi WebRTC — DepthAI {dai.__version__}")
    print(f"[Server] Listening on {HOST_IP}:{PORT}")
    print(f"[Server] Streams: RGB {WIDTH}x{HEIGHT}@{FPS}fps + Depth (aligned)")
    print(f"[Server] Unity → GET  http://<PI_IP>:{PORT}/offer")
    print(f"[Server] Unity → POST http://<PI_IP>:{PORT}/answer")
    print("=" * 55)

    web.run_app(app, host=HOST_IP, port=PORT, print=None)