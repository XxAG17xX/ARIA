// ARIADebugUI.cs
// Debug/testing UI for ARIA — works in both editor (IMGUI) and Quest headset (world-space Canvas).
//
// Quest: world-space Canvas with big buttons, positioned in front of user at launch.
//   Controller laser pointer clicks buttons. Countdown overlay for gaze-directed placement.
//
// Editor: IMGUI text field + buttons (same as before).

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[RequireComponent(typeof(ARIAOrchestrator))]
public class ARIADebugUI : MonoBehaviour
{
    [Header("Debug UI")]
    [SerializeField] private bool showDebugUI = true;
    [SerializeField] private int  fontSize    = 16;

    [Header("Quick placement commands")]
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
    private System.Action _pendingAction; // for demo spawns
    private const float CountdownSeconds = 3f;

    // Room scan removed — lighting now uses PTRL + single-frame analysis

    // VR Canvas UI elements (Quest)
    private Canvas     _vrCanvas;
    private GameObject _canvasGO;
    private bool       _menuVisible;
    private GameObject _mainPanel;
    private GameObject _countdownPanel;
    private Text       _statusText;
    private Text       _transcriptText;
    private Text       _countdownNumText;
    private Text       _countdownCmdText;

    // IMGUI styles (editor)
    private GUIStyle _panelStyle, _labelStyle, _bigLabelStyle;
    private GUIStyle _countdownStyle, _buttonStyle, _fieldStyle;
    private bool     _stylesInitialised;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private void Awake()
    {
        _orchestrator = GetComponent<ARIAOrchestrator>();
        _orchestrator.OnStatusChanged += s =>
        {
            if (!_countdownActive) SetStatus(s);
        };

        _voiceConnector = GetComponent<VoiceSDKConnector>();
        if (_voiceConnector != null)
        {
            _voiceConnector.OnPartialTranscript += t => _partialTranscript = t;
            _voiceConnector.OnFinalTranscript   += t => _partialTranscript = "";
            _voiceConnector.OnListeningChanged  += listening =>
            {
                if (!_countdownActive)
                    SetStatus(listening ? "Listening... speak a command." : "Ready.");
            };
        }
    }

    private void Start()
    {
        if (!showDebugUI) return;

        if (IsRunningOnQuest())
        {
            CreateVRCanvas();
            // Suppress Quest boundary for MR passthrough (user sees real world)
            SuppressBoundary();
        }
    }

    private static void SuppressBoundary()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        OVRManager.boundary.SetVisible(false);
#endif
    }

    // Gaze pointer state
    private Button _hoveredButton;
    private Image  _hoveredImage;
    private Color  _hoveredOrigColor;
    private GameObject _reticle;

    private void Update()
    {
        // Left Y button toggles menu
        if (IsRunningOnQuest() && _canvasGO != null)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (OVRInput.GetDown(OVRInput.Button.Four)) // Y button on left controller
                ToggleMenu();
#endif
        }

        // Voice recording timeout check
        CheckVoiceTimeout();

        // Gaze-based VR pointer (only when menu is visible)
        if (IsRunningOnQuest() && _vrCanvas != null && _menuVisible)
            UpdateGazePointer();

        if (!_countdownActive) return;

        float remaining = _countdownEnd - Time.time;

        // Update VR countdown display
        if (_countdownNumText != null)
        {
            int display = Mathf.CeilToInt(remaining);
            if (display < 1) display = 1;
            _countdownNumText.text = display.ToString();
        }

        if (remaining <= 0f)
        {
            _countdownActive = false;
            if (_countdownPanel != null) _countdownPanel.SetActive(false);
            if (_mainPanel != null) _mainPanel.SetActive(true);

            if (_pendingAction != null)
            {
                SetStatus("Spawning...");
                _pendingAction.Invoke();
                _pendingAction = null;
            }
            else
            {
                SetStatus("Capturing & sending...");
                _orchestrator.ProcessVoiceCommand(_pendingCommand);
            }
        }
    }

    private void UpdateGazePointer()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // Ensure reticle dot exists
        if (_reticle == null) CreateReticle(cam);

        Ray gazeRay = new Ray(cam.transform.position, cam.transform.forward);
        Button hitBtn = null;

        if (Physics.Raycast(gazeRay, out RaycastHit hit, 5f))
        {
            hitBtn = hit.collider.GetComponentInParent<Button>();
            // Move reticle to hit point
            _reticle.transform.position = hit.point - gazeRay.direction * 0.001f;
            _reticle.transform.rotation = Quaternion.LookRotation(-hit.normal);
        }
        else
        {
            // Park reticle 2m ahead
            _reticle.transform.position = cam.transform.position + cam.transform.forward * 2f;
            _reticle.transform.rotation = Quaternion.LookRotation(cam.transform.forward);
        }

        // Hover highlight
        if (hitBtn != _hoveredButton)
        {
            // Unhighlight previous
            if (_hoveredButton != null && _hoveredImage != null)
                _hoveredImage.color = _hoveredOrigColor;

            _hoveredButton = hitBtn;
            if (hitBtn != null)
            {
                _hoveredImage = hitBtn.GetComponent<Image>();
                if (_hoveredImage != null)
                {
                    _hoveredOrigColor = _hoveredImage.color;
                    _hoveredImage.color = new Color(0.4f, 0.4f, 0.55f, 1f);
                }
            }
        }

        // Trigger / A button = click
