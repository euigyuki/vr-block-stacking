using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;

public class InteractionLogger : NetworkBehaviour
{
    [System.Serializable]
    public class InteractionEvent
    {
        public string timestamp;
        public float gameTime;
        public ulong playerId;
        public string blockName;
        public string action;
        public Vector3Data position;
        public int score; 

    }

    [System.Serializable]
    public class Vector3Data
    {
        public float x, y, z;
    }

    [System.Serializable]
    public class SessionLog
    {
        public string sessionStart;
        public List<InteractionEvent> events = new List<InteractionEvent>();
    }

    private SessionLog sessionLog = new SessionLog();
    private string logFilePath;

    public static InteractionLogger Instance { get; private set; }

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
        sessionLog.sessionStart = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
#if UNITY_EDITOR
        logFilePath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
            $"session_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        );
#else
    logFilePath = Path.Combine(Application.persistentDataPath,
        $"session_{DateTime.Now:yyyyMMdd_HHmmss}.json");
#endif
        Debug.Log($"[Logger] Logging to: {logFilePath}");
    }

    public void LogEvent(ulong playerId, string blockName, string action, Vector3 position, int score=0)
    {
        if (!IsServer) return;

        var evt = new InteractionEvent
        {
            timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            gameTime = Time.time,
            playerId = playerId,
            blockName = blockName,
            action = action,
            position = new Vector3Data { x = position.x, y = position.y, z = position.z },
            score = score
        };

        sessionLog.events.Add(evt);
        SaveLog();

        Debug.Log($"[LOG] {evt.timestamp} Player {playerId} {action} {blockName} at ({position.x:F2}, {position.y:F2}, {position.z:F2})");
    }

    private void SaveLog()
    {
        string json = JsonUtility.ToJson(sessionLog, true);
        File.WriteAllText(logFilePath, json);
    }
}