// VoiceSDKConnector.cs — hooks up Meta's Wit.ai voice SDK to our pipeline
// listens for voice transcriptions and forwards them to the orchestrator.
// one-shot mode: records voice on button press, fires callback with transcript, stops.
// auto-activate is OFF so the mic doesn't randomly pick up background speech.
// whole thing is wrapped in #if !UNITY_EDITOR since there's no mic in editor.

using UnityEngine;
#if !UNITY_EDITOR
using Oculus.Voice;
#endif

[RequireComponent(typeof(ARIAOrchestrator))]
public class VoiceSDKConnector : MonoBehaviour
{
    [Tooltip("Start listening automatically on launch. Disable for push-to-talk.")]
    [SerializeField] private bool autoActivate = false;

    [Tooltip("Minimum transcript length before forwarding to ARIA (filters noise).")]
    [SerializeField] private int minTranscriptLength = 3;

    private ARIAOrchestrator _orchestrator;

#if !UNITY_EDITOR
    private AppVoiceExperience _voice;
#endif

    public event System.Action<string> OnPartialTranscript;
    public event System.Action<string> OnFinalTranscript;
    public event System.Action<bool>   OnListeningChanged;

    private void Awake()
    {
        _orchestrator = GetComponent<ARIAOrchestrator>();

#if !UNITY_EDITOR
        _voice = GetComponent<AppVoiceExperience>();
        if (_voice == null)
        {
            Debug.LogWarning("[ARIA] VoiceSDKConnector: no AppVoiceExperience found. " +
                             "Add one via Meta XR → Building Blocks → Voice SDK.");
            enabled = false;
            return;
        }

        _voice.VoiceEvents.OnPartialTranscription.AddListener(OnPartial);
        _voice.VoiceEvents.OnFullTranscription.AddListener(OnFull);
        _voice.VoiceEvents.OnStartListening.AddListener(() =>
        {
            Debug.Log("[ARIA] 🎙 Listening...");
            OnListeningChanged?.Invoke(true);
        });
        _voice.VoiceEvents.OnStoppedListening.AddListener(() =>
        {
            Debug.Log("[ARIA] 🎙 Stopped listening.");
            OnListeningChanged?.Invoke(false);
        });
        _voice.VoiceEvents.OnError.AddListener((errType, msg) =>
        {
            Debug.LogWarning($"[ARIA] Voice error ({errType}): {msg}");
        });
#else
        Debug.Log("[ARIA] VoiceSDKConnector: editor mode — voice disabled. Use debug UI.");
#endif
    }

    private void Start()
    {
#if !UNITY_EDITOR
        if (autoActivate && _voice != null)
        {
            Debug.Log("[ARIA] Voice auto-activate: starting mic...");
            _voice.Activate();
        }
#endif
    }

    // One-shot recording mode: records voice, calls callback with transcript, then stops
    private System.Action<string> _oneShotCallback;
    private bool _oneShotMode;
    public bool IsListening
    {
        get
        {
#if !UNITY_EDITOR
            return _voice != null && _voice.MicActive;
#else
            return false;
#endif
        }
    }

    /// <summary>Call from a controller button or hand gesture to start listening.</summary>
    public void StartListening()
    {
#if !UNITY_EDITOR
        if (_voice != null && !_voice.MicActive)
            _voice.Activate();
#endif
    }

    /// <summary>Record one voice command, call the callback with the transcript, then stop.</summary>
    public void RecordOneShot(System.Action<string> callback)
    {
        _oneShotCallback = callback;
        _oneShotMode = true;
#if !UNITY_EDITOR
        if (_voice != null)
        {
            Debug.Log("[ARIA] Voice one-shot: recording for adjustment...");
            OnListeningChanged?.Invoke(true);
            _voice.Activate();
        }
#endif
    }

    /// <summary>Call to stop listening early.</summary>
    public void StopListening()
    {
#if !UNITY_EDITOR
        if (_voice != null && _voice.MicActive)
            _voice.Deactivate();
#endif
    }

#if !UNITY_EDITOR
    private void OnPartial(string transcript)
    {
        OnPartialTranscript?.Invoke(transcript);
    }

    private void OnFull(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript) || transcript.Length < minTranscriptLength)
        {
            Debug.Log($"[ARIA] Transcript too short, ignoring: \"{transcript}\"");
            return;
        }

        Debug.Log($"[ARIA] Voice transcript: \"{transcript}\"");
        OnFinalTranscript?.Invoke(transcript);

        // One-shot mode: return transcript to callback, don't trigger pipeline
        if (_oneShotMode && _oneShotCallback != null)
        {
            _oneShotMode = false;
            var cb = _oneShotCallback;
            _oneShotCallback = null;
            OnListeningChanged?.Invoke(false);
            cb.Invoke(transcript);
            return;
        }

        // Voice is now button-triggered only (Speak to ARIA button)
        // If somehow a non-one-shot transcript comes through, ignore it
        Debug.LogWarning($"[ARIA] Ignoring non-one-shot transcript: \"{transcript}\"");
    }

    private void OnDestroy()
    {
        if (_voice != null)
        {
            _voice.VoiceEvents.OnPartialTranscription.RemoveListener(OnPartial);
            _voice.VoiceEvents.OnFullTranscription.RemoveListener(OnFull);
        }
    }
#endif
}
