//using System.Collections;
//using System.Text;
//using Unity.WebRTC;
//using UnityEngine;
//using UnityEngine.Networking;

//public class StreamReceiver : MonoBehaviour
//{
//    [Header("Eye Displays")]
//    public MeshRenderer leftEyeDisplay;
//    public MeshRenderer rightEyeDisplay;

//    [Header("Raspberry Pi")]
//    public string piIpAddress = "192.168.137.1";
//    public int piPort = 8080;

//    [Header("Options")]
//    public bool flipVertical = true;
//    public bool forceApplyEveryFrame = true;

//    private RTCPeerConnection pc;
//    private VideoStreamTrack videoTrack;
//    private Texture currentTexture;

//    void Awake()
//    {
//        StartCoroutine(WebRTC.Update());
//    }

//    void Start()
//    {
//        StartCoroutine(Connect());
//    }

//    void Update()
//    {
//        // Most reliable way to keep the texture on the material
//        if (forceApplyEveryFrame && videoTrack != null && videoTrack.Texture != null)
//        {
//            Apply(videoTrack.Texture);
//        }
//    }

//    IEnumerator Connect()
//    {
//        var config = new RTCConfiguration
//        {
//            // Empty = use host candidates only (best for same local network)
//            iceServers = new RTCIceServer[] { }
//        };

//        pc = new RTCPeerConnection(ref config);

//        pc.OnIceConnectionChange = state =>
//            Debug.Log($"[Unity] ICE → {state}");

//        pc.OnConnectionStateChange = state =>
//            Debug.Log($"[Unity] Peer → {state}");

//        pc.OnIceGatheringStateChange = state =>
//            Debug.Log($"[Unity] Gathering → {state}");

//        pc.OnTrack = e =>
//        {
//            Debug.Log($"[Unity] OnTrack → {e.Track.Kind}  id={e.Track.Id}");

//            if (e.Track is VideoStreamTrack track)
//            {
//                videoTrack = track;
//                videoTrack.Enabled = true;

//                videoTrack.OnVideoReceived += tex =>
//                {
//                    Debug.Log($"[Unity] OnVideoReceived → {tex.width}x{tex.height}");
//                    Apply(tex);
//                };

//                // Try immediately if texture already exists
//                if (videoTrack.Texture != null)
//                    Apply(videoTrack.Texture);
//            }
//        };

//        // ---------- 1. GET Offer ----------
//        string offerUrl = $"http://{piIpAddress}:{piPort}/offer";
//        Debug.Log($"[Unity] Requesting offer → {offerUrl}");

//        using (var req = UnityWebRequest.Get(offerUrl))
//        {
//            req.timeout = 12;
//            yield return req.SendWebRequest();

//            if (req.result != UnityWebRequest.Result.Success)
//            {
//                Debug.LogError($"[Unity] Offer failed: {req.error}");
//                yield break;
//            }

//            var data = JsonUtility.FromJson<SdpData>(req.downloadHandler.text);

//            var remoteDesc = new RTCSessionDescription
//            {
//                type = RTCSdpType.Offer,
//                sdp = data.sdp
//            };

//            var setRemote = pc.SetRemoteDescription(ref remoteDesc);
//            yield return setRemote;

//            if (setRemote.IsError)
//            {
//                Debug.LogError($"[Unity] SetRemoteDescription failed: {setRemote.Error.message}");
//                yield break;
//            }
//        }

//        // ---------- 2. Create Answer ----------
//        var answerOp = pc.CreateAnswer();
//        yield return answerOp;

//        if (answerOp.IsError)
//        {
//            Debug.LogError($"[Unity] CreateAnswer failed: {answerOp.Error.message}");
//            yield break;
//        }

//        var localDesc = answerOp.Desc;
//        var setLocal = pc.SetLocalDescription(ref localDesc);
//        yield return setLocal;

//        if (setLocal.IsError)
//        {
//            Debug.LogError($"[Unity] SetLocalDescription failed: {setLocal.Error.message}");
//            yield break;
//        }

