using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Handles Unity Relay allocation, join code creation, and join code consumption.
/// Join codes are shared automatically via Firebase Realtime Database so Quests
/// can connect without any manual input.
///
/// Flow:
///   GMKtec (Editor):
///     1. Signs in anonymously to Unity Auth
///     2. Allocates a Relay server
///     3. Gets a join code
///     4. Writes the join code to Firebase
///     5. Starts NGO host
///
///   Quest (device build):
///     1. Signs in anonymously to Unity Auth
///     2. Reads the join code from Firebase
///     3. Joins the Relay allocation
///     4. Starts NGO client
///
/// Reconnection:
///   If the Relay transport fails (e.g. stale allocation, network hiccup),
///   the client automatically retries up to MaxReconnectAttempts times,
///   re-reading the join code from Firebase each time so it always gets
///   the latest allocation from the host.
/// </summary>
public class RelayManager : MonoBehaviour
{
    public static RelayManager Instance { get; private set; }

    // Maximum connections excluding host. 2 Quests = 2.
    private const int MaxConnections = 2;

    // Firebase Realtime Database URL.
    private const string FirebaseUrl =
        "https://vrblockstacking-default-rtdb.firebaseio.com/joinCode.json";

    // Reconnection settings.
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectDelayMs = 3000;

    public string JoinCode { get; private set; }
    public bool IsReady { get; private set; }

    private bool _isClient = false;
    private int _reconnectAttempts = 0;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        // Unsubscribe to avoid memory leaks or stale callbacks.
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnTransportFailure -= OnTransportFailure;
    }

    /// <summary>
    /// Signs into Unity Authentication anonymously.
    /// Required before any Relay operation.
    /// </summary>
    public async Task SignInAsync()
    {
        try
        {
            await UnityServices.InitializeAsync();
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log("[RelayManager] Signed in as: "
                    + AuthenticationService.Instance.PlayerId);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[RelayManager] Sign-in failed: " + e.Message);
            throw;
        }
    }

    /// <summary>
    /// Host path (GMKtec / Editor):
    /// Allocates Relay, gets join code, writes it to Firebase, starts NGO host.
    /// </summary>
    public async Task StartHostWithRelay()
    {
        try
        {
            await SignInAsync();

            Allocation allocation =
                await RelayService.Instance.CreateAllocationAsync(MaxConnections);

            JoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Debug.Log("[RelayManager] Join code: " + JoinCode);

            // Write join code to Firebase so Quests can read it automatically.
            StartCoroutine(WriteJoinCodeToFirebase(JoinCode));

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetRelayServerData(new RelayServerData(allocation, "wss"));

            IsReady = true;
            NetworkManager.Singleton.StartHost();
        }
        catch (Exception e)
        {
            Debug.LogError("[RelayManager] StartHostWithRelay failed: " + e.Message);
            throw;
        }
    }

    /// <summary>
    /// Client path (Quest / device build):
    /// Reads join code from Firebase, joins Relay allocation, starts NGO client.
    /// Also registers the transport failure handler for auto-reconnect.
    /// </summary>
    public async Task StartClientWithRelay()
    {
        try
        {
            _isClient = true;

            await SignInAsync();

            // Read join code from Firebase.
            string joinCode = await ReadJoinCodeFromFirebase();
            if (string.IsNullOrEmpty(joinCode))
            {
                Debug.LogError("[RelayManager] No join code found in Firebase. " +
                               "Make sure the host is running first.");
                ScheduleReconnect();
                return;
            }

            Debug.Log("[RelayManager] Retrieved join code: " + joinCode);

            JoinAllocation joinAllocation =
                await RelayService.Instance.JoinAllocationAsync(joinCode);

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetRelayServerData(new RelayServerData(joinAllocation, "wss"));

            // Register transport failure handler before starting client.
            NetworkManager.Singleton.OnTransportFailure -= OnTransportFailure;
            NetworkManager.Singleton.OnTransportFailure += OnTransportFailure;

            IsReady = true;
            _reconnectAttempts = 0;
            NetworkManager.Singleton.StartClient();
        }
        catch (Exception e)
        {
            Debug.LogError("[RelayManager] StartClientWithRelay failed: " + e.Message);
            ScheduleReconnect();
        }
    }

    // -------------------------------------------------------------------------
    // Reconnection logic
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called automatically by NGO when the Relay transport fails.
    /// Triggers a reconnect after a short delay.
    /// </summary>
    private void OnTransportFailure()
    {
        Debug.LogWarning("[RelayManager] Transport failure detected. " +
                         "Scheduling reconnect...");
        IsReady = false;
        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        if (!_isClient) return;
        StartCoroutine(ReconnectCoroutine());
    }

    private IEnumerator ReconnectCoroutine()
    {
        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Debug.LogError("[RelayManager] Max reconnect attempts reached. " +
                           "Please restart the app.");
            yield break;
        }

        _reconnectAttempts++;
        Debug.Log($"[RelayManager] Reconnect attempt {_reconnectAttempts}/" +
                  $"{MaxReconnectAttempts} in {ReconnectDelayMs / 1000}s...");

        yield return new WaitForSeconds(ReconnectDelayMs / 1000f);

        // Shut down the existing NetworkManager cleanly before retrying.
        if (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.Shutdown();

            // Wait for shutdown to complete.
            yield return new WaitForSeconds(1f);
        }

        // Re-read from Firebase and reconnect. Fire-and-forget.
        _ = StartClientWithRelay();
    }

    // -------------------------------------------------------------------------
    // Firebase REST helpers
    // -------------------------------------------------------------------------

    private IEnumerator WriteJoinCodeToFirebase(string joinCode)
    {
        string json = "\"" + joinCode + "\"";
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        using var request = new UnityWebRequest(FirebaseUrl, "PUT");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
            Debug.Log("[RelayManager] Join code written to Firebase: " + joinCode);
        else
            Debug.LogError("[RelayManager] Failed to write join code: "
                + request.error);
    }

    private async Task<string> ReadJoinCodeFromFirebase()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();
            StartCoroutine(ReadJoinCodeCoroutine(tcs));
            string result = await tcs.Task;

            if (!string.IsNullOrEmpty(result) && result != "null")
            {
                return result.Trim('"');
            }

            Debug.Log("[RelayManager] Join code not ready, retrying in 2s... " +
                      $"(attempt {attempt + 1}/10)");
            await Task.Delay(2000);
        }
        return null;
    }

    private IEnumerator ReadJoinCodeCoroutine(
        System.Threading.Tasks.TaskCompletionSource<string> tcs)
    {
        using var request = UnityWebRequest.Get(FirebaseUrl);
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
            tcs.SetResult(request.downloadHandler.text);
        else
        {
            Debug.LogError("[RelayManager] Failed to read join code: "
                + request.error);
            tcs.SetResult(null);
        }
    }
}