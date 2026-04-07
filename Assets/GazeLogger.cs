using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Logs a snapshot every SnapshotInterval seconds for each connected player.
/// Each snapshot records:
///   - Head position and gaze direction
///   - Which blocks are visible (unobstructed raycast from head to block)
///   - Which blocks are within VisibilityRadius of the player
///   - Current stack configuration (what is resting on what)
///
/// Runs only on the server. Player head transforms are reported via
/// ClientHeadStateServerRpc, called each tick by the client.
///
/// Skips Player 0 (the GMKtec host) since it uses a fixed Editor camera
/// with no real head tracking data.
///
/// Output: a separate JSON file per session, e.g.
///   gaze_20260311_192312.json
/// saved to the same directory as the grab/release log.
/// </summary>
public class GazeLogger : NetworkBehaviour
{
    public static GazeLogger Instance { get; private set; }

    [Header("Logging")]
    [Tooltip("How often (in seconds) to take a snapshot of each player's state.")]
    public float SnapshotInterval = 5f;

    [Tooltip("Radius (in meters) around the player's head to check for nearby blocks.")]
    public float VisibilityRadius = 1.5f;

    [Tooltip("Layer mask for blocks — must match the Blocks layer.")]
    public LayerMask blockLayer;

    // Populated at runtime: clientId -> most recent head transform reported by that client.
    private Dictionary<ulong, HeadState> _headStates = new Dictionary<ulong, HeadState>();

    // All blocks in the scene, populated on server start.
    private List<StackableBlock> _allBlocks = new List<StackableBlock>();

    private float _timer = 0f;
    private string _logPath;
    private List<object> _snapshots = new List<object>();
    private string _sessionStart;

    private struct HeadState
    {
        public Vector3 Position;
        public Vector3 Forward;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // Collect all StackableBlock instances in the scene.
        _allBlocks.AddRange(FindObjectsByType<StackableBlock>(FindObjectsSortMode.None));

        // Set up log file path matching InteractionLogger convention.
        _sessionStart = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string fileName = "gaze_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";

