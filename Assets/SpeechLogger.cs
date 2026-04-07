using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Records the local player's microphone to a WAV file on device.
///
/// RELIABILITY:
///   Audio is flushed to disk every FlushInterval seconds so that data
///   is never lost — even if the app crashes or is force-killed.  The
///   WAV header is rewritten on each flush with the correct sample count,
///   so the file is always a valid WAV.
///
/// Design rationale:
///   - Plain MonoBehaviour, not NetworkBehaviour, for the same reason as
///     PlayerHeadReporter: this runs on the local device and accesses
///     hardware (microphone) that is not a network-spawned object.
///   - Records locally on each Quest to Application.persistentDataPath.
///     After the session, pull files via ADB:
///       adb pull /sdcard/Android/data/com.YourCompany.YourApp/files/ ./speech_logs/
///   - Writes timestamp sync events to InteractionLogger so the audio
///     can be aligned with gaze and interaction logs in the Python
///     analysis pipeline.
///   - Outputs standard 16-bit mono WAV at 16 kHz — compatible with
///     Whisper and most speech-to-text models out of the box.
///
/// Setup:
///   1. Attach to any always-active GameObject in the scene (e.g. XR Origin
///      or a dedicated "Loggers" object).
///   2. Grant microphone permission via ADB after installing:
///        adb shell pm grant com.YourCompany.YourApp android.permission.RECORD_AUDIO
///   3. No other configuration needed. Recording starts automatically
///      once the network client is connected.
///
/// Output:
///   speech_{clientId}_{yyyyMMdd_HHmmss}.wav
///   in Application.persistentDataPath on each Quest.
/// </summary>
public class SpeechLogger : MonoBehaviour
{
    [Header("Audio Settings")]
    [Tooltip("Recording sample rate in Hz. 16000 is standard for speech-to-text.")]
    public int SampleRate = 16000;

    [Tooltip("Length of the circular recording buffer in seconds. " +
             "Audio is flushed to disk well before this wraps around.")]
    public int BufferLengthSeconds = 120;

    [Header("Reliability")]
    [Tooltip("How often (in seconds) to flush recorded audio to disk. " +
             "Shorter = less data lost on crash, but more disk I/O.")]
    public float FlushInterval = 10f;

    [Header("Sync")]
    [Tooltip("How often (in seconds) to log a timestamp sync event to " +
             "InteractionLogger, linking audio elapsed time to game time.")]
    public float SyncInterval = 30f;

    // -------------------------------------------------------------------------
    // Internal state
    // -------------------------------------------------------------------------

    private AudioClip _clip;
    private string _deviceName;
    private bool _isRecording = false;
    private float _recordingStartTime;
    private float _syncTimer = 0f;
    private float _flushTimer = 0f;
    private string _outputPath;
    private ulong _clientId;

    // Tracks how many samples have been flushed to disk so far.
    private int _lastFlushedPosition = 0;

    // Total samples written to disk (across all flushes).
    private int _totalSamplesWritten = 0;

    // File stream kept open for the duration of the recording.
    private FileStream _fileStream;
    private BinaryWriter _fileWriter;

    // Tracks whether we've already logged the "recording_started" event.
    private bool _loggedStart = false;

    void Update()
    {
        // Wait until the network client is connected before starting.
        if (!_isRecording)
        {
            TryStartRecording();
            return;
        }

        // Periodic flush to disk.
        _flushTimer += Time.deltaTime;
        if (_flushTimer >= FlushInterval)
        {
            _flushTimer = 0f;
            FlushAudioToDisk();
        }

        // Periodic sync events.
        _syncTimer += Time.deltaTime;
        if (_syncTimer >= SyncInterval)
        {
            _syncTimer = 0f;
            LogSyncEvent();
        }
    }

    void OnApplicationQuit()
    {
        StopAndFinalize();
    }

    void OnDestroy()
    {
        StopAndFinalize();
    }

    // -------------------------------------------------------------------------
    // Recording lifecycle
    // -------------------------------------------------------------------------

    private void TryStartRecording()
    {
        // Don't record in the Editor — the GMKtec host has no real
        // microphone and its audio events clutter the session log.
#if UNITY_EDITOR
        return;
#endif

        // Guard: need a connected network client to know the player ID.
        if (NetworkManager.Singleton == null) return;
        if (!NetworkManager.Singleton.IsClient) return;
        if (!NetworkManager.Singleton.IsConnectedClient) return;

        // Check for available microphone.
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[SpeechLogger] No microphone detected.");
            return;
        }

