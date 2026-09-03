using UnityEngine;
using UnityEngine.XR;

namespace Teleop
{
    /// <summary>
    /// VR subclass of TargetSelector.
    /// Right controller ray → aim at box → trigger to select.
    /// Left trigger → clear. Either thumbstick click → stop.
    /// </summary>
    public class VRTargetSelector : TargetSelector
    {
        [Header("VR Controller")]
        public OVRInput.Controller controller = OVRInput.Controller.RTouch;

        private void Start()
        {
            // Override rayOrigin to follow the right controller
            var anchor = new GameObject("ControllerRayOrigin");
            anchor.transform.SetParent(transform, false);
            rayOrigin = anchor.transform;
        }

        private void LateUpdate()
        {
            // Keep ray origin synced to controller pose
            if (rayOrigin == null) return;
            rayOrigin.position = OVRInput.GetLocalControllerPosition(controller);
            rayOrigin.rotation = OVRInput.GetLocalControllerRotation(controller);
        }

        protected override bool SelectPressed()
            => OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, controller);

        protected override bool ClearPressed()
            => OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);

        protected override bool StopPressed()
            => OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.All);
    }
}