//        // Wait for ICE gathering
//        float timeout = 5f;
//        while (pc.GatheringState != RTCIceGatheringState.Complete && timeout > 0f)
//        {
//            timeout -= Time.deltaTime;
//            yield return null;
//        }

//        // ---------- 3. POST Answer ----------
//        var answerJson = JsonUtility.ToJson(new SdpData
//        {
//            type = "answer",
//            sdp = pc.LocalDescription.sdp
//        });

//        using (var post = new UnityWebRequest($"http://{piIpAddress}:{piPort}/answer", "POST"))
//        {
//            byte[] body = Encoding.UTF8.GetBytes(answerJson);
//            post.uploadHandler = new UploadHandlerRaw(body);
//            post.downloadHandler = new DownloadHandlerBuffer();
//            post.SetRequestHeader("Content-Type", "application/json");
//            post.timeout = 12;

//            yield return post.SendWebRequest();

//            if (post.result != UnityWebRequest.Result.Success)
//                Debug.LogError($"[Unity] POST answer failed: {post.error}");
//            else
//                Debug.Log("[Unity] Answer posted – waiting for media…");
//        }
//    }

//    // ──────────────────────────────────────────────
//    // Texture application (URP Unlit friendly)
//    // ──────────────────────────────────────────────

//    void Apply(Texture tex)
//    {
//        if (tex == null) return;

//        currentTexture = tex;
//        ApplyTo(leftEyeDisplay, tex);
//        ApplyTo(rightEyeDisplay, tex);
//    }

//    void ApplyTo(MeshRenderer rend, Texture tex)
//    {
//        if (rend == null || tex == null) return;

//        // Always work with a material instance
//        if (rend.sharedMaterial != null)
//            rend.material = new Material(rend.sharedMaterial);

//        Material mat = rend.material;

//        // === URP Unlit uses _BaseMap ===
//        if (mat.HasProperty("_BaseMap"))
//        {
//            mat.SetTexture("_BaseMap", tex);

//            if (flipVertical)
//            {
//                mat.SetTextureScale("_BaseMap", new Vector2(1f, -1f));
//                mat.SetTextureOffset("_BaseMap", new Vector2(0f, 1f));
//            }
//            else
//            {
//                mat.SetTextureScale("_BaseMap", Vector2.one);
//                mat.SetTextureOffset("_BaseMap", Vector2.zero);
//            }
//        }

//        // Classic properties (fallback)
//        mat.mainTexture = tex;
//        if (mat.HasProperty("_MainTex"))
//        {
//            mat.SetTexture("_MainTex", tex);
//            if (flipVertical)
//            {
//                mat.SetTextureScale("_MainTex", new Vector2(1f, -1f));
//                mat.SetTextureOffset("_MainTex", new Vector2(0f, 1f));
//            }
//        }

//        // Force white tint so the image is not darkened
//        if (mat.HasProperty("_BaseColor"))
//            mat.SetColor("_BaseColor", Color.white);
//        if (mat.HasProperty("_Color"))
//            mat.SetColor("_Color", Color.white);
//    }

//    void OnDestroy()
//    {
//        if (videoTrack != null)
//        {
//            videoTrack.Dispose();
//            videoTrack = null;
//        }

//        if (pc != null)
//        {
//            pc.Close();
//            pc.Dispose();
//            pc = null;
//        }
//    }

//    [System.Serializable]
//    private class SdpData
//    {
//        public string type;
//        public string sdp;
//    }
//}

// ABOVE CODE IS WORKING, new depth plus RGB code needs to be functional below 

