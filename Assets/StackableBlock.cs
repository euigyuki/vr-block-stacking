using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using Unity.Netcode;

public class StackableBlock : NetworkBehaviour
{
    public static System.Action<StackableBlock, bool> OnStackedStateChanged;

    [Header("Detection")]
    public LayerMask blockLayer;
    public float castExtraDistance = 0.02f;
    public float boxShrink = 0.02f;

    [Header("Stability")]
    public float settleVelocity = 0.15f;
    public float checkInterval = 0.1f;

    public bool IsStacked { get; private set; }

    // Authoritative position/rotation. Only the server writes these.
    // Clients read them to display the block in the correct position.
    private NetworkVariable<Vector3> _networkPosition = new NetworkVariable<Vector3>(
        Vector3.zero,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<Quaternion> _networkRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private NetworkVariable<bool> _isBeingGrabbed = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    Rigidbody rb;
    BoxCollider box;
    XRGrabInteractable grab;
    float timer;

    // True only on the client that is currently holding this block locally.
    private bool _isLocallyHeld = false;

    // How often the grabbing client sends its position to the server (~30 Hz).
    private const float PositionSendInterval = 0.033f;
    private float _positionSendTimer = 0f;

    // Tracks which block each player is currently holding.
    // Prevents a player from grabbing two blocks simultaneously.
    // Key: clientId, Value: the block being held.
    private static Dictionary<ulong, StackableBlock> _heldByPlayer
        = new Dictionary<ulong, StackableBlock>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        box = GetComponent<BoxCollider>();
        grab = GetComponent<XRGrabInteractable>();
    }

    void OnEnable()
    {
        if (grab != null)
        {
            grab.selectEntered.AddListener(OnGrabbed);
            grab.selectExited.AddListener(OnReleased);
        }
    }