#if UNITY_ANDROID && !UNITY_EDITOR
        bool triggerDown = OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger) || // left trigger
                           OVRInput.GetDown(OVRInput.Button.Three); // X button (left)
#else
        bool triggerDown = Input.GetMouseButtonDown(0);
#endif
        if (triggerDown && _hoveredButton != null)
        {
            _hoveredButton.onClick.Invoke();
            // Flash feedback
            if (_hoveredImage != null)
                _hoveredImage.color = new Color(0.6f, 0.6f, 0.8f, 1f);
        }
    }

    private void CreateReticle(Camera cam)
    {
        _reticle = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _reticle.name = "ARIA_GazeReticle";
        _reticle.transform.localScale = Vector3.one * 0.015f; // 1.5cm dot
        Destroy(_reticle.GetComponent<Collider>()); // don't interfere with raycasts

        var mat = new Material(Shader.Find("UI/Default"));
        mat.color = new Color(1f, 1f, 1f, 0.8f);
        _reticle.GetComponent<Renderer>().material = mat;
    }

    // -------------------------------------------------------------------------
    // VR Canvas UI (Quest) — world-space, controller-clickable
    // -------------------------------------------------------------------------

    private void CreateVRCanvas()
    {
        // Ensure EventSystem exists for button interaction
        if (FindFirstObjectByType<EventSystem>() == null)
        {
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
            // Try XR input module first (handles Quest controllers), fall back to standard
            var xrInputType = System.Type.GetType(
                "UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule, Unity.XR.Interaction.Toolkit");
            if (xrInputType != null)
                esGO.AddComponent(xrInputType);
            else
                esGO.AddComponent<StandaloneInputModule>();
        }

        // Canvas: world-space, 600x520 px at 0.001 scale = 0.6m x 0.52m real
        _canvasGO = new GameObject("ARIA_VR_UI");
        var canvasGO = _canvasGO;
        _vrCanvas = canvasGO.AddComponent<Canvas>();
        _vrCanvas.renderMode = RenderMode.WorldSpace;
        canvasGO.AddComponent<CanvasScaler>();

        // TrackedDeviceGraphicRaycaster handles Quest controller laser pointer clicks
        // Falls back to standard GraphicRaycaster if XR Interaction Toolkit not available
        var xrRaycasterType = System.Type.GetType(
            "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit");
        if (xrRaycasterType != null)
            canvasGO.AddComponent(xrRaycasterType);
        else
            canvasGO.AddComponent<GraphicRaycaster>();

        var canvasRT = canvasGO.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(600, 950);
        canvasGO.transform.localScale = Vector3.one * 0.001f;

        // Start hidden — press left Y button to summon in front of you
        Camera cam = Camera.main;
        canvasGO.SetActive(false);

        // ── Main panel ──────────────────────────────────────────────────────
        _mainPanel = MakePanel(canvasGO.transform, "MainPanel",
            Vector2.zero, new Vector2(600, 950), new Color(0f, 0f, 0f, 0.75f));

        float y = 445f; // start from top

        MakeLabel(_mainPanel.transform, "Title", "ARIA — Look at target, then tap:",
            new Vector2(0, y), new Vector2(560, 36), 22, FontStyle.Bold, Color.white);
        y -= 50f;

        // Quick command buttons
        MakeButton(_mainPanel.transform, "Cmd1", quickCommand1,
            new Vector2(0, y), new Vector2(560, 55), () => StartCountdown(quickCommand1));
        y -= 65f;

        MakeButton(_mainPanel.transform, "Cmd2", quickCommand2,
            new Vector2(0, y), new Vector2(560, 55), () => StartCountdown(quickCommand2));
        y -= 65f;

        MakeButton(_mainPanel.transform, "Cmd3", quickCommand3,
            new Vector2(0, y), new Vector2(560, 55), () => StartCountdown(quickCommand3));
        y -= 75f;

        // Room scan removed — lighting now uses single-frame analysis + PTRL shader

        // Lighting row
        MakeButton(_mainPanel.transform, "BtnLight", "Apply Lighting",
            new Vector2(-145, y), new Vector2(270, 50),
            () => _orchestrator.ApplyLightingToAllSpawned());
        MakeButton(_mainPanel.transform, "BtnToggle", "ARIA vs Default",
            new Vector2(145, y), new Vector2(270, 50),
            () => _orchestrator.ToggleLightingComparison());
        y -= 65f;

        // ── DEMO SPAWN — pre-bundled GLBs, zero API calls ──────────────
        MakeLabel(_mainPanel.transform, "DemoTitle", "DEMO (instant, no credits):",
            new Vector2(0, y), new Vector2(560, 30), 18, FontStyle.Bold,
            new Color(0.4f, 1f, 0.6f));
        y -= 40f;

        MakeButton(_mainPanel.transform, "DemoBed", "Spawn Bed (floor)",
            new Vector2(-145, y), new Vector2(270, 48),
            () => StartCountdownForAction("Bed on floor",
                () => { _orchestrator.SetUserContext("spawn a bed"); _orchestrator.SpawnBundledGlb("bed.glb", "FLOOR", 0.5f, "bed", 1.4f, 2.0f); }));
        MakeButton(_mainPanel.transform, "DemoLamp", "Spawn Lamp (floor)",
            new Vector2(145, y), new Vector2(270, 48),
            () => StartCountdownForAction("Lamp on floor",
                () => { _orchestrator.SetUserContext("spawn a lamp"); _orchestrator.SpawnBundledGlb("lamp.glb", "FLOOR", 1.5f, "lamp"); }));
        y -= 58f;

        MakeButton(_mainPanel.transform, "DemoWall", "Spawn Wall Art (wall)",
            new Vector2(0, y), new Vector2(560, 48),
            () => StartCountdownForAction("Wall art on wall",
                () => { _orchestrator.SetUserContext("hang a painting on the wall"); _orchestrator.SpawnBundledGlb("wall_art.glb", "WALL_FACE", 0.6f, "wall_art", 0.8f, 0.05f); }));
        y -= 58f;

        // Adjust with Claude — user looks at target, then presses
        MakeButton(_mainPanel.transform, "BtnAdjust", "Adjust with Claude (speak + look)",
            new Vector2(0, y), new Vector2(560, 48),
            () => StartVoiceAdjust());
        y -= 58f;

        // Toggle EffectMesh visibility
        MakeButton(_mainPanel.transform, "BtnEffectMesh", "Toggle Room Wireframe",
            new Vector2(0, y), new Vector2(560, 44),
            () => ToggleEffectMesh());
        y -= 52f;

        // Transcript
        _transcriptText = MakeLabel(_mainPanel.transform, "Transcript", "",
            new Vector2(0, y), new Vector2(560, 28), 16, FontStyle.Italic, Color.grey);
        y -= 32f;

        // Status
        _statusText = MakeLabel(_mainPanel.transform, "Status",
            "Status: " + _status,
            new Vector2(0, y), new Vector2(560, 50), 17, FontStyle.Normal, Color.white);

        // ── Countdown overlay (hidden) ──────────────────────────────────────
        _countdownPanel = MakePanel(canvasGO.transform, "CountdownPanel",
            Vector2.zero, new Vector2(600, 950), new Color(0f, 0f, 0f, 0.9f));

        _countdownNumText = MakeLabel(_countdownPanel.transform, "CNum", "3",
            new Vector2(0, 60), new Vector2(300, 180), 120, FontStyle.Bold,
            new Color(1f, 0.85f, 0.2f));

        MakeLabel(_countdownPanel.transform, "CInstr",
            "Look at where you want objects placed...",
            new Vector2(0, -60), new Vector2(560, 40), 22, FontStyle.Normal, Color.white);

        _countdownCmdText = MakeLabel(_countdownPanel.transform, "CCmd", "",
            new Vector2(0, -110), new Vector2(560, 30), 16, FontStyle.Italic, Color.grey);

        _countdownPanel.SetActive(false);

        Debug.Log("[ARIA] VR Canvas UI created.");
    }

    // -------------------------------------------------------------------------
    // UI helper factories
    // -------------------------------------------------------------------------

    private static GameObject MakePanel(Transform parent, string name,
        Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.color = color;
        return go;
    }

    private static Text MakeLabel(Transform parent, string name, string text,
        Vector2 pos, Vector2 size, int fSize, FontStyle style, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        var t = go.GetComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = fSize;
        t.fontStyle = style;
        t.color = color;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Truncate;
        return t;
    }

    private static void MakeButton(Transform parent, string name, string label,
        Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
            typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        var img = go.GetComponent<Image>();
        img.color = new Color(0.25f, 0.25f, 0.3f, 1f);

        var btn = go.GetComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.35f, 0.35f, 0.45f);
        colors.pressedColor     = new Color(0.5f, 0.5f, 0.6f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);

        // Button text as child
        var txtGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        txtGO.transform.SetParent(go.transform, false);
        var txtRT = txtGO.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = new Vector2(8, 4);
        txtRT.offsetMax = new Vector2(-8, -4);
        var t = txtGO.GetComponent<Text>();
        t.text = label;
        t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.fontSize = 18;
        t.fontStyle = FontStyle.Bold;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;

        // BoxCollider for gaze raycast hit detection (sized in local/pixel space,
        // Canvas scale 0.001 converts to world units automatically)
        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(size.x, size.y, 10f);
    }

    // -------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------

    private void ToggleMenu()
    {
        _menuVisible = !_menuVisible;
        _canvasGO.SetActive(_menuVisible);

        if (_menuVisible)
        {
            // Place in front of user at current head position (stays in world space)
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 fwd = cam.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
                else fwd.Normalize();
                _canvasGO.transform.position = cam.transform.position + fwd * 1.2f;
                _canvasGO.transform.rotation = Quaternion.LookRotation(fwd);
            }
        }

        // Hide/show reticle with menu
        if (_reticle != null) _reticle.SetActive(_menuVisible);
    }

    // -------------------------------------------------------------------------
    // Room lighting scan — 4-photo 360° capture
    // -------------------------------------------------------------------------

    // Room scan methods removed — lighting uses PTRL + single-frame analysis

    // -------------------------------------------------------------------------
    // Voice-assisted Claude adjustment
    // -------------------------------------------------------------------------

    private bool _waitingForVoice;
    private float _voiceTimeout;

    private void StartVoiceAdjust()
    {
        if (_voiceConnector != null && !_waitingForVoice)
        {
            // Step 1: Record voice command with 5-second timeout
            SetStatus("Speak now... (5s timeout, or press trigger to skip)");
            _waitingForVoice = true;
            _voiceTimeout = Time.time + 5f;

            if (_countdownPanel != null)
            {
                _countdownPanel.SetActive(true);
                _mainPanel.SetActive(false);
                _countdownNumText.text = "Speak";
                _countdownCmdText.text = "Say what to adjust, or press trigger to skip";
            }

            _voiceConnector.RecordOneShot(transcript =>
            {
                _waitingForVoice = false;
                SetStatus($"Heard: \"{transcript}\" — now look at target...");
                _orchestrator.SetUserContext(transcript);

                if (_countdownCmdText != null)
                    _countdownCmdText.text = $"\"{transcript}\"";

                StartCountdownForAction("Claude adjustment",
                    () => _orchestrator.AdjustLastSpawnWithClaude());
            });
        }
        else
        {
            // No voice SDK or already waiting — skip voice, go straight to countdown
            StartCountdownForAction("Claude adjustment",
                () => _orchestrator.AdjustLastSpawnWithClaude());
        }
    }

    private void CheckVoiceTimeout()
    {
        if (!_waitingForVoice) return;

        // Check for trigger press to skip voice recording
#if UNITY_ANDROID && !UNITY_EDITOR
        bool skip = OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger) ||
                    OVRInput.GetDown(OVRInput.Button.Three);
