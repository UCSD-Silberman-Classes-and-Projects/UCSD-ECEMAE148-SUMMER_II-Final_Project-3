// Add this script to OVRCameraRig GameObject
using UnityEngine;
public class EyeLayerSetup : MonoBehaviour
{

    public GameObject leftQuad;
    public GameObject rightQuad;
    public Camera leftCam;   // drag OVRCameraRig → LeftEyeAnchor → LeftEyeCamera
    public Camera rightCam;  // drag OVRCameraRig → RightEyeAnchor → RightEyeCamera

    void Start()
    {
        // get the actual eye cameras from OVRCameraRig
        int leftLayer = LayerMask.NameToLayer("LeftEye");
        int rightLayer = LayerMask.NameToLayer("RightEye");

        leftCam.cullingMask &= ~(1 << rightLayer);
        rightCam.cullingMask &= ~(1 << leftLayer);

        leftQuad.layer = leftLayer;
        rightQuad.layer = rightLayer;
    }
}