    void OnDisable()
    {
        if (grab != null)
        {
            grab.selectEntered.RemoveListener(OnGrabbed);
            grab.selectExited.RemoveListener(OnReleased);
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Server runs full physics simulation.
            rb.isKinematic = false;
        }
        else
        {
            // Clients do not simulate physics — they only mirror server position.
            // This prevents the two Rigidbodies from producing different results
            // and fighting each other through NetworkVariable updates.
            rb.isKinematic = true;

            _networkPosition.OnValueChanged += OnPositionChanged;
            _networkRotation.OnValueChanged += OnRotationChanged;

            // Apply initial state immediately on late join.
            if (_networkPosition.Value != Vector3.zero)
            {
                transform.position = _networkPosition.Value;
                transform.rotation = _networkRotation.Value;
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer)
        {
            _networkPosition.OnValueChanged -= OnPositionChanged;
            _networkRotation.OnValueChanged -= OnRotationChanged;
        }
    }

    // Called on clients when the server broadcasts a new position.
    private void OnPositionChanged(Vector3 oldPos, Vector3 newPos)
    {
        // While this client is holding the block, local hand tracking drives
        // position — do not override it with stale server data.
        if (_isLocallyHeld) return;
        transform.position = newPos;
    }

    private void OnRotationChanged(Quaternion oldRot, Quaternion newRot)
    {
        if (_isLocallyHeld) return;
        transform.rotation = newRot;
    }

    void Update()
    {
        if (_isLocallyHeld)
        {
            // Grabbing client pushes its position to the server at ~30 Hz.
            _positionSendTimer += Time.deltaTime;
            if (_positionSendTimer >= PositionSendInterval)
            {
                _positionSendTimer = 0f;
                UpdateHeldPositionServerRpc(transform.position, transform.rotation);
            }
            return;
        }

        // Server broadcasts physics-simulated position to all clients.
        if (IsServer)
        {
            _networkPosition.Value = transform.position;
            _networkRotation.Value = transform.rotation;

            // Stacking evaluation runs only on server.
            timer += Time.deltaTime;
            if (timer >= checkInterval)
            {
                timer = 0f;
                EvaluateStacked();
            }
        }
    }

    /// <summary>
    /// Sent by the grabbing client at ~30 Hz.
    /// Server applies the position so physics and all observers stay in sync.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void UpdateHeldPositionServerRpc(Vector3 position, Quaternion rotation)
    {
        // While held, keep the server-side Rigidbody kinematic so physics
        // does not fight the incoming hand position.
        rb.isKinematic = true;
        transform.position = position;
        transform.rotation = rotation;
        _networkPosition.Value = position;
        _networkRotation.Value = rotation;
    }

    float lastGrabTime;
    const float grabDebounceTime = 0.05f;

    void OnGrabbed(SelectEnterEventArgs args)
    {
        if (Time.time - lastGrabTime < grabDebounceTime) return;
        lastGrabTime = Time.time;

        ulong localId = NetworkManager.Singleton.LocalClientId;

        // Prevent grabbing a second block while already holding one.
        if (_heldByPlayer.TryGetValue(localId, out StackableBlock alreadyHeld))
        {
            if (alreadyHeld != this)
            {
                // Force-cancel this grab attempt.
                grab.interactionManager.CancelInteractableSelection(
                    (IXRSelectInteractable)grab);
                Debug.Log("[StackableBlock] Grab blocked: player already holding "
                    + alreadyHeld.gameObject.name);
                return;
            }
        }

        _heldByPlayer[localId] = this;
        _isLocallyHeld = true;
        _positionSendTimer = 0f;
        SetStacked(false);

        if (!NetworkObject.IsSpawned)
        {
            Debug.LogWarning("[StackableBlock] NetworkObject not spawned yet, skipping RPC.");
            return;
        }

        if (!NetworkObject.IsOwner)
            RequestOwnershipServerRpc(localId);

        NotifyGrabbedServerRpc(localId);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        ulong localId = NetworkManager.Singleton.LocalClientId;

        if (_heldByPlayer.TryGetValue(localId, out StackableBlock held) && held == this)
            _heldByPlayer.Remove(localId);

        _isLocallyHeld = false;

        if (!NetworkObject.IsSpawned)
        {
            Debug.LogWarning("[StackableBlock] NetworkObject not spawned yet, skipping RPC.");
            return;
        }

        NotifyReleasedServerRpc(localId, transform.position);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestOwnershipServerRpc(ulong clientId)
    {
        NetworkObject.ChangeOwnership(clientId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NotifyGrabbedServerRpc(ulong playerId)
    {
        _isBeingGrabbed.Value = true;

        // Re-enable kinematic on server side while block is held.
        // UpdateHeldPositionServerRpc will keep it positioned correctly.
        rb.isKinematic = true;

        Debug.Log($"[LOG] Player {playerId} grabbed {gameObject.name} at time {Time.time}");
        if (InteractionLogger.Instance != null)
        {
            int score = StackingArea.Instance != null ? StackingArea.Instance.CurrentScore : 0;
            InteractionLogger.Instance.LogEvent(
                playerId, gameObject.name, "grabbed", transform.position, score);
        }
        else
            Debug.LogWarning("[LOG] InteractionLogger.Instance is null!");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NotifyReleasedServerRpc(ulong playerId, Vector3 position)
    {
        _isBeingGrabbed.Value = false;

        // Re-enable physics on the server so the block falls naturally.
        rb.isKinematic = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        Debug.Log($"[LOG] Player {playerId} released {gameObject.name} " +
                  $"at position {position} at time {Time.time}");

        if (InteractionLogger.Instance != null)
            StartCoroutine(LogReleasedDelayed(playerId, position));
        else
            Debug.LogWarning("[LOG] InteractionLogger.Instance is null!");
    }

    IEnumerator LogReleasedDelayed(ulong playerId, Vector3 position)
    {
        yield return new WaitForSeconds(0.2f);
        int score = StackingArea.Instance != null ? StackingArea.Instance.CurrentScore : 0;
        InteractionLogger.Instance.LogEvent(
            playerId, gameObject.name, "released", position, score);
    }

    void EvaluateStacked()
    {
        if (rb == null || box == null) return;
        if (rb.linearVelocity.magnitude > settleVelocity)
        {
            SetStacked(false);
            return;
        }

        Bounds b = box.bounds;
        Vector3 center = b.center;
        Vector3 halfExtents = b.extents - new Vector3(boxShrink, boxShrink, boxShrink);
        float castDistance = b.extents.y + castExtraDistance;

        bool hitSomethingBelow = Physics.BoxCast(
            center, halfExtents, Vector3.down,
            out RaycastHit hit, transform.rotation,
            castDistance, blockLayer, QueryTriggerInteraction.Ignore
        );

        if (!hitSomethingBelow) { SetStacked(false); return; }
        if (hit.collider != null &&
            hit.collider.gameObject == gameObject) { SetStacked(false); return; }

        SetStacked(true);
    }

    void SetStacked(bool value)
    {
        if (IsStacked == value) return;
        IsStacked = value;
        OnStackedStateChanged?.Invoke(this, IsStacked);
    }
}