#else
        bool skip = Input.GetMouseButtonDown(0);
#endif

        if (skip || Time.time > _voiceTimeout)
        {
            _waitingForVoice = false;
            _voiceConnector?.StopListening();

            if (_countdownPanel != null) _countdownPanel.SetActive(false);
            if (_mainPanel != null) _mainPanel.SetActive(true);

            SetStatus("Voice skipped — adjusting with visual context only...");
            StartCountdownForAction("Claude adjustment",
                () => _orchestrator.AdjustLastSpawnWithClaude());
        }
    }

    private bool _effectMeshHidden;

    private void ToggleEffectMesh()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        _effectMeshHidden = !_effectMeshHidden;

        // EffectMesh spawns child GameObjects with renderers at runtime —
        // find ALL renderers in the EffectMesh hierarchy AND the MRUK room objects
        var effectMesh = FindFirstObjectByType<Meta.XR.MRUtilityKit.EffectMesh>();
        if (effectMesh != null)
        {
            // Toggle the EffectMesh GameObject's children (spawned surface meshes)
            foreach (Transform child in effectMesh.transform)
                child.gameObject.SetActive(!_effectMeshHidden);
        }

        // Also toggle the HideMesh property on the EffectMesh component itself
        if (effectMesh != null)
            effectMesh.HideMesh = _effectMeshHidden;

        SetStatus(_effectMeshHidden ? "Room wireframe OFF" : "Room wireframe ON");
