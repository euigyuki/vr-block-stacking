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

    [Header("Snapping")]
    [Tooltip("How close (in meters) the block must be to another block to snap onto it.")]
    public float snapRadius = 0.15f;
    [Tooltip("How quickly the block slides into snap position. Higher = faster.")]
    public float snapSpeed = 20f;

    public bool IsStacked { get; private set; }

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

    private bool _isLocallyHeld = false;
    private bool _isSnapping = false;
    private Vector3 _snapTarget;

    private const float PositionSendInterval = 0.033f;
    private float _positionSendTimer = 0f;

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
            // Start kinematic — blocks are frozen until grabbed.
            // We do not use gravity at all in this setup.
            rb.isKinematic = true;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }
        else
        {
            rb.isKinematic = true;
            _networkPosition.OnValueChanged += OnPositionChanged;
            _networkRotation.OnValueChanged += OnRotationChanged;

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

    private void OnPositionChanged(Vector3 oldPos, Vector3 newPos)
    {
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
            _positionSendTimer += Time.deltaTime;
            if (_positionSendTimer >= PositionSendInterval)
            {
                _positionSendTimer = 0f;
                UpdateHeldPositionServerRpc(transform.position, transform.rotation);
            }
            return;
        }

        if (IsServer)
        {
            // Smoothly slide the block into snap position on the server.
            if (_isSnapping)
            {
                transform.position = Vector3.MoveTowards(
                    transform.position, _snapTarget, snapSpeed * Time.deltaTime);

                if (Vector3.Distance(transform.position, _snapTarget) < 0.001f)
                {
                    transform.position = _snapTarget;
                    _isSnapping = false;
                    // Freeze all constraints so the physics solver cannot jitter
                    // the block after snapping. Keep isKinematic = false so the
                    // block remains a solid collider — other blocks and hands
                    // cannot pass through it or push it into the table.
                    // Constraints are cleared in NotifyGrabbedServerRpc so the
                    // block can move freely again when picked up.
                    rb.isKinematic = true;
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.constraints = RigidbodyConstraints.FreezeAll;
                }
            }

            _networkPosition.Value = transform.position;
            _networkRotation.Value = transform.rotation;

            timer += Time.deltaTime;
            if (timer >= checkInterval)
            {
                timer = 0f;
                EvaluateStacked();
            }
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void UpdateHeldPositionServerRpc(Vector3 position, Quaternion rotation)
    {
        rb.isKinematic = true;
        rb.constraints = RigidbodyConstraints.None;
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

        if (_heldByPlayer.TryGetValue(localId, out StackableBlock alreadyHeld))
        {
            if (alreadyHeld != this)
            {
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

        NotifyReleasedServerRpc(localId, transform.position, transform.rotation);
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
        _isSnapping = false;
        rb.isKinematic = true;
        rb.constraints = RigidbodyConstraints.None; // clear any freeze applied after snapping

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
    void NotifyReleasedServerRpc(ulong playerId, Vector3 releasePosition, Quaternion releaseRotation)
    {
        _isBeingGrabbed.Value = false;
        transform.position = releasePosition;
        transform.rotation = releaseRotation;

        // Check if this block is near any other block.
        Vector3 snapPos;
        if (TryFindSnapTarget(releasePosition, out snapPos))
        {
            // Snap: slide smoothly into position, keep kinematic during slide.
            rb.isKinematic = true;
            _isSnapping = true;
            _snapTarget = snapPos;
            Debug.Log($"[StackableBlock] Snapping {gameObject.name} to {snapPos}");
        }
        else
        {
            // No nearby block — freeze exactly where released.
            // No gravity, no physics settling. This eliminates all network
            // sync jitter and table-sinking artifacts on Quest clients.
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.constraints = RigidbodyConstraints.FreezeAll;
            _networkPosition.Value = transform.position;
            _networkRotation.Value = transform.rotation;
        }

        Debug.Log($"[LOG] Player {playerId} released {gameObject.name} " +
                  $"at position {releasePosition} at time {Time.time}");

        if (InteractionLogger.Instance != null)
            StartCoroutine(LogReleasedDelayed(playerId, releasePosition));
        else
            Debug.LogWarning("[LOG] InteractionLogger.Instance is null!");
    }

    /// <summary>
    /// Looks for any nearby block and computes the clean snap-on-top position.
    /// Returns true if a snap target was found.
    /// </summary>
    private bool TryFindSnapTarget(Vector3 releasePos, out Vector3 snapPosition)
    {
        snapPosition = Vector3.zero;

        float blockHeight = box != null ? box.bounds.size.y : 1f;
        Collider[] nearby = Physics.OverlapSphere(releasePos, snapRadius, blockLayer);

        StackableBlock bestTarget = null;
        float bestDist = float.MaxValue;

        foreach (Collider col in nearby)
        {
            if (col.gameObject == gameObject) continue;

            StackableBlock other = col.GetComponent<StackableBlock>();
            if (other == null) continue;
            if (other._isBeingGrabbed.Value) continue;

            float dist = Vector3.Distance(releasePos, col.transform.position);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTarget = other;
            }
        }

        if (bestTarget == null) return false;

        // Snap position: top surface of the target block + half the height of
        // the block being placed. This accounts for center-pivot meshes and
        // eliminates the visible air gap between stacked blocks.
        BoxCollider targetBox = bestTarget.GetComponent<BoxCollider>();
        float targetTopY = targetBox != null
            ? targetBox.bounds.max.y
            : bestTarget.transform.position.y + (blockHeight / 2f);

        Vector3 candidate = new Vector3(
            bestTarget.transform.position.x,
            targetTopY + (blockHeight / 2f),
            bestTarget.transform.position.z
        );

        // Walk upward until the candidate position is clear.
        int maxStack = 10;
        for (int i = 0; i < maxStack; i++)
        {
            Collider[] atCandidate = Physics.OverlapSphere(candidate, blockHeight * 0.4f, blockLayer);
            bool occupied = false;
            foreach (Collider c in atCandidate)
            {
                if (c.gameObject == gameObject) continue;
                occupied = true;
                break;
            }
            if (!occupied) break;
            candidate += Vector3.up * blockHeight;
        }

        snapPosition = candidate;
        return true;
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
        if (_isSnapping) return; // do not evaluate while sliding into place
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