        // Save to desktop on the GMKtec host. Falls back to persistentDataPath
        // on Quest builds where there is no desktop folder.
        string desktopPath = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.Desktop);
        string savePath = string.IsNullOrEmpty(desktopPath)
            ? Application.persistentDataPath
            : desktopPath;
        _logPath = Path.Combine(savePath, fileName);
        Debug.Log("[GazeLogger] Logging to: " + _logPath);
    }

    void Update()
    {
        if (!IsServer) return;

        _timer += Time.deltaTime;
        if (_timer >= SnapshotInterval)
        {
            _timer = 0f;
            TakeSnapshots();
        }
    }

    /// <summary>
    /// Called by each client every SnapshotInterval to report their head transform.
    /// The server collects these and uses them when building snapshots.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void ReportHeadStateServerRpc(Vector3 position, Vector3 forward, ulong clientId)
    {
        _headStates[clientId] = new HeadState { Position = position, Forward = forward };
    }

    /// <summary>
    /// Takes one snapshot per connected player and appends to the log.
    /// Skips Player 0 (GMKtec host) since it has no real head tracking.
    /// </summary>
    private void TakeSnapshots()
    {
        if (_headStates.Count == 0) return;

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        float gameTime = Time.time;

        foreach (var kvp in _headStates)
        {
            ulong playerId = kvp.Key;

            // Skip the host (Player 0) — it uses a fixed Editor camera
            // with no real head tracking data.
            if (playerId == 0) continue;

            HeadState head = kvp.Value;

            List<string> visibleBlocks = GetVisibleBlocks(head.Position, head.Forward);
            List<string> nearbyBlocks = GetNearbyBlocks(head.Position);
            List<object> stackConfig = GetStackConfiguration();

            var snapshot = new
            {
                timestamp,
                gameTime,
                playerId,
                headPosition = new { x = head.Position.x, y = head.Position.y, z = head.Position.z },
                gazeDirection = new { x = head.Forward.x, y = head.Forward.y, z = head.Forward.z },
                visibleBlocks,
                blocksInRadius = nearbyBlocks,
                stackConfiguration = stackConfig
            };

            _snapshots.Add(snapshot);

            Debug.Log($"[GazeLogger] Snapshot — Player {playerId} | " +
                      $"Visible: [{string.Join(", ", visibleBlocks)}] | " +
                      $"Nearby: [{string.Join(", ", nearbyBlocks)}]");
        }

        WriteLog();
    }

    /// <summary>
    /// Casts a ray from the player's head toward each block.
    /// A block is "visible" if the ray reaches it without hitting another collider first.
    /// Also checks that the block is within the player's forward field of view (90 degrees).
    /// </summary>
    private List<string> GetVisibleBlocks(Vector3 headPos, Vector3 headForward)
    {
        List<string> visible = new List<string>();

        foreach (StackableBlock block in _allBlocks)
        {
            if (block == null) continue;

            Vector3 toBlock = block.transform.position - headPos;
            float distance = toBlock.magnitude;

            // Only check blocks within VisibilityRadius.
            if (distance > VisibilityRadius) continue;

            // Check if block is within 90-degree FOV cone.
            float angle = Vector3.Angle(headForward, toBlock.normalized);
            if (angle > 90f) continue;

            // Raycast to check for obstructions.
            if (!Physics.Raycast(headPos, toBlock.normalized, out RaycastHit hit, distance))
            {
                // No hit means nothing in the way — block is visible.
                visible.Add(block.gameObject.name);
            }
            else if (hit.collider != null &&
                     hit.collider.GetComponent<StackableBlock>() != null &&
                     hit.collider.gameObject.name == block.gameObject.name)
            {
                // Ray hit the block itself — visible.
                visible.Add(block.gameObject.name);
            }
        }

        return visible;
    }

    /// <summary>
    /// Returns all blocks within VisibilityRadius of the player's head,
    /// regardless of line of sight.
    /// </summary>
    private List<string> GetNearbyBlocks(Vector3 headPos)
    {
        List<string> nearby = new List<string>();
        Collider[] hits = Physics.OverlapSphere(headPos, VisibilityRadius, blockLayer);

        foreach (Collider col in hits)
        {
            StackableBlock block = col.GetComponent<StackableBlock>();
            if (block != null)
                nearby.Add(col.gameObject.name);
        }

        return nearby;
    }

    /// <summary>
    /// Builds a description of what is stacked on what by checking which block
    /// is directly above each other block.
    /// </summary>
    private List<object> GetStackConfiguration()
    {
        var config = new List<object>();

        foreach (StackableBlock block in _allBlocks)
        {
            if (block == null) continue;

            string stackedOn = null;

            // Cast a short ray downward from this block's center.
            // If it hits another StackableBlock, that is what this block is resting on.
            BoxCollider box = block.GetComponent<BoxCollider>();
            if (box != null)
            {
                float castDistance = box.bounds.extents.y + 0.05f;
                if (Physics.Raycast(block.transform.position, Vector3.down,
                    out RaycastHit hit, castDistance, blockLayer))
                {
                    if (hit.collider != null &&
                        hit.collider.gameObject != block.gameObject)
                    {
                        stackedOn = hit.collider.gameObject.name;
                    }
                }
            }

            config.Add(new
            {
                block = block.gameObject.name,
                position = new
                {
                    x = block.transform.position.x,
                    y = block.transform.position.y,
                    z = block.transform.position.z
                },
                stackedOn
            });
        }

        return config;
    }

    /// <summary>
    /// Writes all accumulated snapshots to the JSON log file as pretty-printed JSON.
    /// Called after every snapshot tick so data is not lost if the session ends abruptly.
    /// </summary>
    private void WriteLog()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"sessionStart\": \"{_sessionStart}\",");
            sb.AppendLine($"  \"snapshotIntervalSeconds\": {SnapshotInterval.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},");
            sb.AppendLine("  \"snapshots\": [");

            for (int i = 0; i < _snapshots.Count; i++)
            {
                sb.Append(SnapshotToJson(_snapshots[i], 4));
                if (i < _snapshots.Count - 1) sb.Append(",");
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.Append("}");

            File.WriteAllText(_logPath, sb.ToString());
        }
        catch (Exception e)
        {
            Debug.LogError("[GazeLogger] Failed to write log: " + e.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Pretty-printed JSON serialization via reflection
    // -------------------------------------------------------------------------

    private string SnapshotToJson(object obj, int indent)
    {
        var type = obj.GetType();
        var props = type.GetProperties();
        string pad = new string(' ', indent);
        string innerPad = new string(' ', indent + 2);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(pad + "{");

        for (int i = 0; i < props.Length; i++)
        {
            string name = props[i].Name;
            object val = props[i].GetValue(obj);
            string comma = i < props.Length - 1 ? "," : "";
            sb.AppendLine($"{innerPad}\"{name}\": {ValueToJson(val, indent + 2)}{comma}");
        }

        sb.Append(pad + "}");
        return sb.ToString();
    }

    private string ValueToJson(object val, int indent)
    {
        string pad = new string(' ', indent);
        string innerPad = new string(' ', indent + 2);

        if (val == null) return "null";
        if (val is string s) return $"\"{EscapeJson(s)}\"";
        if (val is bool b) return b ? "true" : "false";
        if (val is float f) return f.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        if (val is double d) return d.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        if (val is int || val is long || val is ulong) return val.ToString();

        if (val is List<string> ls)
        {
            if (ls.Count == 0) return "[]";
            return "[\n" + innerPad +
                   string.Join(",\n" + innerPad, ls.ConvertAll(x => $"\"{EscapeJson(x)}\"")) +
                   "\n" + pad + "]";
        }

        if (val is List<object> lo)
        {
            if (lo.Count == 0) return "[]";
            var items = lo.ConvertAll(x => SnapshotToJson(x, indent + 2));
            return "[\n" + string.Join(",\n", items) + "\n" + pad + "]";
        }

        // Nested anonymous object.
        var vtype = val.GetType();
        if (vtype.IsClass && !vtype.IsPrimitive)
            return SnapshotToJson(val, indent);

        return $"\"{val}\"";
    }

    private string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        if (IsServer && _snapshots.Count > 0)
            WriteLog();
    }
}