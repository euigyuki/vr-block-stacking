using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class StackingArea : MonoBehaviour
{
    public TextMeshProUGUI scoreText;

    [Header("Update Rate")]
    public float checkInterval = 0.1f;

    [Header("Filtering")]
    public float stillVelocityThreshold = 0.15f;

    [Header("Tower Detection")]
    [Tooltip("How wide the upward/downward box check is (relative to block size). 0.45 = ~half width of cube.")]
    public float boxCastHalfExtentFactor = 0.45f;

    [Tooltip("Extra distance to search above a block (in meters/units).")]
    public float extraSearchUp = 0.05f;

    [Tooltip("Extra distance to search below a block (in meters/units).")]
    public float extraSearchDown = 0.05f;

    private readonly HashSet<GameObject> blocksInZone = new();
    private float timer;

    private void OnTriggerEnter(Collider other)
    {
        var block = GetBlockRoot(other);
        if (block != null && block.CompareTag("Block"))
            blocksInZone.Add(block);
    }

    private void OnTriggerExit(Collider other)
    {
        var block = GetBlockRoot(other);
        if (block != null && block.CompareTag("Block"))
            blocksInZone.Remove(block);
    }

    private GameObject GetBlockRoot(Collider other)
    {
        if (other.attachedRigidbody != null) return other.attachedRigidbody.gameObject;
        var parentRb = other.GetComponentInParent<Rigidbody>();
        if (parentRb != null) return parentRb.gameObject;
        return other.gameObject;
    }

    private void Update()
    {
        timer += Time.deltaTime;
        if (timer >= checkInterval)
        {
            UpdateScore();
            timer = 0f;
        }
    }

    private void UpdateScore()
    {
        if (scoreText == null) return;

        blocksInZone.RemoveWhere(go => go == null);

        // Eligible = in zone + not held + mostly still
        List<GameObject> eligible = new();
        foreach (var block in blocksInZone)
        {
            var interactable = block.GetComponent<XRGrabInteractable>();
            var rb = block.GetComponent<Rigidbody>();

            bool isNotHeld = (interactable == null || !interactable.isSelected);
            bool isStill = (rb == null || rb.linearVelocity.magnitude < stillVelocityThreshold);

            if (isNotHeld && isStill)
                eligible.Add(block);
        }

        int highest = 0;

        foreach (var block in eligible)
        {
            // base block = nothing directly underneath
            if (!HasBlockDirectlyBelow(block))
            {
                int height = CountUpwards(block);
                if (height > highest) highest = height;
            }
        }

        scoreText.text = $"Live Score: {highest}";
    }

    private bool TryGetAnyCollider(GameObject go, out Collider col)
    {
        col = go.GetComponent<Collider>();
        if (col != null) return true;
        col = go.GetComponentInChildren<Collider>();
        return col != null;
    }

    private bool HasBlockDirectlyBelow(GameObject block)
    {
        if (!TryGetAnyCollider(block, out var col)) return false;

        Vector3 center = col.bounds.center;
        float halfX = col.bounds.extents.x * boxCastHalfExtentFactor;
        float halfZ = col.bounds.extents.z * boxCastHalfExtentFactor;

        Vector3 halfExtents = new Vector3(halfX, 0.01f, halfZ);

        // Cast down a small amount from just below the block center
        float dist = col.bounds.extents.y + extraSearchDown;

        // Start slightly above the bottom so we don't start inside table
        Vector3 origin = new Vector3(center.x, col.bounds.min.y + 0.01f, center.z);

        var hits = Physics.BoxCastAll(origin, halfExtents, Vector3.down, Quaternion.identity, dist);

        foreach (var h in hits)
        {
            var hitRoot = GetBlockRoot(h.collider);
            if (hitRoot != null && hitRoot != block && hitRoot.CompareTag("Block"))
                return true;
        }
        return false;
    }

    private int CountUpwards(GameObject baseBlock)
    {
        int height = 1;
        GameObject current = baseBlock;

        // walk upward: find the nearest block directly above each step
        while (true)
        {
            GameObject above = FindNearestBlockAbove(current);
            if (above == null) break;

            height++;
            current = above;

            if (height > 50) break; // safety
        }

        return height;
    }

    private GameObject FindNearestBlockAbove(GameObject block)
    {
        if (!TryGetAnyCollider(block, out var col)) return null;

        Vector3 center = col.bounds.center;

        float halfX = col.bounds.extents.x * boxCastHalfExtentFactor;
        float halfZ = col.bounds.extents.z * boxCastHalfExtentFactor;
        Vector3 halfExtents = new Vector3(halfX, 0.01f, halfZ);

        float dist = col.bounds.size.y + extraSearchUp;

        // start just above the top surface
        Vector3 origin = new Vector3(center.x, col.bounds.max.y + 0.01f, center.z);

        var hits = Physics.BoxCastAll(origin, halfExtents, Vector3.up, Quaternion.identity, dist);

        GameObject best = null;
        float bestY = float.PositiveInfinity;

        foreach (var h in hits)
        {
            var hitRoot = GetBlockRoot(h.collider);
            if (hitRoot == null || hitRoot == block || !hitRoot.CompareTag("Block")) continue;

            float y = hitRoot.transform.position.y;
            if (y < bestY)
            {
                bestY = y;
                best = hitRoot;
            }
        }

        return best;
    }
}
