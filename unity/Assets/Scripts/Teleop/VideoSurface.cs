using Unity.WebRTC;
using UnityEngine;

namespace Teleop
{
    /// <summary>
    /// The video background, and the single source of truth for turning
    /// source-frame pixels into world positions.
    ///
    /// The alignment trick: the video quad and every overlay box are sized
    /// from the same sensorHFovDeg constant and live on the same plane, as
    /// children of this transform. Alignment is therefore a pure 2D mapping
    /// that cannot drift, and it does not depend on Unity's camera FOV at all
    /// -- the headset FOV only changes how much of the surface you can see at
    /// once, never where a box sits on it.
    ///
    /// That inverts the usual advice to "match Unity camera FOV to the OAK-D
    /// FOV exactly". You no longer have to, and more importantly you can no
    /// longer get it subtly wrong. The one number that must be right is
    /// sensorHFovDeg, and it is wrong in exactly one visible way: boxes are
    /// uniformly too large or too small, scaling from the frame centre. That
    /// is a much easier bug to see than a gradual drift toward the edges.
    ///
    /// OAK-D RGB (IMX378) is 69 deg horizontal DFOV-corrected; OAK-D Lite
    /// (IMX214) is close but check your unit's datasheet. If you crop or
    /// letterbox on the Pi, this must be the FOV of the cropped image.
    /// </summary>
    [DisallowMultipleComponent]
    public class VideoSurface : MonoBehaviour
    {
        [Header("Sensor")]
        [Tooltip("Horizontal field of view of the SOURCE image, in degrees. " +
                 "If the Pi crops before encoding, use the cropped FOV.")]
        [Range(20f, 140f)]
        public float sensorHFovDeg = 69f;

        [Header("Placement")]
        [Tooltip("Distance from the operator's head to the video plane, metres. " +
                 "Affects comfort only -- not alignment.")]
        [Range(1f, 20f)]
        public float surfaceDistance = 6f;

        [Tooltip("Follow the headset so the surface stays in front of the operator.")]
        public bool lockToHead = true;

        [Tooltip("How quickly the surface catches up to head yaw. 0 = rigid.")]
        [Range(0f, 20f)]
        public float followSharpness = 4f;

        [Header("Debug")]
        public bool drawFrameBorder = false;

        private Transform _head;
        private MeshRenderer _screen;
        private Material _screenMaterial;
        private LineRenderer _border;
        private VideoStreamTrack _videoTrack;
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");

        public int FrameWidth { get; private set; } = 1280;
        public int FrameHeight { get; private set; } = 720;

        /// <summary>World-space width of the video plane at surfaceDistance.</summary>
        public float SurfaceWidth =>
            2f * surfaceDistance * Mathf.Tan(sensorHFovDeg * 0.5f * Mathf.Deg2Rad);

        public float SurfaceHeight =>
            SurfaceWidth * (FrameHeight / Mathf.Max(1f, (float)FrameWidth));

