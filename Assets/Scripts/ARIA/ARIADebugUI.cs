// ARIADebugUI.cs
// Debug/testing UI for ARIA — works in both editor (text input) and Quest headset (big buttons).
// Uses Unity's immediate-mode IMGUI (OnGUI) which renders as a HUD overlay in VR.
//
// Headset flow:
//   1. Look at where you want objects placed
//   2. Tap "Place Objects Here" with controller trigger (laser pointer)
//   3. 3-2-1 countdown appears — keep looking at the target spot
//   4. At 0: passthrough frame captured, Claude runs, objects spawn where you looked
//   5. Tap again looking somewhere else — new objects go there
//
// Editor flow:
//   1. Type a command in the text field
//   2. Press "Send Command" or Enter
//   3. "Quick Test" uses a pre-set command with the text field's content

using UnityEngine;

[RequireComponent(typeof(ARIAOrchestrator))]
public class ARIADebugUI : MonoBehaviour
{
    [Header("Debug UI — visible in Game View and Quest HUD")]
    [SerializeField] private bool showDebugUI = true;
    [SerializeField] private int  fontSize    = 16;

    [Header("Quick placement commands (Quest)")]
    [SerializeField] private string quickCommand1 = "put a tall floor lamp and a cozy armchair";
    [SerializeField] private string quickCommand2 = "hang a framed landscape painting on the wall";
    [SerializeField] private string quickCommand3 = "place a small potted plant on the table";

    private ARIAOrchestrator  _orchestrator;
    private VoiceSDKConnector _voiceConnector;
    private string            _inputText = "put a reading lamp in the corner";
    private string            _status    = "Ready — look where you want objects, then tap a button.";
    private string            _partialTranscript = "";

    // Countdown state
    private bool   _countdownActive;
    private float  _countdownEnd;
    private string _pendingCommand;
    private const float CountdownSeconds = 3f;

    // Styles
    private GUIStyle _panelStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _bigLabelStyle;
    private GUIStyle _countdownStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _bigButtonStyle;
    private GUIStyle _fieldStyle;
    private bool     _stylesInitialised;

    private void Awake()
    {
        _orchestrator = GetComponent<ARIAOrchestrator>();
        _orchestrator.OnStatusChanged += s =>
        {
            if (!_countdownActive) _status = s;
        };

        _voiceConnector = GetComponent<VoiceSDKConnector>();
        if (_voiceConnector != null)
        {
            _voiceConnector.OnPartialTranscript += t => _partialTranscript = t;
            _voiceConnector.OnFinalTranscript   += t => _partialTranscript = "";
            _voiceConnector.OnListeningChanged  += listening =>
            {
                if (!_countdownActive)
                    _status = listening ? "Listening... speak a command." : "Ready.";
            };
        }
    }

    private void Update()
    {
        if (!_countdownActive) return;

        float remaining = _countdownEnd - Time.time;
        if (remaining <= 0f)
        {
            _countdownActive = false;
            _status = "Capturing & sending...";
            _orchestrator.ProcessVoiceCommand(_pendingCommand);
        }
    }

    private void OnGUI()
    {
        if (!showDebugUI) return;
        InitStyles();

        // If countdown is active, draw big centered countdown overlay
        if (_countdownActive)
        {
            DrawCountdownOverlay();
            return; // hide everything else during countdown
        }

        bool isVR = IsRunningOnQuest();

        if (isVR)
            DrawQuestUI();
        else
            DrawEditorUI();
    }

    // -------------------------------------------------------------------------
    // Quest UI — big buttons, no text field
    // -------------------------------------------------------------------------

    private void DrawQuestUI()
    {
        float panelW = 500f;
        float panelH = 360f;
        float x = (Screen.width - panelW) / 2f;
        float y = Screen.height - panelH - 40f;

        GUI.Box(new Rect(x, y, panelW, panelH), GUIContent.none, _panelStyle);

        float padding = 16f;
        float inner = x + padding;
        float btnH = 50f;
        float cy = y + padding;

        GUI.Label(new Rect(inner, cy, panelW - padding * 2f, 30f),
            "ARIA — Look at target, then tap:", _bigLabelStyle);
        cy += 36f;

        // Big placement buttons
        if (GUI.Button(new Rect(inner, cy, panelW - padding * 2f, btnH), quickCommand1, _bigButtonStyle))
            StartCountdown(quickCommand1);
        cy += btnH + 8f;

        if (GUI.Button(new Rect(inner, cy, panelW - padding * 2f, btnH), quickCommand2, _bigButtonStyle))
            StartCountdown(quickCommand2);
        cy += btnH + 8f;

        if (GUI.Button(new Rect(inner, cy, panelW - padding * 2f, btnH), quickCommand3, _bigButtonStyle))
            StartCountdown(quickCommand3);
        cy += btnH + 12f;

        // Apply lighting button
        if (GUI.Button(new Rect(inner, cy, panelW - padding * 2f, btnH), "Apply Lighting to All Objects", _bigButtonStyle))
            _orchestrator.ApplyLightingToAllSpawned();
        cy += btnH + 12f;

        // Live transcript
        if (!string.IsNullOrEmpty(_partialTranscript))
        {
            GUI.Label(new Rect(inner, cy, panelW - padding * 2f, 24f),
                $"\"{_partialTranscript}\"", _labelStyle);
            cy += 26f;
        }

        // Status
        GUI.Label(new Rect(inner, cy, panelW - padding * 2f, 40f),
            $"Status: {_status}", _labelStyle);
    }

