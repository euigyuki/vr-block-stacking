using System;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Vivox;

/// <summary>
/// Manages Vivox voice chat for the VR DPIP study.
///
/// Provides a shared voice channel so participants in separate rooms
/// can communicate during the collaborative block-stacking task.
///
/// Flow:
///   1. Call JoinVoiceChannel() after Unity Services are initialized
///      and the player has signed in (RelayManager.SignInAsync handles this).
///   2. Vivox initializes, logs in, and joins a group channel.
///   3. Both Quest players hear each other through the channel.
///   4. On session end, call LeaveVoiceChannel() to clean up.
///
/// Setup:
///   1. Attach to the same GameObject as NetworkManager or any
///      always-active object.
///   2. Vivox credentials must be configured in Project Settings > Vivox.
///   3. Test Mode should be enabled for development builds.
///   4. Microphone permission must be granted on Quest via:
///        adb shell pm grant com.DefaultCompany.VRProject1 android.permission.RECORD_AUDIO
///
/// Note: This runs alongside SpeechLogger. Vivox handles real-time
/// voice communication; SpeechLogger records the local mic to WAV
/// for offline transcription. They do not interfere with each other.
/// </summary>
public class VivoxManager : MonoBehaviour
{
    public static VivoxManager Instance { get; private set; }

    [Header("Channel Settings")]
    [Tooltip("Name of the voice channel. All players in the same session " +
             "join this channel to hear each other.")]
    public string ChannelName = "dpip_session";

    /// <summary>
    /// True once the local player has joined the voice channel
    /// and can send/receive audio.
    /// </summary>
    public bool IsConnected { get; private set; }

    private bool _initialized = false;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Call this after Unity Services are initialized and the player
    /// has signed in. RelayManager.SignInAsync() handles both of these,
    /// so call JoinVoiceChannel() after SignInAsync() completes.
    /// </summary>
    public async Task JoinVoiceChannel()
    {
        try
        {
            // Initialize Vivox (safe to call multiple times).
            if (!_initialized)
            {
                await VivoxService.Instance.InitializeAsync();
                _initialized = true;
                Debug.Log("[VivoxManager] Vivox initialized.");
            }

            // Log in to Vivox. Uses Unity Authentication player ID
            // automatically when using Unity Gaming Services integration.
            if (!VivoxService.Instance.IsLoggedIn)
            {
                LoginOptions options = new LoginOptions();
                await VivoxService.Instance.LoginAsync(options);
                Debug.Log("[VivoxManager] Logged in to Vivox.");
            }

            // Join a group channel for voice chat.
            // AudioOnly — we don't need text chat for the study.
            await VivoxService.Instance.JoinGroupChannelAsync(
                ChannelName,
                ChatCapability.AudioOnly
            );

            IsConnected = true;
            Debug.Log($"[VivoxManager] Joined voice channel: {ChannelName}");

            // Subscribe to participant events for logging.
            VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAdded;
            VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemoved;
        }
        catch (Exception e)
        {
            Debug.LogError($"[VivoxManager] Failed to join voice channel: {e.Message}");
        }
    }

    /// <summary>
    /// Call on session end to cleanly leave the voice channel.
    /// </summary>
    public async Task LeaveVoiceChannel()
    {
        try
        {
            if (IsConnected)
            {
                VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAdded;
                VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemoved;

                await VivoxService.Instance.LeaveAllChannelsAsync();
                IsConnected = false;
                Debug.Log("[VivoxManager] Left voice channel.");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[VivoxManager] Error leaving channel: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (IsConnected)
        {
            // Fire-and-forget cleanup.
            _ = LeaveVoiceChannel();
        }
    }

    // -------------------------------------------------------------------------
    // Participant events — useful for debugging and future logging
    // -------------------------------------------------------------------------

    private void OnParticipantAdded(VivoxParticipant participant)
    {
        Debug.Log($"[VivoxManager] Participant joined: {participant.DisplayName} " +
                  $"(IsSelf: {participant.IsSelf})");
    }

    private void OnParticipantRemoved(VivoxParticipant participant)
    {
        Debug.Log($"[VivoxManager] Participant left: {participant.DisplayName}");
    }
}