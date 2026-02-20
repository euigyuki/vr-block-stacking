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

    Rigidbody rb;
    BoxCollider box;
    XRGrabInteractable grab;
    float timer;

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

    void Update()
    {
        if (!IsServer) return;
        timer += Time.deltaTime;
        if (timer < checkInterval) return;
        timer = 0f;
        EvaluateStacked();
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        SetStacked(false);
        NotifyGrabbedServerRpc(NetworkManager.Singleton.LocalClientId);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        NotifyReleasedServerRpc(NetworkManager.Singleton.LocalClientId, transform.position);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NotifyGrabbedServerRpc(ulong playerId)
    {
        Debug.Log($"[LOG] Player {playerId} grabbed {gameObject.name} at time {Time.time}");
        if (InteractionLogger.Instance != null)
        {
            int score = StackingArea.Instance != null ? StackingArea.Instance.CurrentScore : 0;
            InteractionLogger.Instance.LogEvent(playerId, gameObject.name, "grabbed", transform.position, score);
        }
        else
            Debug.LogWarning("[LOG] InteractionLogger.Instance is null!");
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void NotifyReleasedServerRpc(ulong playerId, Vector3 position)
    {
        Debug.Log($"[LOG] Player {playerId} released {gameObject.name} at position {position} at time {Time.time}");
        if (InteractionLogger.Instance != null)
            StartCoroutine(LogReleasedDelayed(playerId, position));
        else
            Debug.LogWarning("[LOG] InteractionLogger.Instance is null!");
    }

    System.Collections.IEnumerator LogReleasedDelayed(ulong playerId, Vector3 position)
    {
        yield return new WaitForSeconds(0.2f);
        int score = StackingArea.Instance != null ? StackingArea.Instance.CurrentScore : 0;
        InteractionLogger.Instance.LogEvent(playerId, gameObject.name, "released", position, score);
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
        if (hit.collider != null && hit.collider.gameObject == gameObject) { SetStacked(false); return; }

        SetStacked(true);
    }

    void SetStacked(bool value)
    {
        if (IsStacked == value) return;
        IsStacked = value;
        OnStackedStateChanged?.Invoke(this, IsStacked);
    }
}