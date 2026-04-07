using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Attached to the XR camera (XR Origin > Camera Offset > Main Camera).
///
/// Every ReportInterval seconds, sends this client's head position and gaze
/// direction to the server so GazeLogger can log accurate per-player snapshots.
///
/// Does NOT extend NetworkBehaviour because the scene camera is not a
/// network-spawned object — ownership checks would always fail on Quest clients.
/// Instead it uses NetworkManager directly, which is always available once
/// the client has connected.
/// </summary>
public class PlayerHeadReporter : MonoBehaviour
{
    [Tooltip("Should match GazeLogger.SnapshotInterval — default 5 seconds.")]
    public float ReportInterval = 5f;

    private float _timer = 0f;
    private Camera _camera;

    void Awake()
    {
        _camera = GetComponent<Camera>();
        if (_camera == null)
            _camera = GetComponentInChildren<Camera>();
    }

    void Update()
    {
        if (NetworkManager.Singleton == null) { Debug.Log("[PHR] NM null"); return; }
        if (!NetworkManager.Singleton.IsClient) { Debug.Log("[PHR] not client"); return; }
        if (!NetworkManager.Singleton.IsConnectedClient) { Debug.Log("[PHR] not connected"); return; }
        if (_camera == null) { Debug.Log("[PHR] camera null"); return; }

        _timer += Time.deltaTime;
        if (_timer >= ReportInterval)
        {
            _timer = 0f;
            ReportHeadState();
        }
    }

    private void ReportHeadState()
    {
        if (GazeLogger.Instance == null)
        {
            Debug.LogWarning("[PlayerHeadReporter] GazeLogger.Instance is null — not yet spawned.");
            return;
        }

        Vector3 position = _camera.transform.position;
        Vector3 forward = _camera.transform.forward;
        ulong clientId = NetworkManager.Singleton.LocalClientId;

        GazeLogger.Instance.ReportHeadStateServerRpc(position, forward, clientId);

        Debug.Log($"[PlayerHeadReporter] clientId={clientId} pos={position} fwd={forward}");
    }
}