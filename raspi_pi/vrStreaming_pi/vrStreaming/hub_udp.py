"""
pi_h264_to_hub.py
DepthAI → raw H.264 → GStreamer (h264parse + mpegtsmux) → UDP
"""

import depthai as dai
import subprocess
import time

HUB_IP   = "100.65.15.35"
HUB_PORT = 5000
WIDTH    = 640
HEIGHT   = 480
FPS      = 20
BITRATE  = 2_000_000

pipeline = dai.Pipeline()

cam = pipeline.create(dai.node.Camera).build()
cam_out = cam.requestOutput(
    size=(WIDTH, HEIGHT),
    type=dai.ImgFrame.Type.NV12,
    fps=FPS
)

videoEnc = pipeline.create(dai.node.VideoEncoder).build(
    cam_out,
    frameRate=FPS,
    profile=dai.VideoEncoderProperties.Profile.H264_BASELINE  # no B-frames
)
videoEnc.setBitrate(BITRATE)
videoEnc.setKeyframeFrequency(FPS)      # keyframe every second
videoEnc.setNumBFrames(0)               # explicitly disable B-frames

q = videoEnc.bitstream.createOutputQueue(maxSize=2, blocking=False)

gst_cmd = [
    "gst-launch-1.0", "-q",
    "fdsrc", "fd=0", "!",
    "h264parse", "config-interval=1", "!",
    "mpegtsmux", "alignment=7", "!",
    "udpsink", f"host={HUB_IP}", f"port={HUB_PORT}",
    "sync=false", "async=false"
]

print(f"Starting DepthAI → MPEG-TS → {HUB_IP}:{HUB_PORT}")
gst = subprocess.Popen(gst_cmd, stdin=subprocess.PIPE)
time.sleep(0.5)

with pipeline:
    pipeline.start()
    print("Pipeline started")

    try:
        while pipeline.isRunning():
            pkt = q.tryGet()
            if pkt is not None:
                data = bytes(pkt.getData())
                gst.stdin.write(data)
                gst.stdin.flush()
            else:
                time.sleep(0.001)
    except KeyboardInterrupt:
        pass
    finally:
        gst.stdin.close()
        gst.terminate()
        gst.wait()
