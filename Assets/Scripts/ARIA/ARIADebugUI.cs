// ARIADebugUI.cs
// Editor testing UI — lets you trigger the full ARIA pipeline from a text field
// without a headset or microphone. Uses Unity's immediate-mode IMGUI (OnGUI).
//
// Add this component to the ARIA_Manager GameObject alongside ARIAOrchestrator.
// It draws a floating panel in the Game View during Play mode (editor and APK).
//
// Usage:
//   1. Type a voice command in the text field (e.g. "make this corner a reading nook")
//   2. Press "Send Command" or hit Enter → calls ARIAOrchestrator.ProcessVoiceCommand()
//   3. "Mock Room" injects a test command using the orchestrator's built-in mock MRUK JSON
//   4. Status label shows the current pipeline stage

using UnityEngine;

[RequireComponent(typeof(ARIAOrchestrator))]
public class ARIADebugUI : MonoBehaviour
{
    [Header("Debug UI — visible in Game View during Play mode")]
    [SerializeField] private bool showDebugUI = true;
    [SerializeField] private int  fontSize    = 16;

    private ARIAOrchestrator _orchestrator;
    private VoiceSDKConnector _voiceConnector;
    private string           _inputText = "make this corner a reading nook";
    private string           _status    = "Ready.";
    private string           _partialTranscript = "";
    private GUIStyle         _panelStyle;
    private GUIStyle         _labelStyle;
    private GUIStyle         _buttonStyle;
    private GUIStyle         _fieldStyle;
    private bool             _stylesInitialised;

    private void Awake()
    {
        _orchestrator = GetComponent<ARIAOrchestrator>();
        _orchestrator.OnStatusChanged += s => _status = s;

        _voiceConnector = GetComponent<VoiceSDKConnector>();
        if (_voiceConnector != null)
        {
            _voiceConnector.OnPartialTranscript += t => _partialTranscript = t;
            _voiceConnector.OnFinalTranscript   += t => _partialTranscript = "";
            _voiceConnector.OnListeningChanged  += listening =>
                _status = listening ? "Listening..." : "Ready.";
        }
    }

    private void OnGUI()
    {
        if (!showDebugUI) return;

        InitStyles();

        float panelW = 420f;
        float panelH = 210f;
        float x      = 20f;
        float y      = Screen.height - panelH - 20f;

        // Background panel
        GUI.Box(new Rect(x, y, panelW, panelH), GUIContent.none, _panelStyle);

        float padding = 12f;
        float inner   = x + padding;
        float lineH   = fontSize + 8f;
        float cy      = y + padding;

        GUI.Label(new Rect(inner, cy, panelW - padding * 2f, lineH),
            "ARIA Debug", _labelStyle);
        cy += lineH + 4f;

        // Command input field
        GUI.Label(new Rect(inner, cy, 90f, lineH), "Command:", _labelStyle);
        Event e = Event.current;
        _inputText = GUI.TextField(
            new Rect(inner + 95f, cy, panelW - padding * 2f - 95f, lineH),
            _inputText, _fieldStyle);

        // Fire on Enter
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Return
            && GUI.GetNameOfFocusedControl() != "")
            SendCommand();

        cy += lineH + 6f;

        // Buttons
        float btnW = (panelW - padding * 2f - 8f) / 2f;
        if (GUI.Button(new Rect(inner, cy, btnW, lineH + 4f), "Send Command", _buttonStyle))
            SendCommand();
        if (GUI.Button(new Rect(inner + btnW + 8f, cy, btnW, lineH + 4f), "Mock Room Test", _buttonStyle))
            SendCommand("put a reading lamp in the corner and a small side table");

        cy += lineH + 6f;

        // Apply lighting retroactively to all spawned objects
        if (GUI.Button(new Rect(inner, cy, panelW - padding * 2f, lineH + 4f),
            "Apply Lighting to All Objects", _buttonStyle))
        {
            _orchestrator.ApplyLightingToAllSpawned();
        }

        cy += lineH + 10f;

        // Live transcript (from Voice SDK on Quest)
        if (!string.IsNullOrEmpty(_partialTranscript))
        {
            GUI.Label(new Rect(inner, cy, panelW - padding * 2f, lineH),
                $"\"{_partialTranscript}\"", _labelStyle);
            cy += lineH + 2f;
        }

        // Status
        GUI.Label(new Rect(inner, cy, panelW - padding * 2f, lineH * 2f),
            $"Status: {_status}", _labelStyle);
    }

    private void SendCommand(string overrideText = null)
    {
        string cmd = (overrideText ?? _inputText).Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        _status = "Sending...";
        _orchestrator.ProcessVoiceCommand(cmd);
    }

    private void InitStyles()
    {
        if (_stylesInitialised) return;
        _stylesInitialised = true;

        // Semi-transparent dark panel
        var panelTex = new Texture2D(1, 1);
        panelTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
        panelTex.Apply();

        _panelStyle = new GUIStyle(GUI.skin.box)
        {
            normal = { background = panelTex }
        };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = fontSize,
            fontStyle = FontStyle.Normal,
            normal    = { textColor = Color.white }
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = fontSize - 1
        };

        _fieldStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize = fontSize - 1
        };
    }
}