#endif
    }

    private void SetStatus(string s)
    {
        _status = s;
        if (_statusText != null) _statusText.text = "Status: " + s;
    }

    private void StartCountdown(string command)
    {
        if (_countdownActive) return;
        _pendingCommand = command;
        _pendingAction = null;
        _countdownActive = true;
        _countdownEnd = Time.time + CountdownSeconds;
        BeginCountdownUI(command);
    }

    private void StartCountdownForAction(string label, System.Action action)
    {
        if (_countdownActive) return;
        _pendingAction = action;
        _pendingCommand = null;
        _countdownActive = true;
        _countdownEnd = Time.time + CountdownSeconds;
        BeginCountdownUI(label);
    }

    private void BeginCountdownUI(string label)
    {
        SetStatus("Look at your target...");
        if (_countdownPanel != null)
        {
            _countdownCmdText.text = $"\"{label}\"";
            _countdownPanel.SetActive(true);
            _mainPanel.SetActive(false);
        }
    }

    private void SendCommand(string overrideText = null)
    {
        string cmd = (overrideText ?? _inputText).Trim();
        if (string.IsNullOrEmpty(cmd)) return;
        SetStatus("Sending...");
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
    // Editor IMGUI (unchanged — only draws when NOT on Quest)
    // -------------------------------------------------------------------------

    private void OnGUI()
    {
        if (!showDebugUI || IsRunningOnQuest()) return;
        InitStyles();

        if (_countdownActive) { DrawCountdownOverlay(); return; }
        DrawEditorUI();
    }

    private void DrawEditorUI()
    {
        float panelW = 420f, panelH = 240f;
        float x = 20f, y = Screen.height - panelH - 20f;
        GUI.Box(new Rect(x, y, panelW, panelH), GUIContent.none, _panelStyle);

        float padding = 12f, inner = x + padding;
        float lineH = fontSize + 8f, cy = y + padding;

        GUI.Label(new Rect(inner, cy, panelW - padding * 2f, lineH),
            "ARIA Debug (Editor)", _labelStyle);
        cy += lineH + 4f;

        GUI.Label(new Rect(inner, cy, 90f, lineH), "Command:", _labelStyle);
        _inputText = GUI.TextField(
            new Rect(inner + 95f, cy, panelW - padding * 2f - 95f, lineH), _inputText);
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
            SendCommand();
        cy += lineH + 6f;

        float btnW = (panelW - padding * 2f - 8f) / 2f;
        if (GUI.Button(new Rect(inner, cy, btnW, lineH + 4f), "Send Command"))
            SendCommand();
        if (GUI.Button(new Rect(inner + btnW + 8f, cy, btnW, lineH + 4f), "Quick Test"))
            SendCommand(quickCommand1);
        cy += lineH + 6f;

        float halfBtn = (panelW - padding * 2f - 8f) / 2f;
        if (GUI.Button(new Rect(inner, cy, halfBtn, lineH + 4f), "Apply Lighting"))
            _orchestrator.ApplyLightingToAllSpawned();
        if (GUI.Button(new Rect(inner + halfBtn + 8f, cy, halfBtn, lineH + 4f), "ARIA vs Default"))
            _orchestrator.ToggleLightingComparison();
        cy += lineH + 10f;

        if (!string.IsNullOrEmpty(_partialTranscript))
        {
            GUI.Label(new Rect(inner, cy, panelW - padding * 2f, lineH),
                $"\"{_partialTranscript}\"", _labelStyle);
            cy += lineH + 2f;
        }

        GUI.Label(new Rect(inner, cy, panelW - padding * 2f, lineH * 2f),
            $"Status: {_status}", _labelStyle);
    }

    private void DrawCountdownOverlay()
    {
        float remaining = _countdownEnd - Time.time;
        int display = Mathf.CeilToInt(remaining);
        if (display < 1) display = 1;

        GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none, _panelStyle);
        GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 200f),
            display.ToString(), _countdownStyle);
        GUI.Label(new Rect(0, Screen.height * 0.55f, Screen.width, 60f),
            "Look at where you want objects placed...", _bigLabelStyle);
        GUI.Label(new Rect(0, Screen.height * 0.65f, Screen.width, 40f),
            $"\"{_pendingCommand}\"", _labelStyle);
    }

    private void InitStyles()
    {
        if (_stylesInitialised) return;
        _stylesInitialised = true;

        var panelTex = new Texture2D(1, 1);
        panelTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
        panelTex.Apply();

        _panelStyle = new GUIStyle(GUI.skin.box) { normal = { background = panelTex } };

        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize, normal = { textColor = Color.white },
            alignment = TextAnchor.MiddleCenter
        };

        _bigLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize + 6, fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }, alignment = TextAnchor.MiddleCenter
        };

        _countdownStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 120, fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(1f, 0.85f, 0.2f) },
            alignment = TextAnchor.MiddleCenter
        };
    }
}
