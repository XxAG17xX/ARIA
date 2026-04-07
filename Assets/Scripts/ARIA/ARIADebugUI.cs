// ARIADebugUI.cs
// Debug/testing UI for ARIA — works in both editor (IMGUI) and Quest headset (world-space Canvas).
//
// Quest: world-space Canvas with big buttons, positioned in front of user at launch.
//   Controller laser pointer clicks buttons. Countdown overlay for gaze-directed placement.
//
// Editor: IMGUI text field + buttons (same as before).

using System.Collections.Generic;
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

    // PTRL toggle button reference
    private GameObject _ptrlButton;
    private Text _ptrlButtonText;

    // Activity log panel (shows Claude calls, light confirms, spawns, PTRL, errors)
    private GameObject _logCanvasGO;
    private Text       _logText;
    private static string _claudeLog = "No activity yet.";
    public static void AppendClaudeLog(string msg)
    {
        string timestamp = System.DateTime.Now.ToString("HH:mm:ss");
        _claudeLog = $"[{timestamp}] {msg}\n\n{_claudeLog}";
        if (_claudeLog.Length > 3000) _claudeLog = _claudeLog.Substring(0, 3000);
    }

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
            CreateLogPanel();
            // Suppress Quest boundary for MR passthrough (user sees real world)
            SuppressBoundary();
        }
    }

    private void CreateLogPanel()
    {
        _logCanvasGO = new GameObject("ARIA_LogPanel");
        var canvas = _logCanvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _logCanvasGO.AddComponent<CanvasScaler>();
        _logCanvasGO.AddComponent<GraphicRaycaster>();

        var rt = _logCanvasGO.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(500, 700);
        _logCanvasGO.transform.localScale = Vector3.one * 0.001f;

        // Dark background
        var panel = MakePanel(_logCanvasGO.transform, "LogBg",
            Vector2.zero, new Vector2(500, 700), new Color(0f, 0f, 0f, 0.85f));

        // Title
        MakeLabel(panel.transform, "LogTitle", "ARIA Log",
            new Vector2(0, 320), new Vector2(460, 30), 20, FontStyle.Bold, Color.yellow);

        // Close button (top right)
        MakeButton(panel.transform, "LogClose", "X",
            new Vector2(220, 320), new Vector2(50, 30),
            () => { _logCanvasGO.SetActive(false); });

        // Log text (scrollable via pages)
        _logText = MakeLabel(panel.transform, "LogContent", _claudeLog,
            new Vector2(0, -20), new Vector2(480, 600), 12, FontStyle.Normal, Color.white);
        _logText.alignment = TextAnchor.UpperLeft;

        _logCanvasGO.SetActive(false);
    }

    private static void SuppressBoundary()
    {
        try { OVRManager.boundary.SetVisible(false); }
        catch (System.Exception) { /* Deprecated in OpenXR — safe to ignore */ }
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
            if (OVRInput.GetDown(OVRInput.Button.Four)) // Y button on left controller
                ToggleMenu();
            // Left grip = place light at crosshair (no menu needed)
            if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) && !_menuVisible)
                _orchestrator.PlaceLightAtCrosshair();
            // Right grip = grab/release nearest light source
            if (OVRInput.Get(OVRInput.Button.SecondaryHandTrigger))
                GrabNearestLight();
            else if (_grabbedLight != null)
                ReleaseLight();
        }

        // X button: cycle selection / delete (only when menu is NOT visible)
        if (IsRunningOnQuest() && !_menuVisible)
        {
            if (OVRInput.GetDown(OVRInput.Button.Three)) // X button on left controller
                CycleOrDeleteSelection();
        }

        // Voice recording timeout check
        CheckVoiceTimeout();

        // Keep log panel text fresh while menu is open
        if (_menuVisible && _logCanvasGO != null && _logCanvasGO.activeSelf && _logText != null)
        {
            _logText.text = _claudeLog;
            _logText.SetAllDirty();
        }

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
            // Force world-space canvas to redraw (batch invalidation)
            _countdownNumText.SetAllDirty();
            Canvas.ForceUpdateCanvases();
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

        // Trigger / mouse click (OVRInput works via Quest Link in editor)
        bool triggerDown = OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger) ||
                           OVRInput.GetDown(OVRInput.Button.Three) ||
                           Input.GetMouseButtonDown(0);
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
        _ptrlButton = MakeButtonWithRef(_mainPanel.transform, "BtnPTRL", "PTRL: OFF",
            new Vector2(0, y), new Vector2(560, 50),
            () => {
                bool on = _orchestrator.TogglePTRL();
                if (_ptrlButtonText != null)
                {
                    _ptrlButtonText.text = on ? "PTRL: ON" : "PTRL: OFF";
                    _ptrlButtonText.SetAllDirty();
                }
            });
        y -= 58f;

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
        // Light + wireframe controls
        MakeButton(_mainPanel.transform, "BtnPlaceLight", "Place Light Here",
            new Vector2(-145, y), new Vector2(270, 44),
            () => _orchestrator.PlaceLightAtCrosshair());
        MakeButton(_mainPanel.transform, "BtnEffectMesh", "Toggle Wireframe",
            new Vector2(145, y), new Vector2(270, 44),
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

    private GameObject MakeButtonWithRef(Transform parent, string name, string label,
        Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        MakeButton(parent, name, label, pos, size, onClick);
        // Find the text child to update later
        var btnGO = parent.Find(name);
        if (btnGO != null)
        {
            var txt = btnGO.GetComponentInChildren<Text>();
            _ptrlButtonText = txt;
        }
        return btnGO?.gameObject;
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
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 fwd = cam.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
                else fwd.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

                // Main menu in front
                _canvasGO.transform.position = cam.transform.position + fwd * 1.2f;
                _canvasGO.transform.rotation = Quaternion.LookRotation(fwd);

                // Log panel to the left, facing user
                if (_logCanvasGO != null)
                {
                    _logCanvasGO.SetActive(true);
                    _logCanvasGO.transform.position = cam.transform.position + fwd * 1.2f - right * 0.65f;
                    _logCanvasGO.transform.rotation = Quaternion.LookRotation(fwd);
                    if (_logText != null) _logText.text = _claudeLog;
                }
            }
        }
        else
        {
            if (_logCanvasGO != null) _logCanvasGO.SetActive(false);
        }

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
                _countdownNumText.SetAllDirty();
                _countdownCmdText.text = "Say what to adjust, or press trigger to skip";
                _countdownCmdText.SetAllDirty();
            }

            _voiceConnector.RecordOneShot(transcript =>
            {
                _waitingForVoice = false;
                SetStatus($"Heard: \"{transcript}\" — now look at target...");
                _orchestrator.SetUserContext(transcript);

                if (_countdownCmdText != null)
                {
                    _countdownCmdText.text = $"\"{transcript}\"";
                    _countdownCmdText.SetAllDirty();
                }

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
        bool skip = OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger) ||
                    OVRInput.GetDown(OVRInput.Button.Three) ||
                    Input.GetMouseButtonDown(0);

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

    // Light grab state
    private GameObject _grabbedLight;
    private float _grabDistance;

    private void GrabNearestLight()
    {
        if (_grabbedLight != null)
        {
            // Already grabbing — move light to right controller position
            Vector3 controllerPos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
            Quaternion controllerRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
            Camera cam = Camera.main;
            if (cam != null)
            {
                Transform trackingSpace = cam.transform.parent;
                if (trackingSpace != null)
                    controllerPos = trackingSpace.TransformPoint(controllerPos);
            }
            _grabbedLight.transform.position = controllerPos + (controllerRot * Vector3.forward) * 0.1f;
            return;
        }

        // Find nearest manual light to right controller
        Vector3 rPos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        Camera c = Camera.main;
        if (c != null)
        {
            Transform ts = c.transform.parent;
            if (ts != null) rPos = ts.TransformPoint(rPos);
        }

        float nearest = 0.3f; // grab radius
        foreach (var go in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (go.name.StartsWith("ARIA_ManualLight"))
            {
                float dist = Vector3.Distance(go.transform.position, rPos);
                if (dist < nearest)
                {
                    nearest = dist;
                    _grabbedLight = go.gameObject;
                }
            }
        }

        if (_grabbedLight != null)
            SetStatus($"Grabbed light — move and release grip to place");
    }

    private void ReleaseLight()
    {
        if (_grabbedLight != null)
        {
            SetStatus($"Light dropped at {_grabbedLight.transform.position:F1}");
            Debug.Log($"[ARIA] Light released at {_grabbedLight.transform.position}");
            _grabbedLight = null;
        }
    }

    private int _selectedIndex = -1;
    private GameObject _selectionHighlight;
    private float _lastXPress;
    private readonly List<GameObject> _selectableObjects = new();

    /// <summary>
    /// Builds a combined list of all selectable objects: spawned objects + light spheres.
    /// Called each time X is pressed to get a fresh list.
    /// </summary>
    private void RefreshSelectableList()
    {
        _selectableObjects.Clear();

        // Add spawned objects (children of spawnRoot/orchestrator)
        Transform root = _orchestrator.transform;
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i).gameObject;
            // Skip internal objects (highlight, UI, etc.)
            if (child.name.StartsWith("ARIA_")) continue;
            _selectableObjects.Add(child);
        }

        // Add light spheres
        foreach (var lightGO in _orchestrator.GetManualLights())
        {
            if (lightGO != null)
                _selectableObjects.Add(lightGO);
        }
    }

    private void CycleOrDeleteSelection()
    {
        // Only allow selection when PTRL is off
        if (_orchestrator.IsPTRLActive)
        {
            SetStatus("Turn PTRL OFF first to select/delete objects.");
            return;
        }

        RefreshSelectableList();

        if (_selectableObjects.Count == 0)
        {
            SetStatus("No objects to select.");
            return;
        }

        // Double-tap X within 0.5s = delete selected
        if (_selectedIndex >= 0 && _selectedIndex < _selectableObjects.Count
            && Time.time - _lastXPress < 0.5f)
        {
            GameObject selected = _selectableObjects[_selectedIndex];
            string objName = selected.name;
            ClearSelectionHighlight();

            // If it's a light sphere, remove from orchestrator's list too
            _orchestrator.RemoveManualLight(selected);

            Destroy(selected);
            _selectedIndex = -1;
            SetStatus($"Deleted: {objName}");
            Debug.Log($"[ARIA] Deleted: {objName}");
            _lastXPress = 0;
            return;
        }

        _lastXPress = Time.time;

        // Single tap = cycle to next object
        _selectedIndex = (_selectedIndex + 1) % _selectableObjects.Count;
        GameObject obj = _selectableObjects[_selectedIndex];
        HighlightObject(obj);
        string type = obj.name.StartsWith("ARIA_ManualLight") ? " (light)" : "";
        SetStatus($"Selected [{_selectedIndex + 1}/{_selectableObjects.Count}]: {obj.name}{type} — double-tap X to delete");
    }

    private void HighlightObject(GameObject obj)
    {
        ClearSelectionHighlight();

        // Create a wireframe cube around the selected object
        Bounds b = ARIAOrchestrator.CalculateMeshBounds(obj);
        _selectionHighlight = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _selectionHighlight.name = "ARIA_SelectionHighlight";
        Destroy(_selectionHighlight.GetComponent<Collider>());
        _selectionHighlight.transform.position = b.center;
        _selectionHighlight.transform.localScale = b.size + Vector3.one * 0.05f;

        // Orange wireframe material
        var mat = new Material(Shader.Find("UI/Default"));
        mat.color = new Color(1f, 0.6f, 0f, 0.3f);
        _selectionHighlight.GetComponent<Renderer>().material = mat;
    }

    private void ClearSelectionHighlight()
    {
        if (_selectionHighlight != null)
        {
            Destroy(_selectionHighlight);
            _selectionHighlight = null;
        }
    }

    private bool _effectMeshHidden;

    private void ToggleEffectMesh()
    {
        _effectMeshHidden = !_effectMeshHidden;

        var effectMesh = FindFirstObjectByType<Meta.XR.MRUtilityKit.EffectMesh>();
        if (effectMesh != null)
        {
            foreach (Transform child in effectMesh.transform)
                child.gameObject.SetActive(!_effectMeshHidden);
            effectMesh.HideMesh = _effectMeshHidden;
        }

        SetStatus(_effectMeshHidden ? "Room wireframe OFF" : "Room wireframe ON");
    }

    private void SetStatus(string s)
    {
        _status = s;
        if (_statusText != null)
        {
            _statusText.text = "Status: " + s;
            _statusText.SetAllDirty();
        }
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
            _countdownCmdText.SetAllDirty();
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
        // Runtime check — true when headset is connected (Quest Link or APK)
        return OVRManager.isHmdPresent;
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
        if (GUI.Button(new Rect(inner, cy, panelW - padding * 2f, lineH + 4f), "PTRL: Toggle"))
            _orchestrator.TogglePTRL();
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
