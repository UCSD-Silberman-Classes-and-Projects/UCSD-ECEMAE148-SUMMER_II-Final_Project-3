using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

namespace Teleop
{
    [DisallowMultipleComponent]
    public class HubClient : MonoBehaviour
    {
        [Header("Hub")]
        public string hubUrl = "http://192.168.139.138:8080";
        public float reconnectDelay = 2f;

        [Header("Link")]
        [Range(2f, 20f)]
        public float heartbeatHz = 5f;

        [Header("Wiring")]
        public VideoSurface surface;

        [Header("Status (read only)")]
        public bool connected;
        public string lastError = "";
        public int telemetryReceived;

        public WorldBuffer World { get; } = new WorldBuffer(32);
        public WorldSnapshot Latest => World.Newest;
        public event Action<WorldSnapshot> OnWorld;

        public bool LinkOpen => _link != null &&
                                _link.ReadyState == RTCDataChannelState.Open;

        public void SelectTarget(int trackId) => Send(SelectMsg.Json(trackId));
        public void ClearSelection() => Send(SelectMsg.Json(-1));
        public void RequestMode(string mode) => Send(ModeMsg.Json(mode));
        public void Stop() => Send(ModeMsg.Json("stop"));

        private RTCPeerConnection _pc;
        private RTCDataChannel _link;
        private readonly ConcurrentQueue<string> _inbox = new();
        private Coroutine _heartbeat;
        private Coroutine _webRtcUpdate;

        // ─────────────────────────────────────────────────────
        //  LIFECYCLE
        // ─────────────────────────────────────────────────────

        
        private void OnEnable()
        {
            StartCoroutine(DelayedStart());
        }

        private IEnumerator DelayedStart()
        {
            // Wait two frames before initializing WebRTC so Unity
            // finishes its first frame and doesn't block the Editor
            yield return null;
            yield return null;

            _webRtcUpdate = StartCoroutine(WebRTC.Update());

            yield return null; // give WebRTC one frame to initialize

            StartCoroutine(ConnectLoop());
        }

        private void OnDisable()
        {
            if (_webRtcUpdate != null)
            {
                StopCoroutine(_webRtcUpdate);
                _webRtcUpdate = null;
            }
            Teardown();
        }

        private void Update()
        {
            while (_inbox.TryDequeue(out var json))
            {
                WorldSnapshot snap;
                try { snap = JsonUtility.FromJson<WorldSnapshot>(json); }
                catch (Exception e) { lastError = "parse: " + e.Message; continue; }

                if (snap == null || snap.type != "world") continue;

                // Don't let hub's -1 clear a local selection that hasn't been
                // confirmed yet — only clear when hub explicitly drops it
                var current = World.Newest;
                if (snap.selected_id < 0 && current != null && current.selected_id >= 0)
                    snap.selected_id = current.selected_id;

                World.Push(snap);
                telemetryReceived++;
                if (surface != null) surface.SetFrameSizeHint(snap.frame_w, snap.frame_h);
                OnWorld?.Invoke(snap);
            }
        }

        // ─────────────────────────────────────────────────────
        //  CONNECTION
        // ─────────────────────────────────────────────────────

        private IEnumerator ConnectLoop()
        {
            while (enabled)
            {
                if (!connected)
                {
                    yield return Negotiate();
                    if (!connected)
                        yield return new WaitForSeconds(reconnectDelay);
                }
                yield return null;
            }
        }