        _clientId = NetworkManager.Singleton.LocalClientId;
        _deviceName = Microphone.devices[0];

        // Build output path.
        string fileName = $"speech_{_clientId}_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
        _outputPath = Path.Combine(Application.persistentDataPath, fileName);

        // Request microphone permission on Android/Quest.
        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            StartCoroutine(RequestMicPermission());
            return;
        }

        StartRecording();
    }

    private IEnumerator RequestMicPermission()
    {
        yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);

        if (Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            StartRecording();
        }
        else
        {
            Debug.LogError("[SpeechLogger] Microphone permission denied. " +
                           "Speech will not be recorded this session.");
        }
    }

    private void StartRecording()
    {
        // Use a looping buffer so long sessions don't hit the buffer limit.
        _clip = Microphone.Start(_deviceName, loop: true, BufferLengthSeconds, SampleRate);

        if (_clip == null)
        {
            Debug.LogError("[SpeechLogger] Microphone.Start returned null. " +
                           "Check device and permissions.");
            return;
        }

        // Open the WAV file and write the header.
        // The header will be rewritten on every flush with the correct size.
        try
        {
            _fileStream = new FileStream(_outputPath, FileMode.Create);
            _fileWriter = new BinaryWriter(_fileStream);
            WriteWavHeader(_fileWriter, 0); // placeholder, updated on each flush
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpeechLogger] Failed to create WAV file: {e.Message}");
            Microphone.End(_deviceName);
            return;
        }

        _isRecording = true;
        _recordingStartTime = Time.time;
        _lastFlushedPosition = 0;
        _totalSamplesWritten = 0;
        _syncTimer = 0f;
        _flushTimer = 0f;

        Debug.Log($"[SpeechLogger] Recording started — device: {_deviceName}, " +
                  $"sampleRate: {SampleRate}, clientId: {_clientId}, " +
                  $"output: {_outputPath}");

        // Log the recording start to InteractionLogger.
        if (!_loggedStart && InteractionLogger.Instance != null)
        {
            _loggedStart = true;
            InteractionLogger.Instance.LogEvent(
                _clientId,
                "microphone",
                "recording_started",
                Vector3.zero,
                0
            );
        }
    }

    /// <summary>
    /// Flushes any new audio samples from the microphone buffer to disk.
    /// Called periodically during recording.  Handles the circular buffer
    /// wrap-around correctly.
    /// </summary>
    private void FlushAudioToDisk()
    {
        if (_clip == null || _fileWriter == null) return;

        int currentPosition = Microphone.GetPosition(_deviceName);
        if (currentPosition < 0) return;

        // Nothing new to write.
        if (currentPosition == _lastFlushedPosition) return;

        int totalSamplesInBuffer = _clip.samples;
        float[] samples;

        if (currentPosition > _lastFlushedPosition)
        {
            // Simple case: no wrap-around.
            int count = currentPosition - _lastFlushedPosition;
            samples = new float[count];
            _clip.GetData(samples, _lastFlushedPosition);
        }
        else
        {
            // Wrap-around: read from last position to end, then from start
            // to current position.
            int countToEnd = totalSamplesInBuffer - _lastFlushedPosition;
            int countFromStart = currentPosition;
            samples = new float[countToEnd + countFromStart];

            if (countToEnd > 0)
            {
                float[] endChunk = new float[countToEnd];
                _clip.GetData(endChunk, _lastFlushedPosition);
                Array.Copy(endChunk, 0, samples, 0, countToEnd);
            }

            if (countFromStart > 0)
            {
                float[] startChunk = new float[countFromStart];
                _clip.GetData(startChunk, 0);
                Array.Copy(startChunk, 0, samples, countToEnd, countFromStart);
            }
        }

        // Write samples as 16-bit PCM.
        try
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                short pcm = (short)(clamped * short.MaxValue);
                _fileWriter.Write(pcm);
            }

            _totalSamplesWritten += samples.Length;
            _lastFlushedPosition = currentPosition;

            // Rewrite the WAV header with the updated sample count so
            // the file is always valid if the process dies right now.
            _fileStream.Seek(0, SeekOrigin.Begin);
            WriteWavHeader(_fileWriter, _totalSamplesWritten);
            _fileStream.Seek(0, SeekOrigin.End);

            _fileWriter.Flush();
            _fileStream.Flush();
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpeechLogger] Flush failed: {e.Message}");
        }
    }

    /// <summary>
    /// Final flush and cleanup. Captures the microphone position BEFORE
    /// stopping the mic, then flushes only the remaining unwritten samples.
    /// This prevents the circular buffer wrap-around from dumping the
    /// entire buffer (including silence) into the file.
    /// </summary>
    private void StopAndFinalize()
    {
        if (!_isRecording) return;
        _isRecording = false;

        // Capture the current write position BEFORE stopping the mic.
        // After Microphone.End(), GetPosition() returns 0 on most platforms,
        // which would trigger the wrap-around path and write the full buffer.
        int finalPosition = Microphone.GetPosition(_deviceName);

        // Stop the microphone.
        Microphone.End(_deviceName);

        // Flush only the samples between _lastFlushedPosition and finalPosition.
        // Skip if finalPosition is invalid (< 0) or if there's nothing new.
        if (finalPosition > 0 && finalPosition != _lastFlushedPosition &&
            _clip != null && _fileWriter != null)
        {
            try
            {
                int totalSamplesInBuffer = _clip.samples;
                float[] samples;

                if (finalPosition > _lastFlushedPosition)
                {
                    int count = finalPosition - _lastFlushedPosition;
                    samples = new float[count];
                    _clip.GetData(samples, _lastFlushedPosition);
                }
                else
                {
                    // Genuine wrap-around during the last flush interval.
                    int countToEnd = totalSamplesInBuffer - _lastFlushedPosition;
                    int countFromStart = finalPosition;
                    samples = new float[countToEnd + countFromStart];

                    if (countToEnd > 0)
                    {
                        float[] endChunk = new float[countToEnd];
                        _clip.GetData(endChunk, _lastFlushedPosition);
                        Array.Copy(endChunk, 0, samples, 0, countToEnd);
                    }
                    if (countFromStart > 0)
                    {
                        float[] startChunk = new float[countFromStart];
                        _clip.GetData(startChunk, 0);
                        Array.Copy(startChunk, 0, samples, countToEnd, countFromStart);
                    }
                }

                for (int i = 0; i < samples.Length; i++)
                {
                    float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                    short pcm = (short)(clamped * short.MaxValue);
                    _fileWriter.Write(pcm);
                }

                _totalSamplesWritten += samples.Length;

                // Final header update.
                _fileStream.Seek(0, SeekOrigin.Begin);
                WriteWavHeader(_fileWriter, _totalSamplesWritten);
                _fileStream.Seek(0, SeekOrigin.End);

                _fileWriter.Flush();
                _fileStream.Flush();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SpeechLogger] Final flush failed: {e.Message}");
            }
        }

        // Close the file.
        try
        {
            _fileWriter?.Close();
            _fileStream?.Close();
        }
        catch (Exception e)
        {
            Debug.LogError($"[SpeechLogger] Error closing file: {e.Message}");
        }

        _fileWriter = null;
        _fileStream = null;

        Debug.Log($"[SpeechLogger] Saved {_totalSamplesWritten} samples " +
                  $"({(float)_totalSamplesWritten / SampleRate:F1}s) to {_outputPath}");

        // Log stop event.
        if (InteractionLogger.Instance != null)
        {
            InteractionLogger.Instance.LogEvent(
                _clientId,
                "microphone",
                "recording_stopped",
                Vector3.zero,
                0
            );
        }
    }

    // -------------------------------------------------------------------------
    // Sync events
    // -------------------------------------------------------------------------

    private void LogSyncEvent()
    {
        if (InteractionLogger.Instance == null) return;

        float audioElapsed = Time.time - _recordingStartTime;

        InteractionLogger.Instance.LogEvent(
            _clientId,
            "microphone",
            "audio_sync",
            new Vector3(audioElapsed, 0f, 0f),
            0
        );

        Debug.Log($"[SpeechLogger] Sync — audioElapsed: {audioElapsed:F2}s, " +
                  $"gameTime: {Time.time:F2}s");
    }

    // -------------------------------------------------------------------------
    // WAV header writer
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes (or rewrites) a standard 16-bit PCM mono WAV header.
    /// Call with sampleCount=0 initially, then rewrite with the real
    /// count on each flush so the file is always valid.
    /// </summary>
    private static void WriteWavHeader(BinaryWriter writer, int sampleCount)
    {
        int channels = 1;
        int bitsPerSample = 16;
        int byteRate = 16000 * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);
        int dataSize = sampleCount * (bitsPerSample / 8);

        // RIFF header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);  // file size minus 8
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);              // chunk size
        writer.Write((short)1);        // PCM format
        writer.Write((short)channels);
        writer.Write(16000);           // sample rate
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
    }
}