        // ------------------------------------------------------------------
        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && _videoTrack != null && _screen != null)
            {
                var tex = _videoTrack.Texture;
                if (tex != null)
                {
                    _screen.material.SetTexture(BaseMapId, tex);
                    _screen.material.mainTexture = tex;
                }
            }
        }
        private void Awake()
        {
            _head = Camera.main != null ? Camera.main.transform : null;

            _screen = GetComponent<MeshRenderer>();

            if (_screen == null)
            {
                Debug.LogError("[VideoSurface] No MeshRenderer on this GameObject.");
                return;
            }

            _screen.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _screen.receiveShadows = false;

            // Instance the EXISTING material rather than creating a new one
            // This keeps the shader/properties the Editor already set up
            _screenMaterial = _screen.material; // .material auto-creates an instance
            _screenMaterial.SetColor("_BaseColor", Color.white);
  
        }

        //private void BuildScreen()
        //{
        //    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        //    quad.name = "VideoPlane";
        //    quad.transform.SetParent(transform, false);
        //    quad.transform.localPosition = new Vector3(0f, 0f, surfaceDistance);

        //    // No collider: the video plane must never intercept a selection
        //    // ray, or every click would land on the background instead of a box.
        //    Destroy(quad.GetComponent<Collider>());

        //    _screen = quad.GetComponent<MeshRenderer>();
        //    _screen.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        //    _screen.receiveShadows = false;

        //    // Unlit: this is a camera image, not a lit surface in the scene.
        //    var shader = Shader.Find("Universal Render Pipeline/Unlit");
        //    if (shader == null)
        //        shader = Shader.Find("Unlit/Texture"); // fallback for non-URP
        //    _screenMaterial = new Material(shader);
        //    _screenMaterial.SetColor("_BaseColor", Color.white); // ensure no tint
        //    _screen.material = _screenMaterial;

        //    //_border = new GameObject("FrameBorder").AddComponent<LineRenderer>();
        //    //_border.transform.SetParent(transform, false);
        //    //_border.useWorldSpace = true;
        //    //_border.loop = true;
        //    //_border.positionCount = 4;
        //    //_border.widthMultiplier = 0.01f;
        //    //_border.material = new Material(Shader.Find("Sprites/Default"));
        //    //_border.startColor = _border.endColor = new Color(1f, 1f, 1f, 0.15f);
        //    //_border.enabled = false;
        //}
        public void SetVideoTrack(VideoStreamTrack track)
        {
            _videoTrack = track;

            // Still catch the first frame to get dimensions
            track.OnVideoReceived += tex =>
            {
                if (tex.width > 0 && tex.height > 0)
                {
                    FrameWidth = tex.width;
                    FrameHeight = tex.height;
                }
            };
        }
        private void LateUpdate()
        {
            // Poll WebRTC texture every frame
            // Poll WebRTC texture every frame
            if (_videoTrack != null && _screen != null)
            {
                var tex = _videoTrack.Texture;
                if (tex != null && _screen.material.GetTexture(BaseMapId) != tex)
                {
                    _screen.material.SetTexture(BaseMapId, tex);
                    _screen.material.mainTexture = tex;
                }
            }

            // Rest of your existing LateUpdate code below
            if (lockToHead && _head != null)
            {
                var forward = _head.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 1e-4f)
                {
                    var target = Quaternion.LookRotation(forward.normalized, Vector3.up);
                    transform.rotation = followSharpness <= 0f
                        ? target
                        : Quaternion.Slerp(transform.rotation, target,
                                           1f - Mathf.Exp(-followSharpness * Time.deltaTime));
                }
                transform.position = _head.position +
                                     _head.forward.normalized * surfaceDistance;
            }

            ApplyScale();
        }

        private void ApplyScale()
        {
            if (_screen == null) return;
            transform.localScale = new Vector3(SurfaceWidth, SurfaceHeight, 1f);
        }

        // ------------------------------------------------------------------

        /// <summary>Called by HubClient when a decoded video texture arrives.</summary>
        public void SetTexture(Texture texture)
        {
            if (_screenMaterial == null || texture == null) return;

            _screenMaterial.SetTexture("_BaseMap", texture);
            _screenMaterial.mainTexture = texture;
            _screenMaterial.SetColor("_BaseColor", Color.white);

            if (texture.width > 0 && texture.height > 0)
            {
                FrameWidth = texture.width;
                FrameHeight = texture.height;
            }
        }

        /// <summary>
        /// Telemetry also reports frame size. Prefer the texture when we have
        /// one, but this keeps the projection correct before the first frame
        /// arrives so boxes are never drawn against a guessed aspect ratio.
        /// </summary>
        public void SetFrameSizeHint(int w, int h)
        {
            if (_screenMaterial != null && _screenMaterial.mainTexture != null) return;
            if (w > 0 && h > 0) { FrameWidth = w; FrameHeight = h; }
        }

        /// <summary>
        /// Source pixel (origin top-left, as OpenCV and YOLO report it) to a
        /// world point on the video plane. Everything drawn on top of the
        /// video goes through here.
        /// </summary>
        public Vector3 PixelToWorld(float px, float py)
        {
            float u = px / Mathf.Max(1f, FrameWidth) - 0.5f;   // -0.5 to +0.5
            float v = 0.5f - py / Mathf.Max(1f, FrameHeight);  // flipped
            return transform.TransformPoint(new Vector3(u, v, 0f)); // ← scale already applied
        }

        public Vector2 PixelSizeToWorld(float pw, float ph)
        {
            return new Vector2(
                pw / Mathf.Max(1f, FrameWidth),   // normalized, scale handles the rest
                ph / Mathf.Max(1f, FrameHeight));
        }

        public Vector3 PlaneNormal => -transform.forward;
    }
}
