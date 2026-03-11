using System;
using TMPro;
using UnityEngine;

/// <summary>
/// Drives session startup.
///
/// Editor (GMKtec / host):
///   Automatically allocates a Relay session and starts the host.
///   Displays the join code on screen for reference.
///
/// Device build (Quest / client):
///   Automatically reads the join code from Firebase and connects.
///   No manual input required.
/// </summary>
public class NetworkUI : MonoBehaviour
{
    [Header("Host UI (shown in Editor)")]
    [SerializeField] private GameObject hostPanel;
    [SerializeField] private TextMeshProUGUI joinCodeDisplay;

    [Header("Client UI (shown on Quest)")]
    [SerializeField] private GameObject clientPanel;

    [Header("Shared")]
    [SerializeField] private TextMeshProUGUI statusText;

    void Start()
    {
#if UNITY_EDITOR
        ShowHostUI();
        _ = StartHostAsync();
#else
        ShowClientUI();
        _ = StartClientAsync();
#endif
    }

    // -------------------------------------------------------------------------
    // Host (Editor)
    // -------------------------------------------------------------------------

    private void ShowHostUI()
    {
        if (hostPanel != null) hostPanel.SetActive(true);
        if (clientPanel != null) clientPanel.SetActive(false);
        SetStatus("Starting host...");
    }

    private async System.Threading.Tasks.Task StartHostAsync()
    {
        try
        {
            await RelayManager.Instance.StartHostWithRelay();
            string code = RelayManager.Instance.JoinCode;

            if (joinCodeDisplay != null)
                joinCodeDisplay.text = "Join Code: " + code;

            SetStatus("Session ready. Join code: " + code);
        }
        catch (Exception e)
        {
            SetStatus("Host failed: " + e.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Client (Quest)
    // -------------------------------------------------------------------------

    private void ShowClientUI()
    {
        if (hostPanel != null) hostPanel.SetActive(false);
        if (clientPanel != null) clientPanel.SetActive(true);
        SetStatus("Connecting to session...");
    }

    private async System.Threading.Tasks.Task StartClientAsync()
    {
        try
        {
            await RelayManager.Instance.StartClientWithRelay();
            SetStatus("Connected.");
        }
        catch (Exception e)
        {
            SetStatus("Connection failed: " + e.Message);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SetStatus(string message)
    {
        Debug.Log("[NetworkUI] " + message);
        if (statusText != null)
            statusText.text = message;
    }
}