/*
=============================================================
  StreamReceiver_final.cs  —  Unity WebRTC Video Receiver
  Unity 2022.3 LTS  |  com.unity.webrtc@3.0.0-pre.7
  Meta Quest 3  |  OpenXR  |  URP
=============================================================

ARCHITECTURE:
  Pi HTTP server (aiohttp)
    GET  /offer  → returns SDP offer JSON
    POST /answer → accepts SDP answer JSON, stream begins

  Unity (this script):
    1. GET /offer  from Pi
    2. SetRemoteDescription (Pi's offer)
    3. CreateAnswer
    4. SetLocalDescription
    5. Wait for ICE gathering
    6. POST /answer to Pi
    7. OnTrack fires for each video stream:
         track 0 = RGB  (colour camera)
         track 1 = DEPTH (colourised depth map)

SCENE SETUP:
  Create two quads in the scene:
    - Left Eye Quad  → assign to leftEyeDisplay
    - Right Eye Quad → assign to rightEyeDisplay
  Both display the RGB stream (mono for now).
  Assign depthDisplay to a third quad to see the depth stream.

  Attach this script to an empty GameObject.
  Fill in piIpAddress in the Inspector.

URP MATERIAL NOTE:
  Quads need an Unlit URP material.
  Create: Assets → Create → Material → shader = "Universal Render Pipeline/Unlit"
  This script sets _BaseMap + mainTexture automatically.
=============================================================
*/

using System.Collections;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

