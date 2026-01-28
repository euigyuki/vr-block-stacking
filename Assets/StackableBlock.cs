using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class StackableBlock : MonoBehaviour
{
    public static System.Action<StackableBlock, bool> OnStackedStateChanged;

    [Header("Detection")]
    public LayerMask blockLayer;         // Set to Blocks
    public float castExtraDistance = 0.02f;
    public float boxShrink = 0.02f;

    [Header("Stability")]
    public float settleVelocity = 0.15f; // must be "not moving much" to count
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
        timer += Time.deltaTime;
        if (timer < checkInterval) return;
        timer = 0f;

        EvaluateStacked();
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        // Don’t count blocks while held
        SetStacked(false);
    }

    void OnReleased(SelectExitEventArgs args)
    {
        // After release, we’ll detect again on next interval
    }

    void EvaluateStacked()
    {
        if (rb == null || box == null) return;

        // Must be mostly still to count as stacked
        if (rb.linearVelocity.magnitude > settleVelocity) // Unity 6 uses linearVelocity
        {
            SetStacked(false);
            return;
        }

        Bounds b = box.bounds;
        Vector3 center = b.center;
        Vector3 halfExtents = b.extents - new Vector3(boxShrink, boxShrink, boxShrink);

        // cast down slightly below the block
        float castDistance = b.extents.y + castExtraDistance;

        bool hitSomethingBelow =
            Physics.BoxCast(
                center,
                halfExtents,
                Vector3.down,
                out RaycastHit hit,
                transform.rotation,
                castDistance,
                blockLayer,
                QueryTriggerInteraction.Ignore
            );

        if (!hitSomethingBelow)
        {
            SetStacked(false);
            return;
        }

        // Make sure we’re not hitting ourselves
        if (hit.collider != null && hit.collider.gameObject == gameObject)
        {
            SetStacked(false);
            return;
        }

        // If we hit a block-layer object below, we are stacked
        SetStacked(true);
    }

    void SetStacked(bool value)
    {
        if (IsStacked == value) return;

        IsStacked = value;
        OnStackedStateChanged?.Invoke(this, IsStacked);
    }
}