        private IEnumerator Negotiate()
        {
            Teardown();

            var config = new RTCConfiguration
            {
                iceServers = new RTCIceServer[] { }
            };

            _pc = new RTCPeerConnection(ref config);

            _pc.OnIceConnectionChange = state =>
            {
                Debug.Log($"[HubClient] ICE → {state}");
                if (state == RTCIceConnectionState.Failed ||
                    state == RTCIceConnectionState.Disconnected ||
                    state == RTCIceConnectionState.Closed)
                    connected = false;
            };

            _pc.OnConnectionStateChange = state =>
                Debug.Log($"[HubClient] Peer → {state}");

            _pc.OnTrack = e =>
            {
                Debug.Log($"[HubClient] OnTrack → {e.Track.Kind}");
                if (e.Track is VideoStreamTrack video)
                {
                    video.Enabled = true;

                    if (surface != null)
                        surface.SetVideoTrack(video);  // ← pass track, not texture
                }
            };

            _pc.AddTransceiver(TrackKind.Video, new RTCRtpTransceiverInit
            {
                direction = RTCRtpTransceiverDirection.RecvOnly
            });

            _link = _pc.CreateDataChannel("link",
                        new RTCDataChannelInit { ordered = true });

            _link.OnOpen = () =>
            {
                Debug.Log("[HubClient] data channel open");
                connected = true;
                lastError = "";
                if (_heartbeat == null)
                    _heartbeat = StartCoroutine(Heartbeat());
            };
            _link.OnClose = () => { connected = false; };
            _link.OnMessage = bytes =>
                _inbox.Enqueue(Encoding.UTF8.GetString(bytes));

            // ── Create offer ─────────────────────────────────
            var offerOp = _pc.CreateOffer();
            yield return offerOp;
            if (offerOp.IsError)
            {
                lastError = "createOffer: " + offerOp.Error.message;
                Debug.LogError($"[HubClient] {lastError}");
                yield break;
            }

            var offer = offerOp.Desc;
            var setLocal = _pc.SetLocalDescription(ref offer);
            yield return setLocal;
            if (setLocal.IsError)
            {
                lastError = "setLocal: " + setLocal.Error.message;
                Debug.LogError($"[HubClient] {lastError}");
                yield break;
            }

            // ── Wait for ICE gathering ────────────────────────
            float deadline = Time.realtimeSinceStartup + 5f;
            while (_pc.GatheringState != RTCIceGatheringState.Complete &&
                   Time.realtimeSinceStartup < deadline)
                yield return null;

            Debug.Log("[HubClient] ICE gathering complete, posting offer");

            // ── POST offer → receive answer ───────────────────
            var body = JsonUtility.ToJson(new SdpPayload
            {
                sdp = _pc.LocalDescription.sdp,
                type = "offer"
            });

            using var req = new UnityWebRequest($"{hubUrl}/offer", "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 10
            };
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                lastError = "signaling: " + req.error;
                Debug.LogError($"[HubClient] {lastError}");
                yield break;
            }

            Debug.Log("[HubClient] Answer received from hub");

            var reply = JsonUtility.FromJson<SdpPayload>(req.downloadHandler.text);
            var answer = new RTCSessionDescription
            {
                type = RTCSdpType.Answer,
                sdp = reply.sdp
            };

            var setRemote = _pc.SetRemoteDescription(ref answer);
            yield return setRemote;
            if (setRemote.IsError)
            {
                lastError = "setRemote: " + setRemote.Error.message;
                Debug.LogError($"[HubClient] {lastError}");
            }
            else
            {
                Debug.Log("[HubClient] Connected — waiting for video frames");
            }
        }

        // ─────────────────────────────────────────────────────
        //  HEARTBEAT
        // ─────────────────────────────────────────────────────

        private IEnumerator Heartbeat()
        {
            var wait = new WaitForSeconds(1f / Mathf.Max(1f, heartbeatHz));
            while (true)
            {
                if (LinkOpen) Send(HeartbeatMsg.Json());
                yield return wait;
            }
        }

        private void Send(string json)
        {
            if (!LinkOpen) return;
            try { _link.Send(json); }
            catch (Exception e) { lastError = "send: " + e.Message; }
        }

        // ─────────────────────────────────────────────────────
        //  TEARDOWN
        // ─────────────────────────────────────────────────────

        private void Teardown()
        {
            if (_heartbeat != null) { StopCoroutine(_heartbeat); _heartbeat = null; }
            if (_link != null) { _link.Close(); _link = null; }
            if (_pc != null) { _pc.Close(); _pc.Dispose(); _pc = null; }
            connected = false;
            World.Clear();
        }

        // ─────────────────────────────────────────────────────
        //  JSON
        // ─────────────────────────────────────────────────────

        [Serializable]
        private struct SdpPayload
        {
            public string sdp;
            public string type;
        }
    }
}