    // -------------------------------------------------------------------------
    // Editor UI — text field + buttons (same as before)
    // -------------------------------------------------------------------------

    private void DrawEditorUI()
    {
        float panelW = 420f;
        float panelH = 240f;
        float x = 20f;
        float y = Screen.height - panelH - 20f;

        GUI.Box(new Rect(x, y, panelW, panelH), GUIContent.none, _panelStyle);

        float padding = 12f;
        float inner = x + padding;
        float lineH = fontSize + 8f;
        float cy = y + padding;

        GUI.Label(new Rect(inner, cy, panelW - padding * 2f, lineH),
            "ARIA Debug (Editor)", _labelStyle);
        cy += lineH + 4f;

        // Command input
        GUI.Label(new Rect(inner, cy, 90f, lineH), "Command:", _labelStyle);
        Event e = Event.current;
        _inputText = GUI.TextField(
            new Rect(inner + 95f, cy, panelW - padding * 2f - 95f, lineH),
            _inputText, _fieldStyle);

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Return
            && GUI.GetNameOfFocusedControl() != "")
            SendCommand();

        cy += lineH + 6f;

        // Buttons row
        float btnW = (panelW - padding * 2f - 8f) / 2f;
        if (GUI.Button(new Rect(inner, cy, btnW, lineH + 4f), "Send Command", _buttonStyle))
            SendCommand();
        if (GUI.Button(new Rect(inner + btnW + 8f, cy, btnW, lineH + 4f), "Quick Test", _buttonStyle))
            SendCommand(quickCommand1);
        cy += lineH + 6f;

        // Apply lighting
        if (GUI.Button(new Rect(inner, cy, panelW - padding * 2f, lineH + 4f),
            "Apply Lighting to All Objects", _buttonStyle))
            _orchestrator.ApplyLightingToAllSpawned();
        cy += lineH + 10f;

        // Live transcript
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

    // -------------------------------------------------------------------------
    // Countdown overlay — big centered numbers
    // -------------------------------------------------------------------------

    private void StartCountdown(string command)
    {
        if (_countdownActive) return;
        _pendingCommand = command;
        _countdownActive = true;
        _countdownEnd = Time.time + CountdownSeconds;
        _status = "Look at your target...";
    }

    private void DrawCountdownOverlay()
    {
        float remaining = _countdownEnd - Time.time;
        int display = Mathf.CeilToInt(remaining);
        if (display < 1) display = 1;

        // Semi-transparent full-screen overlay
        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _panelStyle);

        // Big countdown number
        string countText = display.ToString();
        GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 200f),
            countText, _countdownStyle);

        // Instruction text below
        GUI.Label(new Rect(0, Screen.height * 0.55f, Screen.width, 60f),
            "Look at where you want objects placed...", _bigLabelStyle);

        // Show what will be placed
        GUI.Label(new Rect(0, Screen.height * 0.65f, Screen.width, 40f),
            $"\"{_pendingCommand}\"", _labelStyle);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SendCommand(string overrideText = null)
    {
        string cmd = (overrideText ?? _inputText).Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        _status = "Sending...";
        _orchestrator.ProcessVoiceCommand(cmd);
    }

    private static bool IsRunningOnQuest()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return true;
#else
        return false;
#endif
    }

    // -------------------------------------------------------------------------
    // Styles
    // -------------------------------------------------------------------------

    private void InitStyles()
    {
        if (_stylesInitialised) return;
        _stylesInitialised = true;

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
            normal    = { textColor = Color.white },
            alignment = TextAnchor.MiddleCenter
        };

        _bigLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = fontSize + 6,
            fontStyle = FontStyle.Bold,
            normal    = { textColor = Color.white },
            alignment = TextAnchor.MiddleCenter
        };

        _countdownStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 120,
            fontStyle = FontStyle.Bold,
            normal    = { textColor = new Color(1f, 0.85f, 0.2f) }, // yellow-gold
            alignment = TextAnchor.MiddleCenter
        };

        _buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = fontSize - 1
        };

        _bigButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize  = fontSize + 2,
            fontStyle = FontStyle.Bold,
            padding   = new RectOffset(12, 12, 8, 8)
        };

        _fieldStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize = fontSize - 1
        };
    }
}
