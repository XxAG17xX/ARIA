// ARIADebugUI.cs
// Debug/testing UI for ARIA — works in both editor (IMGUI) and Quest headset (world-space Canvas).
//
// Quest: world-space Canvas with big buttons, positioned in front of user at launch.
//   Controller laser pointer clicks buttons. Countdown overlay for gaze-directed placement.
//
// Editor: IMGUI text field + buttons (same as before).

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Meta.XR.MRUtilityKit;

[RequireComponent(typeof(ARIAOrchestrator))]
public class ARIADebugUI : MonoBehaviour
{
    [Header("Debug UI")]
    [SerializeField] private bool showDebugUI = true;
    [SerializeField] private int  fontSize    = 16;

    private ARIAOrchestrator  _orchestrator;
    private VoiceSDKConnector _voiceConnector;
    private string            _status    = "Ready — look where you want objects, then tap a button.";
    private string            _partialTranscript = "";

    // Countdown state
    private bool   _countdownActive;
    private float  _countdownEnd;
    private System.Action _pendingAction;
    private const float CountdownSeconds = 3f;

    // Room scan removed — lighting now uses PTRL + single-frame analysis

    // VR Canvas UI elements (Quest)
    private Canvas     _vrCanvas;
    private GameObject _canvasGO;
    private bool       _menuVisible;
    private GameObject _mainPanel;
    private GameObject _countdownPanel;
    private CanvasGroup _countdownGroup; // use alpha instead of SetActive to keep mesh alive
    private Text       _statusText;
    private Text       _transcriptText;
    private Text       _countdownNumText;
    private Text       _countdownCmdText;

    // PTRL toggle button reference
    private GameObject _ptrlButton;
    private Text _ptrlButtonText;

    // Shadow mode toggle button reference
    private Text _shadowModeButtonText;

    // Activity log panel (shows Claude calls, light confirms, spawns, PTRL, errors)
    private GameObject _logCanvasGO;
    private Text       _logText;
    private float      _logScrollOffset; // pixels scrolled down (right thumbstick)
    private bool       _logScrollDirty;
    private static string _claudeLog = "No activity yet.";
    public static void AppendClaudeLog(string msg)
    {
        string timestamp = System.DateTime.Now.ToString("HH:mm:ss");
        _claudeLog = $"[{timestamp}] {msg}\n\n{_claudeLog}";
        if (_claudeLog.Length > 24000) _claudeLog = _claudeLog.Substring(0, 24000);
    }


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

        // Scroll viewport with clipping mask — text stays inside the panel
        var viewport = new GameObject("LogViewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(panel.transform, false);
        var vpRT = viewport.GetComponent<RectTransform>();
        vpRT.anchoredPosition = new Vector2(0, -20);
        vpRT.sizeDelta = new Vector2(480, 600);

        // Log text inside viewport (much taller than viewport — scrollable)
        _logText = MakeLabel(viewport.transform, "LogContent", _claudeLog,
            new Vector2(0, 0), new Vector2(480, 4000), 12, FontStyle.Normal, Color.white);
        _logText.alignment = TextAnchor.UpperLeft;
        // Anchor to top of viewport so text starts at top and extends down
        var logRT = _logText.GetComponent<RectTransform>();
        logRT.anchorMin = new Vector2(0.5f, 1f);
        logRT.anchorMax = new Vector2(0.5f, 1f);
        logRT.pivot = new Vector2(0.5f, 1f);
        logRT.anchoredPosition = Vector2.zero;

        _logCanvasGO.SetActive(false);
    }

    private static void SuppressBoundary()
    {
        try
        {
            // Try multiple approaches to suppress boundary for MR passthrough
            // 1. Legacy (deprecated but sometimes still works)
            OVRManager.boundary.SetVisible(false);
        }
        catch (System.Exception) { /* Deprecated in OpenXR */ }

        try
        {
            // 2. Set boundary to not be visible via OVRManager instance
            if (OVRManager.instance != null)
            {
                // Request no boundary rendering — MR passthrough app shows real world
                var t = typeof(OVRManager);
                var prop = t.GetProperty("shouldBoundaryVisibilityBeSuppressed");
                if (prop != null) prop.SetValue(OVRManager.instance, true);
            }
        }
        catch (System.Exception) { /* Property may not exist in this SDK version */ }
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
            // Right grip = grab/release nearest light OR spawned object
            if (OVRInput.Get(OVRInput.Button.SecondaryHandTrigger))
            {
                GrabNearest();
                // While holding an object, thumbstick rotates it
                if (_grabbedObject != null)
                    RotateGrabbedObject();
            }
            else
                ReleaseGrabbed();
        }

        // X button: cycle selection / delete (only when menu is NOT visible)
        if (IsRunningOnQuest() && !_menuVisible)
        {
            if (OVRInput.GetDown(OVRInput.Button.Three)) // X button on left controller
                CycleOrDeleteSelection();
        }

        // Voice recording timeout check
        CheckVoiceTimeout();

