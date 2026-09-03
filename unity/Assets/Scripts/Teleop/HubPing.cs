using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Teleop
{
    public class HubPing : MonoBehaviour
    {
        public string hubUrl = "http://192.168.139.138:8080";

        private IEnumerator Start()
        {
            Debug.Log("[HubPing] Testing HTTP connection to hub...");

            using var req = UnityWebRequest.Get(hubUrl);
            req.timeout = 5;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
                Debug.Log("[HubPing] HTTP OK — hub is reachable");
            else
                Debug.LogError($"[HubPing] HTTP FAILED: {req.error}");
        }
    }
}