public class StreamReceiver : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────
    //  INSPECTOR FIELDS
    // ─────────────────────────────────────────────────────────

    [Header("Pi Server")]
    [Tooltip("IP address of the Raspberry Pi running pi_stream_final.py")]
    public string piIpAddress = "192.168.1.100";  // ← change this to your Pi IP

    [Tooltip("Port matching PORT in pi_stream_final.py")]
    public int piPort = 8080;

    [Header("RGB Display (both eyes for mono VR)")]
    [Tooltip("Quad renderer for left eye — assign in Inspector")]
    public MeshRenderer leftEyeDisplay;

    public MeshRenderer videoQuad;  // single quad on CenterEyeAnchor

    [Tooltip("Quad renderer for right eye — assign in Inspector")]
    public MeshRenderer rightEyeDisplay;

    [Header("Depth Display (optional — for debugging)")]
    [Tooltip("Quad renderer for depth stream — can be null")]
    public MeshRenderer depthDisplay;

    [Header("Options")]
    [Tooltip("Flip texture vertically — needed because WebRTC Y-axis is inverted")]
    public bool flipVertical = false;

    [Tooltip("Reapply texture every Update() frame — fixes some Unity material refresh bugs")]
    public bool forceApplyEveryFrame = true;

    // ─────────────────────────────────────────────────────────
    //  PRIVATE STATE
    // ─────────────────────────────────────────────────────────

    private RTCPeerConnection _pc;          // WebRTC peer connection
    private VideoStreamTrack _rgbTrack;    // track 0: colour camera
    private VideoStreamTrack _depthTrack;  // track 1: depth colourmap
    private int _trackCount = 0;            // counts OnTrack calls (expect 2)

    // ─────────────────────────────────────────────────────────
    //  UNITY LIFECYCLE
    // ─────────────────────────────────────────────────────────

    void Awake()
    {
        // WebRTC.Update() is a coroutine that drives the WebRTC internal loop.
        // Must be started before any RTCPeerConnection is created.
        StartCoroutine(WebRTC.Update());
    }

    void Start()
    {
        Debug.Log($"[Unity] Connecting to Pi at {piIpAddress}:{piPort}");
        StartCoroutine(Connect());
    }

    void Update()
    {
        // Force-apply textures every frame to prevent Unity material cache issues.
        // This is the most reliable way to keep the video visible on URP materials.
        if (!forceApplyEveryFrame) return;

        if (_rgbTrack != null && _rgbTrack.Texture != null)
        {
            // Apply RGB to both eye quads (mono VR — same image both eyes)
            ApplyToRenderer(leftEyeDisplay, _rgbTrack.Texture);
            ApplyToRenderer(rightEyeDisplay, _rgbTrack.Texture);
            ApplyToRenderer(videoQuad, _rgbTrack.Texture);
        }

        if (_depthTrack != null && _depthTrack.Texture != null)
        {
            // Apply depth to debug quad (optional)
            ApplyToRenderer(depthDisplay, _depthTrack.Texture);
        }
    }

    // ─────────────────────────────────────────────────────────
    //  WEBRTC CONNECT COROUTINE
    // ─────────────────────────────────────────────────────────

    IEnumerator Connect()
    {
        // RTCConfiguration with no ICE servers = host-only candidates.
        // This is correct for same-LAN connections — no STUN/TURN needed.
        var config = new RTCConfiguration
        {
            iceServers = new RTCIceServer[] { }
        };

        _pc = new RTCPeerConnection(ref config);

        // ── Connection state logging ─────────────────────────
        _pc.OnIceConnectionChange = state =>
            Debug.Log($"[Unity] ICE state → {state}");

        _pc.OnConnectionStateChange = state =>
            Debug.Log($"[Unity] Peer connection → {state}");

        _pc.OnIceGatheringStateChange = state =>
            Debug.Log($"[Unity] ICE gathering → {state}");

        // ── Track handler ────────────────────────────────────
        // Pi sends TWO tracks: RGB first, then Depth.
        // OnTrack fires once per track received.
        _pc.OnTrack = e =>
        {
            _trackCount++;
            Debug.Log($"[Unity] OnTrack #{_trackCount} → kind={e.Track.Kind}  id={e.Track.Id}");

            if (e.Track is VideoStreamTrack videoTrack)
            {
                videoTrack.Enabled = true;

                if (_trackCount == 1)
                {
                    // First track = RGB colour stream
                    _rgbTrack = videoTrack;
                    Debug.Log("[Unity] RGB track assigned");

                    // OnVideoReceived fires when first frame arrives
                    _rgbTrack.OnVideoReceived += tex =>
                    {
                        Debug.Log($"[Unity] RGB first frame: {tex.width}x{tex.height}");
                        ApplyToRenderer(leftEyeDisplay, tex);
                        ApplyToRenderer(rightEyeDisplay, tex);
                        ApplyToRenderer(videoQuad, tex);
                    };
                }
                else if (_trackCount == 2)
                {
                    // Second track = Depth colourmap stream
                    _depthTrack = videoTrack;
                    Debug.Log("[Unity] Depth track assigned");

                    _depthTrack.OnVideoReceived += tex =>
                    {
                        Debug.Log($"[Unity] Depth first frame: {tex.width}x{tex.height}");
                        ApplyToRenderer(depthDisplay, tex);
                    };
                }
            }
        };

        // ── STEP 1: GET offer from Pi ────────────────────────
        string offerUrl = $"http://{piIpAddress}:{piPort}/offer";
        Debug.Log($"[Unity] GET {offerUrl}");

        using (var req = UnityWebRequest.Get(offerUrl))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Unity] GET /offer failed: {req.error}");
                Debug.LogError("[Unity] Is pi_stream_final.py running on the Pi?");
                yield break;
            }

            Debug.Log("[Unity] Offer received from Pi");

            // Parse the SDP offer JSON
            var offerData = JsonUtility.FromJson<SdpData>(req.downloadHandler.text);

            var remoteDesc = new RTCSessionDescription
            {
                type = RTCSdpType.Offer,
                sdp = offerData.sdp
            };

            // Apply Pi's offer as our remote description
            var setRemote = _pc.SetRemoteDescription(ref remoteDesc);
            yield return setRemote;

            if (setRemote.IsError)
            {
                Debug.LogError($"[Unity] SetRemoteDescription error: {setRemote.Error.message}");
                yield break;
            }
            Debug.Log("[Unity] Remote description set ✓");
        }

        // ── STEP 2: Create Answer ────────────────────────────
        var answerOp = _pc.CreateAnswer();
        yield return answerOp;

        if (answerOp.IsError)
        {
            Debug.LogError($"[Unity] CreateAnswer error: {answerOp.Error.message}");
            yield break;
        }

        // Apply our answer as local description
        var localDesc = answerOp.Desc;
        var setLocal = _pc.SetLocalDescription(ref localDesc);
        yield return setLocal;

        if (setLocal.IsError)
        {
            Debug.LogError($"[Unity] SetLocalDescription error: {setLocal.Error.message}");
            yield break;
        }
        Debug.Log("[Unity] Local description set ✓");

        // ── STEP 3: Wait for ICE gathering ──────────────────
        // ICE gathers local network candidates before we post the answer.
        // Timeout after 5s — on LAN, gathering completes in <1s.
        float timeout = 5f;
        while (_pc.GatheringState != RTCIceGatheringState.Complete && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        if (timeout <= 0f)
            Debug.LogWarning("[Unity] ICE gathering timed out — continuing anyway");
        else
            Debug.Log("[Unity] ICE gathering complete ✓");

        // ── STEP 4: POST answer to Pi ────────────────────────
        string answerUrl = $"http://{piIpAddress}:{piPort}/answer";
        string answerJson = JsonUtility.ToJson(new SdpData
        {
            type = "answer",
            sdp = _pc.LocalDescription.sdp
        });

        Debug.Log($"[Unity] POST {answerUrl}");

        using (var post = new UnityWebRequest(answerUrl, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(answerJson);
            post.uploadHandler = new UploadHandlerRaw(body);
            post.downloadHandler = new DownloadHandlerBuffer();
            post.SetRequestHeader("Content-Type", "application/json");
            post.timeout = 15;

            yield return post.SendWebRequest();

            if (post.result != UnityWebRequest.Result.Success)
                Debug.LogError($"[Unity] POST /answer failed: {post.error}");
            else
                Debug.Log("[Unity] Answer posted — waiting for video frames... ✓");
        }

        // OnTrack will fire after this — no further action needed here.
    }

    // ─────────────────────────────────────────────────────────
    //  TEXTURE APPLICATION
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Apply a texture to a MeshRenderer's material.
    /// Handles both URP (_BaseMap) and legacy (_MainTex) shader properties.
    /// Creates a material instance to avoid modifying shared materials.
    /// </summary>
    void ApplyToRenderer(MeshRenderer rend, Texture tex)
    {
        if (rend == null || tex == null) return;

        // Create a per-instance material copy if we haven't already.
        // This prevents modifying the shared material asset on disk.
        if (rend.sharedMaterial != null && rend.material == rend.sharedMaterial)
            rend.material = new Material(rend.sharedMaterial);

        Material mat = rend.material;

        // ── URP Unlit shader: _BaseMap ───────────────────────
        if (mat.HasProperty("_BaseMap"))
        {
            mat.SetTexture("_BaseMap", tex);

            if (flipVertical)
            {
                // WebRTC frames are Y-flipped relative to Unity UVs
                mat.SetTextureScale("_BaseMap", new Vector2(1f, -1f));
                mat.SetTextureOffset("_BaseMap", new Vector2(0f, 1f));
            }
            else
            {
                mat.SetTextureScale("_BaseMap", Vector2.one);
                mat.SetTextureOffset("_BaseMap", Vector2.zero);
            }
        }

        // ── Legacy / Built-in shader: _MainTex ──────────────
        if (mat.HasProperty("_MainTex"))
        {
            mat.SetTexture("_MainTex", tex);

            if (flipVertical)
            {
                mat.SetTextureScale("_MainTex", new Vector2(1f, -1f));
                mat.SetTextureOffset("_MainTex", new Vector2(0f, 1f));
            }
        }

        // Also set mainTexture as universal fallback
        mat.mainTexture = tex;

        // Force white tint — prevents video looking too dark on some materials
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", Color.white);
    }

    // ─────────────────────────────────────────────────────────
    //  CLEANUP
    // ─────────────────────────────────────────────────────────

    void OnDestroy()
    {
        // Dispose WebRTC resources in correct order
        if (_rgbTrack != null)
        {
            _rgbTrack.Dispose();
            _rgbTrack = null;
        }

        if (_depthTrack != null)
        {
            _depthTrack.Dispose();
            _depthTrack = null;
        }

        if (_pc != null)
        {
            _pc.Close();
            _pc.Dispose();
            _pc = null;
        }

        Debug.Log("[Unity] StreamReceiver destroyed, WebRTC closed");
    }

    // ─────────────────────────────────────────────────────────
    //  JSON HELPER
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Matches the JSON format: {"sdp": "...", "type": "offer/answer"}
    /// Used for both parsing Pi's offer and serialising our answer.
    /// </summary>
    [System.Serializable]
    private class SdpData
    {
        public string type;
        public string sdp;
    }
}