        // Log panel: keep text fresh + right thumbstick scrolling
        if (_menuVisible && _logCanvasGO != null && _logCanvasGO.activeSelf && _logText != null)
        {
            // Right thumbstick Y = scroll log (when NOT grabbing an object)
            if (_grabbedObject == null && _grabbedLight == null)
            {
                float scrollInput = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick).y;
                if (Mathf.Abs(scrollInput) > 0.2f)
                {
                    _logScrollOffset += scrollInput * Time.deltaTime * 800f;
                    _logScrollOffset = Mathf.Max(0f, _logScrollOffset);
                    _logScrollDirty = true;
                }
            }

            // Update log text when content changes or scroll moves
            if (_logText.text != _claudeLog || _logScrollDirty)
            {
                _logScrollDirty = false;
                // Recreate from scratch with correct anchoring (not RemakeLabel which preserves old scroll position)
                RecreateLogText(_logScrollOffset);
            }
        }

        // Crosshair + pointer logic:
        // - Menu open + main panel visible → UpdateGazePointer (buttons hover/click)
        // - Menu open + countdown showing → UpdateAlwaysOnCrosshair (crosshair follows gaze for placement)
        // - Menu closed → UpdateAlwaysOnCrosshair (EnvironmentRaycast for real-world surface)
        if (IsRunningOnQuest())
        {
            bool countdownShowing = _countdownActive || _waitingForVoice;
            if (_menuVisible && _vrCanvas != null && !countdownShowing)
                UpdateGazePointer();
            else
                UpdateAlwaysOnCrosshair();
        }

        if (!_countdownActive) return;

        float remaining = _countdownEnd - Time.time;

        // Update VR countdown display — recreate label to force Quest canvas rebuild
        if (_countdownNumText != null)
        {
            int display = Mathf.CeilToInt(remaining);
            if (display < 1) display = 1;
            string numStr = display.ToString();
            if (_countdownNumText.text != numStr)
                _countdownNumText = RemakeLabel(_countdownNumText, numStr);
        }

        if (remaining <= 0f)
        {
            _countdownActive = false;
            ShowCountdownPanel(false);

            // Auto-hide menu so spawned objects aren't hidden behind the UI panel
            if (_menuVisible) ToggleMenu();

            if (_pendingAction != null)
            {
                SetStatus("Processing...");
                _pendingAction.Invoke();
                _pendingAction = null;
            }
        }
    }

    // Last gaze hit state — used by placement engine and Claude context
    private Vector3 _lastGazeHitPoint;
    private Vector3 _lastGazeHitNormal = Vector3.up;
    private bool _lastGazeIsOnClutter;
    private MRUKAnchor _lastGazeAnchor;
    private bool _lastGazeHitValid;

    /// <summary>Last gaze raycast hit point via EnvironmentRaycastManager (live depth sensor).</summary>
    public Vector3 LastGazeHitPoint => _lastGazeHitPoint;
    public Vector3 LastGazeHitNormal => _lastGazeHitNormal;
    public bool LastGazeIsOnClutter => _lastGazeIsOnClutter;
    public MRUKAnchor LastGazeAnchor => _lastGazeAnchor;
    public bool LastGazeHitValid => _lastGazeHitValid;

    // Cached reference to EnvironmentRaycastManager (found at runtime)
    private Meta.XR.EnvironmentRaycastManager _envRaycastMgr;
    private bool _envRaycastSearched;

    private Meta.XR.EnvironmentRaycastManager GetEnvRaycastManager()
    {
        if (_envRaycastMgr == null && !_envRaycastSearched)
        {
            _envRaycastMgr = FindFirstObjectByType<Meta.XR.EnvironmentRaycastManager>();
            if (_envRaycastMgr == null)
            {
                // Auto-create it on the ARIA_Manager GameObject
                if (Meta.XR.EnvironmentRaycastManager.IsSupported)
                {
                    _envRaycastMgr = gameObject.AddComponent<Meta.XR.EnvironmentRaycastManager>();
                    Debug.Log("[ARIA] EnvironmentRaycastManager auto-created on ARIA_Manager.");
                }
                else
                {
                    Debug.LogWarning("[ARIA] EnvironmentRaycastManager not supported (Quest 3+ only).");
                }
            }
            _envRaycastSearched = true;
        }
        return _envRaycastMgr;
    }

    /// <summary>
    /// Always-on crosshair — uses EnvironmentRaycastManager (live depth sensor) for precise
    /// surface detection including clutter objects. Falls back to Physics.Raycast in editor.
    /// </summary>
    private void UpdateAlwaysOnCrosshair()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        if (_reticle == null) CreateReticle(cam);

        Ray gazeRay = new Ray(cam.transform.position, cam.transform.forward);
        bool hit = false;
        Vector3 hitPt = Vector3.zero;
        Vector3 hitNorm = Vector3.up;

        // Primary: EnvironmentRaycastManager (live depth sensor — sees actual physical surfaces)
        var envMgr = GetEnvRaycastManager();
        if (envMgr != null)
        {
            if (envMgr.Raycast(gazeRay, out var envHit, 10f))
            {
                hitPt = envHit.point;
                hitNorm = envHit.normal;
                hit = true;
            }
        }

        // Fallback: Physics.Raycast against colliders (editor mode, or if depth not ready)
        if (!hit && Physics.Raycast(gazeRay, out RaycastHit physHit, 10f))
        {
            hitPt = physHit.point;
            hitNorm = physHit.normal;
            hit = true;
        }

        if (hit)
        {
            _lastGazeHitPoint = hitPt;
            _lastGazeHitNormal = hitNorm;
            _lastGazeHitValid = true;

            // Identify anchor and clutter state via placement engine
            var pe = _orchestrator.GetPlacementEngine();
            if (pe != null)
            {
                _lastGazeAnchor = pe.IdentifyAnchorAtPoint(hitPt);
                _lastGazeIsOnClutter = _lastGazeAnchor != null && pe.IsPointOnClutter(hitPt, _lastGazeAnchor);
            }

            _reticle.transform.position = hitPt + hitNorm * 0.002f;
            _reticle.transform.rotation = Quaternion.LookRotation(-hitNorm);

            // Color: yellow on clutter, white on clear surface
            var mat = _reticle.GetComponent<Renderer>()?.material;
            if (mat != null)
                mat.color = _lastGazeIsOnClutter
                    ? new Color(1f, 0.9f, 0.2f, 0.9f)  // yellow = clutter
                    : new Color(1f, 1f, 1f, 0.8f);       // white = clear surface
        }
        else
        {
            // Nothing hit — park reticle 2m ahead
            _reticle.transform.position = cam.transform.position + cam.transform.forward * 2f;
            _reticle.transform.rotation = Quaternion.LookRotation(cam.transform.forward);
            _lastGazeIsOnClutter = false;
            _lastGazeAnchor = null;
            _lastGazeHitValid = false;
        }

        _reticle.SetActive(true);
    }

    private void UpdateGazePointer()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        if (_reticle == null) CreateReticle(cam);

        // When menu is open: Physics.Raycast lands on Canvas buttons for hover/click.
        // Reticle moves onto buttons so user can see what they're pointing at.
        Ray gazeRay = new Ray(cam.transform.position, cam.transform.forward);
        Button hitBtn = null;

        if (Physics.Raycast(gazeRay, out RaycastHit hit, 5f))
        {
            hitBtn = hit.collider.GetComponentInParent<Button>();
            _reticle.transform.position = hit.point - gazeRay.direction * 0.001f;
            _reticle.transform.rotation = Quaternion.LookRotation(-hit.normal);
            // White reticle on menu
            var mat = _reticle.GetComponent<Renderer>()?.material;
            if (mat != null) mat.color = new Color(1f, 1f, 1f, 0.9f);
        }
        else
        {
            _reticle.transform.position = cam.transform.position + cam.transform.forward * 2f;
            _reticle.transform.rotation = Quaternion.LookRotation(cam.transform.forward);
        }
        _reticle.SetActive(true);

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
            Vector2.zero, new Vector2(600, 1000), new Color(0f, 0f, 0f, 0.75f));

        float y = 445f; // start from top

        MakeLabel(_mainPanel.transform, "Title", "ARIA",
            new Vector2(0, y), new Vector2(560, 36), 26, FontStyle.Bold, Color.white);
        y -= 50f;

        // Voice command — records speech, captures passthrough, sends to Claude + 3D gen pipeline
        MakeButton(_mainPanel.transform, "BtnSpeak", "Speak to ARIA (voice command)",
            new Vector2(0, y), new Vector2(560, 60),
            () => StartVoiceCommand());
        y -= 70f;

        // Lighting row
        _ptrlButton = MakeButtonWithRef(_mainPanel.transform, "BtnPTRL", "PTRL: OFF",
            new Vector2(0, y), new Vector2(560, 50),
            () => {
                bool on = _orchestrator.TogglePTRL();
                if (_ptrlButtonText != null)
                    _ptrlButtonText.text = on ? "PTRL: ON" : "PTRL: OFF";
            });

        // Shadow mode toggle — below PTRL button
        MakeButton(_mainPanel.transform, "BtnShadowMode", "Shadows: Directional",
            new Vector2(0, y - 50), new Vector2(560, 44),
            () => {
                var mode = _orchestrator.CycleShadowMode();
                if (_shadowModeButtonText != null)
                    _shadowModeButtonText.text = mode == ShadowMode.Directional
                        ? "Shadows: Directional" : "Shadows: Point Light";
            });
        {
            var btn = _mainPanel.transform.Find("BtnShadowMode");
            if (btn != null)
                _shadowModeButtonText = btn.GetComponentInChildren<Text>();
        }
        y -= 108f; // PTRL (50) + gap + shadow mode (44) + gap

        // ── DEMO SPAWN — pre-bundled GLBs, zero API calls ──────────────
        MakeLabel(_mainPanel.transform, "DemoTitle", "DEMO (instant, no credits):",
            new Vector2(0, y), new Vector2(560, 30), 18, FontStyle.Bold,
            new Color(0.4f, 1f, 0.6f));
        y -= 40f;

        MakeButton(_mainPanel.transform, "DemoBed", "Spawn Bed",
            new Vector2(-145, y), new Vector2(270, 48),
            () => StartCountdownForAction("Bed at crosshair",
                () => { _orchestrator.SetUserContext("spawn a bed"); _orchestrator.SpawnBundledGlb("bed.glb", "FLOOR", 0.5f, "bed", 1.4f, 2.0f); }));
        MakeButton(_mainPanel.transform, "DemoLamp", "Spawn Lamp",
            new Vector2(145, y), new Vector2(270, 48),
            () => StartCountdownForAction("Lamp at crosshair",
                () => { _orchestrator.SetUserContext("spawn a lamp"); _orchestrator.SpawnBundledGlb("lamp.glb", "FLOOR", 1.5f, "lamp"); }));
        y -= 58f;

        MakeButton(_mainPanel.transform, "DemoWall", "Spawn Wall Art",
            new Vector2(0, y), new Vector2(560, 48),
            () => StartCountdownForAction("Wall art at crosshair",
                () => { _orchestrator.SetUserContext("hang a painting on the wall"); _orchestrator.SpawnBundledGlb("wall_art.glb", "WALL_FACE", 0.6f, "wall_art", 0.8f, 0.05f); }));
        y -= 58f;

        // Tripo quality toggle
        MakeButton(_mainPanel.transform, "BtnTripoQ", "3D Quality: Standard",
            new Vector2(0, y), new Vector2(560, 36),
            () => {
                bool hq = _orchestrator.ToggleTripoQuality();
                var btn = _mainPanel.transform.Find("BtnTripoQ");
                if (btn != null)
                {
                    var txt = btn.GetComponentInChildren<Text>();
                    if (txt != null) txt.text = hq
                        ? "3D Quality: HIGH"
                        : "3D Quality: Standard";
                }
                if (hq)
                    AppendClaudeLog("3D QUALITY → HIGH\n" +
                        "  Model: Tripo v3.1-20260211\n" +
                        "  Polygons: 30,000 faces\n" +
                        "  Texture: detailed (high-res)\n" +
                        "  Geometry: detailed (Ultra Mode)\n" +
                        "  PBR: ON (metallic/roughness maps)\n" +
                        "  ~2-3x credits per object");
                else
                    AppendClaudeLog("3D QUALITY → Standard\n" +
                        "  Model: Tripo v2.5-20250123\n" +
                        "  Polygons: 10,000 faces\n" +
                        "  Texture: standard\n" +
                        "  Geometry: standard\n" +
                        "  PBR: OFF\n" +
                        "  Lowest credit cost");
            });
        y -= 42f;

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

        MakeButton(_mainPanel.transform, "BtnAnchors", "Toggle Anchors",
            new Vector2(-145, y), new Vector2(270, 44),
            () => ToggleAnchorLabels());
        MakeButton(_mainPanel.transform, "BtnGlobalMesh", "Toggle Global Mesh",
            new Vector2(145, y), new Vector2(270, 44),
            () => ToggleGlobalMesh());
        y -= 52f;

        // Transcript — taller to show full voice input
        _transcriptText = MakeLabel(_mainPanel.transform, "Transcript", "",
            new Vector2(0, y), new Vector2(560, 60), 14, FontStyle.Italic, Color.grey);
        y -= 64f;

        // Status
        _statusText = MakeLabel(_mainPanel.transform, "Status",
            "Status: " + _status,
            new Vector2(0, y), new Vector2(560, 50), 17, FontStyle.Normal, Color.white);

        // ── Countdown overlay ────────────────────────────────────────────────
        // Use CanvasGroup alpha instead of SetActive so the Text mesh stays
        // initialized and .text updates work normally on Quest.
        _countdownPanel = MakePanel(canvasGO.transform, "CountdownPanel",
            Vector2.zero, new Vector2(600, 950), new Color(0f, 0f, 0f, 0.9f));
        _countdownGroup = _countdownPanel.AddComponent<CanvasGroup>();
        _countdownGroup.alpha = 0f;
        _countdownGroup.blocksRaycasts = false;
        _countdownGroup.interactable = false;

        _countdownNumText = MakeLabel(_countdownPanel.transform, "CNum", "3",
            new Vector2(0, 60), new Vector2(300, 180), 120, FontStyle.Bold,
            new Color(1f, 0.85f, 0.2f));

        MakeLabel(_countdownPanel.transform, "CInstr",
            "Look at where you want objects placed...",
            new Vector2(0, -60), new Vector2(560, 40), 22, FontStyle.Normal, Color.white);

        _countdownCmdText = MakeLabel(_countdownPanel.transform, "CCmd", "",
            new Vector2(0, -120), new Vector2(560, 80), 14, FontStyle.Italic, Color.grey);

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

    /// <summary>
    /// Destroy and recreate a label to force Quest world-space canvas to render new text.
    /// Quest doesn't rebuild canvas mesh on .text changes, so we create a fresh GameObject.
    /// </summary>
    private static Text RemakeLabel(Text existing, string newText)
    {
        if (existing == null) return null;
        var go = existing.gameObject;
        var parent = go.transform.parent;
        var rt = go.GetComponent<RectTransform>();
        var pos = rt.anchoredPosition;
        var size = rt.sizeDelta;
        int fSize = existing.fontSize;
        var style = existing.fontStyle;
        var color = existing.color;
        string name = go.name;
        Destroy(go);
        return MakeLabel(parent, name, newText, pos, size, fSize, style, color);
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
                _canvasGO.transform.position = cam.transform.position + fwd * 1.0f;
                _canvasGO.transform.rotation = Quaternion.LookRotation(fwd);

                // Log panel to the left, angled toward user for readability
                if (_logCanvasGO != null)
                {
                    _logCanvasGO.SetActive(true);
                    Vector3 logPos = cam.transform.position + fwd * 0.8f - right * 0.55f;
                    _logCanvasGO.transform.position = logPos;
                    // Angle inward toward the user (30° rotation toward center)
                    Vector3 lookDir = (cam.transform.position - logPos).normalized;
                    lookDir.y = 0;
                    _logCanvasGO.transform.rotation = Quaternion.LookRotation(-lookDir);
                    if (_logText != null)
                    {
                        _logScrollOffset = 0f; // reset scroll to top when menu opens
                        RecreateLogText(0f);
                    }
                }
            }
        }
        else
        {
            if (_logCanvasGO != null) _logCanvasGO.SetActive(false);

            // If adjustment is pending and user just closed UI, launch capture now
            if (_adjustmentPending)
            {
                _adjustmentPending = false;
                ShowCountdownPanel(false);
                SetStatus("Capturing scene for Claude...");
                ARIADebugUI.AppendClaudeLog("UI hidden — capturing annotated view for adjustment...");
                _orchestrator.AdjustLastSpawnWithClaude();
            }
        }

        // Reticle stays always-on (UpdateAlwaysOnCrosshair handles it)
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
    private bool _voiceIsForNewCommand; // true = voice command pipeline, false = adjustment

    private void StartVoiceCommand()
    {
        if (_voiceConnector != null && !_waitingForVoice)
        {
            SetStatus("Speak now... (7s timeout, or press trigger to skip)");
            _waitingForVoice = true;
            _voiceIsForNewCommand = true;
            _voiceTimeout = Time.time + 7f;

            if (_countdownPanel != null)
            {
                _countdownNumText = RemakeLabel(_countdownNumText, "Speak");
                _countdownCmdText = RemakeLabel(_countdownCmdText, "Describe what you want & where");
                ShowCountdownPanel(true);
            }

            _voiceConnector.RecordOneShot(transcript =>
            {
                _waitingForVoice = false;
                SetStatus($"Heard: \"{transcript}\" — capturing room...");

                if (_countdownCmdText != null)
                    _countdownCmdText = RemakeLabel(_countdownCmdText, $"\"{transcript}\"");

                // Brief countdown so user can look at target area for the annotated capture
                StartCountdownForAction($"Spawning: {transcript}",
                    () => _orchestrator.ProcessVoiceCommand(transcript));
            });
        }
        else
        {
            // No voice SDK — fall back to text input (editor only)
            SetStatus("No voice SDK — use text input.");
        }
    }

    private void StartVoiceAdjust()
    {
        if (_voiceConnector != null && !_waitingForVoice)
        {
            // Step 1: Record voice — trigger finishes recording (doesn't skip)
            SetStatus("Speak what to adjust... press trigger when done");
            _waitingForVoice = true;
            _voiceIsForNewCommand = false;
            _voiceTimeout = Time.time + 10f;

            if (_countdownPanel != null)
            {
                _countdownNumText = RemakeLabel(_countdownNumText, "Speak");
                _countdownCmdText = RemakeLabel(_countdownCmdText, "Describe the change, press trigger when done");
                ShowCountdownPanel(true);
            }

            _voiceConnector.RecordOneShot(transcript =>
            {
                _waitingForVoice = false;
                _orchestrator.SetUserContext(transcript);

                if (_countdownCmdText != null)
                    _countdownCmdText = RemakeLabel(_countdownCmdText, $"\"{transcript}\"\nClose UI (Y), then look at target");
                SetStatus($"Heard: \"{transcript}\" — close UI, look at target, system will capture");

                // Don't start countdown yet — wait for user to close UI
                // The capture happens in LaunchAdjustmentAfterUIHidden
                _adjustmentPending = true;
            });
        }
        else
        {
            // No voice SDK — proceed with visual context only
            _orchestrator.SetUserContext("");
            _adjustmentPending = true;
            SetStatus("Close UI (Y), look at target — system will capture");
        }
    }

    private bool _adjustmentPending;

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

            if (_voiceIsForNewCommand)
            {
                ShowCountdownPanel(false);
                SetStatus("No voice detected — try again.");
            }
            else
            {
                // Adjustment: trigger = done talking, proceed to capture
                // If no transcript came through callback, proceed with visual context only
                if (_countdownCmdText != null)
                    _countdownCmdText = RemakeLabel(_countdownCmdText, "Close UI (Y), look at target");
                SetStatus("Close UI (Y), look at what to adjust — system will capture");
                _adjustmentPending = true;
            }
        }
    }

    // Light grab state
    private GameObject _grabbedLight;
    private ARIAInteractable _grabbedObject;
    private Vector3 _grabOffset; // offset from controller to object at grab time

    /// <summary>Right grip held: grab nearest light OR spawned object, move with controller.</summary>
    private void GrabNearest()
    {
        // Already holding something — move it with controller
        if (_grabbedLight != null || _grabbedObject != null)
        {
            Vector3 controllerPos = GetRightControllerWorldPos();
            Quaternion controllerRot = GetRightControllerWorldRot();
            Vector3 targetPos = controllerPos + (controllerRot * Vector3.forward) * 0.15f;

            if (_grabbedLight != null)
            {
                _grabbedLight.transform.position = targetPos;
                if (_orchestrator.IsPTRLActive)
                    foreach (var r in _grabbedLight.GetComponentsInChildren<Renderer>())
                        r.enabled = true;
            }
            else if (_grabbedObject != null)
            {
                _grabbedObject.transform.position = targetPos + _grabOffset;
            }
            return;
        }

        // Nothing grabbed yet — find nearest grabbable thing
        Vector3 rPos = GetRightControllerWorldPos();
        float nearestDist = 0.6f; // grab radius
        GameObject nearestLight = null;
        ARIAInteractable nearestObj = null;

        // Check lights
        foreach (var go in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (go.name.StartsWith("ARIA_ManualLight"))
            {
                float dist = Vector3.Distance(go.transform.position, rPos);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestLight = go.gameObject;
                    nearestObj = null;
                }
            }
        }

        // Check spawned objects (closer ones win)
        foreach (var interactable in FindObjectsByType<ARIAInteractable>(FindObjectsSortMode.None))
        {
            float dist = Vector3.Distance(interactable.transform.position, rPos);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestObj = interactable;
                nearestLight = null;
            }
        }

        if (nearestLight != null)
        {
            _grabbedLight = nearestLight;
            SetStatus("Grabbed light — move to reposition");
        }
        else if (nearestObj != null)
        {
            _grabbedObject = nearestObj;
            _grabOffset = nearestObj.transform.position - rPos; // preserve offset so object doesn't jump
            nearestObj.StartGrab();
            SetStatus($"Grabbed {nearestObj.gameObject.name}");
        }
    }

    /// <summary>Right grip released: drop whatever we're holding.</summary>
    private void ReleaseGrabbed()
    {
        if (_grabbedLight != null)
        {
            if (_orchestrator.IsPTRLActive)
                foreach (var r in _grabbedLight.GetComponentsInChildren<Renderer>())
                    r.enabled = false;

            SetStatus($"Light dropped at {_grabbedLight.transform.position:F1}");
            Debug.Log($"[ARIA] Light released at {_grabbedLight.transform.position}");
            _grabbedLight = null;
        }

        if (_grabbedObject != null)
        {
            SetStatus($"Dropped {_grabbedObject.gameObject.name}");
            _grabbedObject.EndGrab(); // triggers gravity or wall snap
            _grabbedObject = null;
        }
    }

    /// <summary>
    /// While holding an object with right grip, use thumbsticks to rotate it.
    /// Right thumbstick X: turn left/right (Y axis). Left thumbstick Y: tilt forward/back (X axis).
    /// This lets the user straighten a fallen lamp or tilt a bed before releasing.
    /// </summary>
    private void RotateGrabbedObject()
    {
        if (_grabbedObject == null) return;

        float rotSpeed = 120f * Time.deltaTime; // degrees per second

        // Right thumbstick X → rotate around Y axis (turn left/right)
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        if (Mathf.Abs(rightStick.x) > 0.15f)
            _grabbedObject.transform.Rotate(Vector3.up, rightStick.x * rotSpeed, Space.World);

        // Right thumbstick Y → rotate around X axis (tilt forward/back)
        if (Mathf.Abs(rightStick.y) > 0.15f)
            _grabbedObject.transform.Rotate(Vector3.right, -rightStick.y * rotSpeed, Space.World);

        // A button: reset rotation to upright (quick fix)
        if (OVRInput.GetDown(OVRInput.Button.One))
        {
            _grabbedObject.transform.rotation = Quaternion.identity;
            SetStatus("Rotation reset to upright");
        }

        // Left thumbstick Y: scale up/down (forward = bigger, backward = smaller)
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        if (Mathf.Abs(leftStick.y) > 0.15f)
        {
            float scaleSpeed = 1f + leftStick.y * Time.deltaTime * 2f; // smooth proportional scaling
            _grabbedObject.transform.localScale *= scaleSpeed;
            // Clamp: never below 5% or above 5x of original
            Vector3 orig = _grabbedObject.originalScale;
            Vector3 cur = _grabbedObject.transform.localScale;
            cur.x = Mathf.Clamp(cur.x, orig.x * 0.05f, orig.x * 5f);
            cur.y = Mathf.Clamp(cur.y, orig.y * 0.05f, orig.y * 5f);
            cur.z = Mathf.Clamp(cur.z, orig.z * 0.05f, orig.z * 5f);
            _grabbedObject.transform.localScale = cur;
        }
    }

    private Vector3 GetRightControllerWorldPos()
    {
        Vector3 pos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        Camera cam = Camera.main;
        if (cam != null)
        {
            Transform ts = cam.transform.parent;
            if (ts != null) pos = ts.TransformPoint(pos);
        }
        return pos;
    }

    private Quaternion GetRightControllerWorldRot()
    {
        Quaternion rot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        Camera cam = Camera.main;
        if (cam != null)
        {
            Transform ts = cam.transform.parent;
            if (ts != null) rot = ts.rotation * rot;
        }
        return rot;
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

    /// <summary>
    /// Recreates the log text with correct top anchoring from scratch.
    /// Does NOT use RemakeLabel (which preserves old position and causes scroll bugs).
    /// </summary>
    private void RecreateLogText(float scrollOffset)
    {
        if (_logText == null) return;
        var parent = _logText.transform.parent; // the viewport
        var go = _logText.gameObject;
        Destroy(go);

        // Create fresh label with explicit top anchoring
        _logText = MakeLabel(parent, "LogContent", _claudeLog,
            new Vector2(0, 0), new Vector2(480, 4000), 12, FontStyle.Normal, Color.white);
        _logText.alignment = TextAnchor.UpperLeft;

        var rt = _logText.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, scrollOffset);
    }

    private bool _effectMeshHidden;
    public bool IsEffectMeshHidden => _effectMeshHidden;

    private void ToggleEffectMesh()
    {
        _effectMeshHidden = !_effectMeshHidden;

        var effectMesh = FindFirstObjectByType<Meta.XR.MRUtilityKit.EffectMesh>();
        if (effectMesh != null)
        {
            // Use EffectMesh's own API with a LabelFilter that EXCLUDES Global Mesh.
            // ~GLOBAL_MESH = everything EXCEPT GLOBAL_MESH.
            // EffectMesh renderers are parented to each anchor (not to EffectMesh GO),
            // so GetComponentsInChildren won't find them — must use the official API.
            var filter = new LabelFilter(~MRUKAnchor.SceneLabels.GLOBAL_MESH);
            effectMesh.ToggleEffectMeshVisibility(!_effectMeshHidden, filter);
        }

        SetStatus(_effectMeshHidden ? "Room wireframe OFF" : "Room wireframe ON");
    }

    private bool _globalMeshVisible;
    public bool IsGlobalMeshVisible => _globalMeshVisible;
    private Material _globalMeshWireframeMat;

    private void ToggleGlobalMesh()
    {
        _globalMeshVisible = !_globalMeshVisible;

        var room = MRUK.Instance?.GetCurrentRoom();
        var globalMeshAnchor = room?.GetGlobalMeshAnchor();

        if (globalMeshAnchor != null)
        {
            // Create wireframe material using ARIA's GlobalMeshWireframe shader
            // (URP port of Meta's Phanto WireframeShader — reads barycentric coords from vertex colors)
            if (_globalMeshWireframeMat == null)
            {
                var shader = Shader.Find("ARIA/GlobalMeshWireframe");
                if (shader != null)
                {
                    _globalMeshWireframeMat = new Material(shader);
                    _globalMeshWireframeMat.SetColor("_WireframeColor", new Color(0f, 1f, 0.3f, 0.7f)); // green wireframe
                    _globalMeshWireframeMat.SetColor("_Color", new Color(0f, 0f, 0f, 0f));               // transparent fill
                    _globalMeshWireframeMat.SetFloat("_DistanceMultiplier", 2f);
                    Debug.Log("[ARIA] Global Mesh wireframe material created (ARIA/GlobalMeshWireframe shader)");
                }
                else
                {
                    // Fallback: simple transparent material
                    shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
                    if (shader != null)
                    {
                        _globalMeshWireframeMat = new Material(shader);
                        _globalMeshWireframeMat.color = new Color(0f, 1f, 0.3f, 0.12f);
                        _globalMeshWireframeMat.renderQueue = 3000;
                    }
                    Debug.LogWarning("[ARIA] ARIA/GlobalMeshWireframe shader not found — using transparent fallback");
                }
            }

            foreach (var r in globalMeshAnchor.GetComponentsInChildren<MeshRenderer>())
            {
                r.enabled = _globalMeshVisible;
                if (_globalMeshVisible && _globalMeshWireframeMat != null)
                    r.material = _globalMeshWireframeMat;
            }

            int triCount = 0;
            foreach (var mf in globalMeshAnchor.GetComponentsInChildren<MeshFilter>())
                if (mf.sharedMesh != null) triCount += mf.sharedMesh.triangles.Length / 3;

            SetStatus(_globalMeshVisible
                ? $"Global Mesh ON ({triCount} triangles)"
                : "Global Mesh OFF");
            ARIADebugUI.AppendClaudeLog(_globalMeshVisible
                ? $"Global Mesh VISIBLE — {triCount} triangles"
                : "Global Mesh hidden");
        }
        else
        {
            SetStatus("Global Mesh not found — room scan may not include it");
            Debug.LogWarning("[ARIA] No GlobalMeshAnchor found in current room");
        }
    }

    private bool _anchorsVisible;
    private readonly List<GameObject> _anchorLabelObjects = new();

    private void ToggleAnchorLabels()
    {
        _anchorsVisible = !_anchorsVisible;

        if (!_anchorsVisible)
        {
            foreach (var go in _anchorLabelObjects)
                if (go != null) Destroy(go);
            _anchorLabelObjects.Clear();
            SetStatus("Anchor labels OFF");
            return;
        }

        // Build anchor registry if not already populated
        _orchestrator.GetType().GetMethod("SerializeMRUKData",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(_orchestrator, null);

        var room = Meta.XR.MRUtilityKit.MRUK.Instance?.GetCurrentRoom();
        if (room == null) { SetStatus("No room data"); _anchorsVisible = false; return; }

        Camera cam = Camera.main;
        var counters = new Dictionary<string, int>();

        foreach (var anchor in room.Anchors)
        {
            string rawLabel = anchor.Label.ToString();
            string prefix = rawLabel switch
            {
                "WALL_FACE" => "WALL", "FLOOR" => "FLOOR", "CEILING" => "CEIL",
                "TABLE" => "TABLE", "COUCH" => "COUCH", "DOOR_FRAME" => "DOOR",
                "WINDOW_FRAME" => "WIN", _ => "OTHER"
            };
            counters.TryGetValue(prefix, out int idx);
            string id = $"{prefix}_{idx}";
            counters[prefix] = idx + 1;

            Vector3 pos = anchor.transform.position;
            float dist = cam != null ? Vector3.Distance(cam.transform.position, pos) : 2f;
            float charSize = Mathf.Clamp(dist * 0.012f, 0.015f, 0.04f);

            // Shadow
            var shadow = new GameObject($"AnchorLabel_Shadow_{id}");
            shadow.transform.position = pos + (cam != null ? (cam.transform.position - pos).normalized * 0.048f : Vector3.forward * 0.048f);
            var tmS = shadow.AddComponent<TextMesh>();
            tmS.text = id; tmS.characterSize = charSize; tmS.fontSize = 36;
            tmS.anchor = TextAnchor.MiddleCenter; tmS.color = Color.black;
            tmS.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            shadow.GetComponent<MeshRenderer>().material = tmS.font.material;
            shadow.AddComponent<BillboardFaceCamera>();
            _anchorLabelObjects.Add(shadow);

            // Label
            var label = new GameObject($"AnchorLabel_{id}");
            label.transform.position = pos + (cam != null ? (cam.transform.position - pos).normalized * 0.05f : Vector3.forward * 0.05f);
            var tm = label.AddComponent<TextMesh>();
            tm.text = id; tm.characterSize = charSize; tm.fontSize = 36;
            tm.anchor = TextAnchor.MiddleCenter; tm.color = Color.yellow;
            tm.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.GetComponent<MeshRenderer>().material = tm.font.material;
            label.AddComponent<BillboardFaceCamera>();
            _anchorLabelObjects.Add(label);
        }

        SetStatus($"Anchors ON — {counters.Values.Sum()} labels");
    }

    private void ShowCountdownPanel(bool show)
    {
        if (_countdownGroup != null)
        {
            _countdownGroup.alpha = show ? 1f : 0f;
            _countdownGroup.blocksRaycasts = show;
            _countdownGroup.interactable = show;
        }
        if (_mainPanel != null) _mainPanel.SetActive(!show);
    }

    private void SetStatus(string s)
    {
        _status = s;
        if (_statusText != null)
            _statusText = RemakeLabel(_statusText, "Status: " + s);
    }


    private void StartCountdownForAction(string label, System.Action action)
    {
        if (_countdownActive) return;
        _pendingAction = action;
        _countdownActive = true;
        _countdownEnd = Time.time + CountdownSeconds;
        BeginCountdownUI(label);
    }

    private void BeginCountdownUI(string label)
    {
        SetStatus("Look at your target...");
        if (_countdownPanel != null)
        {
            _countdownCmdText = RemakeLabel(_countdownCmdText, $"\"{label}\"");
            ShowCountdownPanel(true);
        }
    }

    private static bool IsRunningOnQuest()
    {
        // Runtime check — true when headset is connected (Quest Link or APK)
        return OVRManager.isHmdPresent;
    }

}
