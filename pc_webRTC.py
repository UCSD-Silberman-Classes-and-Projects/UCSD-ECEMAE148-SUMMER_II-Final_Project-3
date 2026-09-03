import asyncio
from aiohttp import web
from aiortc import RTCPeerConnection, RTCSessionDescription
import cv2
import numpy as np
import threading

pcs = set()
latest_jpeg = None
lock = threading.Lock()

async def offer(request):
    params = await request.json()
    pc = RTCPeerConnection()
    pcs.add(pc)

    @pc.on("track")
    def on_track(track):
        print("Track received:", track.kind)
        if track.kind == "video":
            # get the running loop and create task directly
            loop = asyncio.get_event_loop()
            loop.create_task(process_frames(track))
            print("process_frames task created")

    await pc.setRemoteDescription(RTCSessionDescription(
        sdp=params["sdp"], type=params["type"]
    ))
    answer = await pc.createAnswer()
    await pc.setLocalDescription(answer)

    return web.json_response({
        "sdp": pc.localDescription.sdp,
        "type": pc.localDescription.type
    })

@pc.on("track")
def on_track(track):
    print("Track received:", track.kind)
    print("Track state:", track.readyState)
    print("Track id:", track.id)
    if track.kind == "video":
        loop = asyncio.get_event_loop()
        task = loop.create_task(process_frames(track))
        print("Task created:", task)

async def process_frames(track):
    print("process_frames entered")
    try:
        print("waiting for first frame...")
        frame = await asyncio.wait_for(track.recv(), timeout=10.0)
        print("GOT FIRST FRAME:", frame)
        print("frame size:", frame.width, "x", frame.height)
    except asyncio.TimeoutError:
        print("TIMEOUT — no frame arrived in 10 seconds")
    except Exception as e:
        print("ERROR:", type(e).__name__, e)

# async def process_frames(track):
#     global latest_jpeg
#     frame_count = 0
#     print("process_frames started")  # ← confirm it starts
#     while True:
#         try:
#             frame = await asyncio.wait_for(track.recv(), timeout=5.0)
#             img = frame.to_ndarray(format="bgr24")
            
#             _, jpeg = cv2.imencode('.jpg', img,
#                 [cv2.IMWRITE_JPEG_QUALITY, 85])
            
#             with lock:
#                 latest_jpeg = jpeg.tobytes()
            
#             frame_count += 1
#             if frame_count % 20 == 0:
#                 print(f"Frames received: {frame_count}")
                
#         except asyncio.TimeoutError:
#             print("No frame received in 5 seconds — track stalled")
#             break
#         except Exception as e:
#             print(f"process_frames error: {e}")
#             break
    
#     print("process_frames ended")

async def mjpeg_feed(request):
    response = web.StreamResponse()
    response.headers['Content-Type'] = 'multipart/x-mixed-replace; boundary=frame'
    await response.prepare(request)
    
    while True:
        with lock:
            frame = latest_jpeg
        
        if frame is not None:
            await response.write(
                b'--frame\r\n'
                b'Content-Type: image/jpeg\r\n\r\n' + frame + b'\r\n'
            )
        await asyncio.sleep(0.05)  # 20fps display

async def latency_check(request):
    # returns timestamp for latency measurement
    import time
    return web.json_response({"server_time": time.time()})

async def main():
    app = web.Application()
    app.router.add_post("/offer", offer)
    app.router.add_get("/video", mjpeg_feed)      # ← MJPEG stream
    app.router.add_get("/latency", latency_check)  # ← latency endpoint

    runner = web.AppRunner(app)
    await runner.setup()
    
    site = web.TCPSite(runner, host="0.0.0.0", port=8080)
    await site.start()
    
    print("="*40)
    print("Server running on 0.0.0.0:8080")
    print("View stream at: http://100.65.15.35:8080/video")
    print("Open that URL in any browser on PC")
    print("="*40)

    await asyncio.Future()

if __name__ == "__main__":
    asyncio.run(main())