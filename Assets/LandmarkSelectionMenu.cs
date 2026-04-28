using System.Collections.Generic;
using System.Text;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class LandmarkSelectionMenu : MonoBehaviour
{
    [Header("Menu Layout")]
    public bool openOnStart = true;
    public Vector3 menuLocalOffset = new Vector3(0f, 0f, 1.5f);
    public Vector2 canvasSize = new Vector2(1200f, 860f);
    public float canvasScale = 0.0014f;

    [Header("Navigation")]
    [Range(0.2f, 1f)]
    public float navigationThreshold = 0.6f;

    [Min(0.05f)]
    public float initialNavigationDelay = 0.24f;

    [Min(0.03f)]
    public float repeatNavigationDelay = 0.12f;

    [Header("Colors")]
    public Color menuTint = new Color(0.05f, 0.08f, 0.11f, 0.92f);
    public Color normalEntryColor = new Color(0.14f, 0.19f, 0.25f, 0.9f);
    public Color selectedEntryColor = new Color(0.87f, 0.64f, 0.18f, 1f);
    public Color activeEntryColor = new Color(0.16f, 0.52f, 0.38f, 1f);
    public Color selectedTextColor = new Color(0.08f, 0.08f, 0.08f, 1f);

    private readonly List<Image> entryBackgrounds = new List<Image>();
    private readonly List<TextMeshProUGUI> entryLabels = new List<TextMeshProUGUI>();
    private readonly StringBuilder detailBuilder = new StringBuilder(1024);

    private TourSystem tourSystem;
    private TourGoogleMapsService mapsService;
    private TourMultiplayerManager multiplayerManager;
    private XROrigin xrOrigin;
    private XRTourRigBootstrap locomotionRig;

    private InputAction navigateAction;
    private InputAction submitAction;
    private InputAction toggleMenuAction;
    private InputAction openMapAction;
    private InputAction startHostAction;
    private InputAction startClientAction;
    private InputAction stopNetworkAction;
    private InputAction toggleGuideSyncAction;

    private GameObject menuRoot;
    private RectTransform entriesRoot;
    private TextMeshProUGUI titleText;
    private TextMeshProUGUI detailText;
    private TextMeshProUGUI mapStatusText;
    private TextMeshProUGUI networkText;
    private TextMeshProUGUI footerText;
    private RawImage mapPreviewImage;

    private bool menuVisible;
    private bool menuBuilt;
    private int selectedIndex;
    private int lastNavigationDirection;
    private float nextNavigationTime;
    private Texture2D currentPreviewTexture;
    private string previewStatus = "Waiting for map data.";
    private string routeStatus = string.Empty;

    private void Awake()
    {
        tourSystem = GetComponent<TourSystem>();
        mapsService = GetComponent<TourGoogleMapsService>();
        multiplayerManager = GetComponent<TourMultiplayerManager>();
        xrOrigin = GetComponent<XROrigin>();
        locomotionRig = GetComponent<XRTourRigBootstrap>();
        CreateActions();
    }

    private void OnEnable()
    {
        EnableActions();

        if (tourSystem != null)
        {
            tourSystem.StopChanged += OnStopChanged;
        }

        if (mapsService != null)
        {
            mapsService.PreviewUpdated += OnPreviewUpdated;
            mapsService.DirectionsUpdated += OnDirectionsUpdated;
        }
    }

    private void Start()
    {
        RefreshDependencies();
        EnsureMenuBuilt();

        if (tourSystem != null && tourSystem.HasActiveStop)
        {
            selectedIndex = Mathf.Clamp(tourSystem.CurrentIndex, 0, Mathf.Max(0, tourSystem.StopCount - 1));
        }

        RefreshMenu();
        SetMenuVisible((openOnStart || (tourSystem != null && tourSystem.IsWaitingForMenuSelection)) && tourSystem != null && tourSystem.StopCount > 0);
    }

    private void Update()
    {
        HandleNetworkActions();
        EnsureMenuBuilt();

        if (toggleMenuAction != null && toggleMenuAction.WasPressedThisFrame())
        {
            SetMenuVisible(!menuVisible);
        }

        if (!menuVisible || tourSystem == null || tourSystem.StopCount == 0)
        {
            return;
        }

        HandleNavigation();

        if (submitAction != null && submitAction.WasPressedThisFrame())
        {
            TravelToSelection();
        }

        if (openMapAction != null && openMapAction.WasPressedThisFrame())
        {
            tourSystem.OpenCurrentStopInMaps();
        }
    }

    private void OnDisable()
    {
        if (tourSystem != null)
        {
            tourSystem.StopChanged -= OnStopChanged;
        }

        if (mapsService != null)
        {
            mapsService.PreviewUpdated -= OnPreviewUpdated;
            mapsService.DirectionsUpdated -= OnDirectionsUpdated;
        }

        DisableActions();
    }

    private void OnDestroy()
    {
        if (currentPreviewTexture != null)
        {
            Destroy(currentPreviewTexture);
        }

        navigateAction?.Dispose();
        submitAction?.Dispose();
        toggleMenuAction?.Dispose();
        openMapAction?.Dispose();
        startHostAction?.Dispose();
        startClientAction?.Dispose();
        stopNetworkAction?.Dispose();
        toggleGuideSyncAction?.Dispose();
    }

    private void SetMenuVisible(bool visible)
    {
        if (!visible && tourSystem != null && tourSystem.IsWaitingForMenuSelection)
        {
            visible = true;
        }

        menuVisible = visible;

        if (menuRoot != null)
        {
            menuRoot.SetActive(visible);
        }

        if (visible)
        {
            if (tourSystem != null)
            {
                if (tourSystem.HasActiveStop)
                {
                    selectedIndex = Mathf.Clamp(tourSystem.CurrentIndex, 0, Mathf.Max(0, tourSystem.StopCount - 1));
                }
                else
                {
                    selectedIndex = Mathf.Clamp(selectedIndex, 0, Mathf.Max(0, tourSystem.StopCount - 1));
                }

                tourSystem.SetAutoTour(false);
            }

            locomotionRig?.SetLocomotionEnabled(false);
            RefreshMenu();
        }
        else
        {
            locomotionRig?.SetLocomotionEnabled(true);
            lastNavigationDirection = 0;
            nextNavigationTime = 0f;
        }
    }

    private void HandleNavigation()
    {
        if (navigateAction == null || tourSystem == null || tourSystem.StopCount <= 1)
        {
            return;
        }

        var navigate = navigateAction.ReadValue<Vector2>();
        var strongestAxis = Mathf.Abs(navigate.y) >= Mathf.Abs(navigate.x) ? navigate.y : navigate.x;

        if (Mathf.Abs(strongestAxis) < navigationThreshold)
        {
            lastNavigationDirection = 0;
            nextNavigationTime = 0f;
            return;
        }

        if (Time.unscaledTime < nextNavigationTime)
        {
            return;
        }

        var direction = strongestAxis > 0f ? -1 : 1;
        selectedIndex = WrapSelection(selectedIndex + direction);
        RefreshMenu();

        nextNavigationTime = Time.unscaledTime + (lastNavigationDirection == direction ? repeatNavigationDelay : initialNavigationDelay);
        lastNavigationDirection = direction;
    }

    private void TravelToSelection()
    {
        if (tourSystem == null || tourSystem.StopCount == 0)
        {
            return;
        }

        tourSystem.GoToStop(selectedIndex);
        multiplayerManager?.RequestGuideStop(selectedIndex);
        SetMenuVisible(false);
    }

    private void EnsureMenuBuilt()
    {
        if (menuBuilt || xrOrigin == null || xrOrigin.Camera == null)
        {
            return;
        }

        menuRoot = new GameObject("Landmark Selection Menu", typeof(RectTransform));
        menuRoot.transform.SetParent(xrOrigin.Camera.transform, false);
        menuRoot.transform.localPosition = menuLocalOffset;
        menuRoot.transform.localRotation = Quaternion.identity;
        menuRoot.transform.localScale = Vector3.one * canvasScale;

        var rootRect = menuRoot.GetComponent<RectTransform>();
        rootRect.sizeDelta = canvasSize;

        var canvas = menuRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = xrOrigin.Camera;
        menuRoot.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
        menuRoot.AddComponent<GraphicRaycaster>();

        var background = CreatePanel("Background", rootRect, Vector2.zero, Vector2.one, menuTint);
        background.raycastTarget = false;

        titleText = CreateText(
            "Title",
            background.rectTransform,
            new Vector2(0.05f, 0.88f),
            new Vector2(0.95f, 0.98f),
            42,
            FontStyles.Bold,
            TextAlignmentOptions.TopLeft);

        entriesRoot = CreatePanel(
            "Entries Root",
            background.rectTransform,
            new Vector2(0.05f, 0.24f),
            new Vector2(0.4f, 0.82f),
            new Color(0.08f, 0.12f, 0.16f, 0.7f)).rectTransform;

        var layout = entriesRoot.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 18, 18);
        layout.spacing = 12f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;

        detailText = CreateText(
            "Detail",
            background.rectTransform,
            new Vector2(0.45f, 0.56f),
            new Vector2(0.95f, 0.82f),
            26,
            FontStyles.Normal,
            TextAlignmentOptions.TopLeft);

        var mapPanel = CreatePanel(
            "Map Preview Panel",
            background.rectTransform,
            new Vector2(0.45f, 0.24f),
            new Vector2(0.95f, 0.52f),
            new Color(0.08f, 0.12f, 0.16f, 0.7f));

        mapPreviewImage = new GameObject("Map Preview", typeof(RectTransform)).AddComponent<RawImage>();
        mapPreviewImage.transform.SetParent(mapPanel.rectTransform, false);
        var mapRect = mapPreviewImage.rectTransform;
        mapRect.anchorMin = new Vector2(0.03f, 0.08f);
        mapRect.anchorMax = new Vector2(0.97f, 0.92f);
        mapRect.offsetMin = Vector2.zero;
        mapRect.offsetMax = Vector2.zero;
        mapPreviewImage.color = new Color(1f, 1f, 1f, 0.92f);

        mapStatusText = CreateText(
            "Map Status",
            background.rectTransform,
            new Vector2(0.45f, 0.16f),
            new Vector2(0.95f, 0.23f),
            22,
            FontStyles.Italic,
            TextAlignmentOptions.TopLeft);

        networkText = CreateText(
            "Network Status",
            background.rectTransform,
            new Vector2(0.05f, 0.13f),
            new Vector2(0.95f, 0.21f),
            22,
            FontStyles.Normal,
            TextAlignmentOptions.TopLeft);

        footerText = CreateText(
            "Footer",
            background.rectTransform,
            new Vector2(0.05f, 0.04f),
            new Vector2(0.95f, 0.11f),
            20,
            FontStyles.Italic,
            TextAlignmentOptions.BottomLeft);

        menuBuilt = true;
    }

    private void RefreshMenu()
    {
        if (!menuBuilt || tourSystem == null)
        {
            return;
        }

        RebuildEntriesIfNeeded();

        titleText.text = tourSystem.IsWaitingForMenuSelection
            ? "Choose a Landmark to Begin"
            : "Lesotho Landmark Selector";

        if (tourSystem.StopCount == 0)
        {
            detailText.text = "Add landmark stops to the TourSystem to populate the travel menu.";
            RefreshMapPresentation(null);
            RefreshNetworkStatus();
            footerText.text = "No landmark data available.";
            return;
        }

        selectedIndex = WrapSelection(selectedIndex);
        var selectedStop = tourSystem.GetStopAt(selectedIndex);

        for (var i = 0; i < entryLabels.Count; i++)
        {
            var stop = tourSystem.GetStopAt(i);
            var isSelected = i == selectedIndex;
            var isActive = tourSystem.HasActiveStop && i == tourSystem.CurrentIndex;

            entryLabels[i].text = $"{i + 1}. {(stop != null ? stop.title : $"Stop {i + 1}")}";
            entryLabels[i].color = isSelected ? selectedTextColor : Color.white;
            entryBackgrounds[i].color = isSelected
                ? selectedEntryColor
                : (isActive ? activeEntryColor : normalEntryColor);
        }

        detailBuilder.Clear();

        if (selectedStop != null)
        {
            detailBuilder.AppendLine($"<size=130%><b>{selectedStop.title}</b></size>");

            if (!string.IsNullOrWhiteSpace(selectedStop.locationLabel))
            {
                detailBuilder.AppendLine(selectedStop.locationLabel);
            }

            detailBuilder.AppendLine();

            if (!string.IsNullOrWhiteSpace(selectedStop.summary))
            {
                detailBuilder.AppendLine(selectedStop.summary);
                detailBuilder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(selectedStop.historicalFact))
            {
                detailBuilder.AppendLine($"<b>Historical note:</b> {selectedStop.historicalFact}");
                detailBuilder.AppendLine();
            }

            if (selectedStop.useRealWorldCoordinates)
            {
                detailBuilder.AppendLine($"<b>GPS:</b> {selectedStop.coordinates.x:0.0000}, {selectedStop.coordinates.y:0.0000}");
                detailBuilder.AppendLine();
            }

            detailBuilder.AppendLine(
                tourSystem.IsWaitingForMenuSelection
                    ? "<b>Start here:</b> Select this stop to begin the visit, then walk around it freely."
                    : "<b>Ready to travel:</b> Select this stop to jump directly into the landmark.");
        }

        detailText.text = detailBuilder.ToString();
        RefreshMapPresentation(selectedStop);
        RefreshNetworkStatus();
        footerText.text =
            "Navigate: Left stick / arrows | Travel: Trigger / Enter | Walk after travel: Left stick or WASD | Map: Right primary / M | " +
            "Network: H host | J client | K stop | G sync | Menu: Left primary / Tab";
    }

    private void RebuildEntriesIfNeeded()
    {
        if (entriesRoot == null || tourSystem == null || entryLabels.Count == tourSystem.StopCount)
        {
            return;
        }

        foreach (Transform child in entriesRoot)
        {
            Destroy(child.gameObject);
        }

        entryBackgrounds.Clear();
        entryLabels.Clear();

        for (var i = 0; i < tourSystem.StopCount; i++)
        {
            var entryObject = new GameObject($"Stop {i + 1}", typeof(RectTransform));
            entryObject.transform.SetParent(entriesRoot, false);

            var layoutElement = entryObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 76f;

            var background = entryObject.AddComponent<Image>();
            background.color = normalEntryColor;

            var button = entryObject.AddComponent<Button>();
            var stopIndex = i;
            button.onClick.AddListener(() =>
            {
                selectedIndex = stopIndex;
                RefreshMenu();
                TravelToSelection();
            });

            var label = CreateText(
                "Label",
                entryObject.GetComponent<RectTransform>(),
                new Vector2(0.05f, 0.12f),
                new Vector2(0.95f, 0.88f),
                28,
                FontStyles.Bold,
                TextAlignmentOptions.Left);

            entryBackgrounds.Add(background);
            entryLabels.Add(label);
        }
    }

    private void CreateActions()
    {
        navigateAction = new InputAction("Landmark Navigate", InputActionType.Value, expectedControlType: "Vector2");
        navigateAction.AddBinding("<XRController>{LeftHand}/{Primary2DAxis}").WithProcessor("StickDeadzone");
        navigateAction.AddBinding("<Gamepad>/dpad");
        navigateAction.AddBinding("<Gamepad>/leftStick").WithProcessor("StickDeadzone");

        var keyboardNavigate = navigateAction.AddCompositeBinding("2DVector");
        keyboardNavigate.With("Up", "<Keyboard>/upArrow");
        keyboardNavigate.With("Down", "<Keyboard>/downArrow");
        keyboardNavigate.With("Left", "<Keyboard>/leftArrow");
        keyboardNavigate.With("Right", "<Keyboard>/rightArrow");

        submitAction = new InputAction("Landmark Submit", InputActionType.Button);
        submitAction.AddBinding("<Keyboard>/enter");
        submitAction.AddBinding("<Gamepad>/buttonSouth");
        submitAction.AddBinding("<XRController>{RightHand}/{TriggerButton}");

        toggleMenuAction = new InputAction("Landmark Menu Toggle", InputActionType.Button);
        toggleMenuAction.AddBinding("<Keyboard>/tab");
        toggleMenuAction.AddBinding("<Keyboard>/escape");
        toggleMenuAction.AddBinding("<Gamepad>/start");
        toggleMenuAction.AddBinding("<XRController>{LeftHand}/{PrimaryButton}");

        openMapAction = new InputAction("Landmark Open Map", InputActionType.Button);
        openMapAction.AddBinding("<Keyboard>/m");
        openMapAction.AddBinding("<Gamepad>/buttonNorth");
        openMapAction.AddBinding("<XRController>{RightHand}/{PrimaryButton}");

        startHostAction = new InputAction("Start Hosted Tour", InputActionType.Button);
        startHostAction.AddBinding("<Keyboard>/h");

        startClientAction = new InputAction("Start Tour Client", InputActionType.Button);
        startClientAction.AddBinding("<Keyboard>/j");

        stopNetworkAction = new InputAction("Stop Tour Network", InputActionType.Button);
        stopNetworkAction.AddBinding("<Keyboard>/k");

        toggleGuideSyncAction = new InputAction("Toggle Guide Sync", InputActionType.Button);
        toggleGuideSyncAction.AddBinding("<Keyboard>/g");
    }

    private void EnableActions()
    {
        navigateAction?.Enable();
        submitAction?.Enable();
        toggleMenuAction?.Enable();
        openMapAction?.Enable();
        startHostAction?.Enable();
        startClientAction?.Enable();
        stopNetworkAction?.Enable();
        toggleGuideSyncAction?.Enable();
    }

    private void DisableActions()
    {
        navigateAction?.Disable();
        submitAction?.Disable();
        toggleMenuAction?.Disable();
        openMapAction?.Disable();
        startHostAction?.Disable();
        startClientAction?.Disable();
        stopNetworkAction?.Disable();
        toggleGuideSyncAction?.Disable();
    }

    private void RefreshDependencies()
    {
        if (tourSystem == null)
        {
            tourSystem = GetComponent<TourSystem>();
        }

        if (mapsService == null)
        {
            mapsService = GetComponent<TourGoogleMapsService>();
        }

        if (multiplayerManager == null)
        {
            multiplayerManager = GetComponent<TourMultiplayerManager>();
        }

        if (xrOrigin == null)
        {
            xrOrigin = GetComponent<XROrigin>();
        }

        if (locomotionRig == null)
        {
            locomotionRig = GetComponent<XRTourRigBootstrap>();
        }
    }

    private void HandleNetworkActions()
    {
        if (multiplayerManager == null)
        {
            return;
        }

        if (startHostAction != null && startHostAction.WasPressedThisFrame())
        {
            multiplayerManager.StartHostedTour();
            RefreshNetworkStatus();
        }

        if (startClientAction != null && startClientAction.WasPressedThisFrame())
        {
            multiplayerManager.StartTourClient();
            RefreshNetworkStatus();
        }

        if (stopNetworkAction != null && stopNetworkAction.WasPressedThisFrame())
        {
            multiplayerManager.StopNetworking();
            RefreshNetworkStatus();
        }

        if (toggleGuideSyncAction != null && toggleGuideSyncAction.WasPressedThisFrame())
        {
            multiplayerManager.ToggleGuideSync();
            RefreshNetworkStatus();
        }
    }

    private void RefreshMapPresentation(TourSystem.TourStop selectedStop)
    {
        if (mapPreviewImage == null || mapStatusText == null)
        {
            return;
        }

        if (mapsService == null)
        {
            previewStatus = "Live map integration is not attached to this XR rig.";
            routeStatus = string.Empty;
            mapPreviewImage.texture = null;
            RefreshMapStatusText();
            return;
        }

        if (selectedStop == null)
        {
            previewStatus = "Choose a landmark to preview its real-world map.";
            routeStatus = string.Empty;
            mapPreviewImage.texture = null;
            RefreshMapStatusText();
            return;
        }

        mapsService.RequestPreview(selectedStop);
        mapsService.RequestDirections(tourSystem.HasActiveStop ? tourSystem.GetStopAt(tourSystem.CurrentIndex) : null, selectedStop);
    }

    private void RefreshMapStatusText()
    {
        if (mapStatusText == null)
        {
            return;
        }

        mapStatusText.text = string.IsNullOrWhiteSpace(routeStatus)
            ? previewStatus
            : $"{previewStatus}\n{routeStatus}";
    }

    private void RefreshNetworkStatus()
    {
        if (networkText == null)
        {
            return;
        }

        if (multiplayerManager == null)
        {
            networkText.text = "Multiplayer manager missing from XR Origin.";
            return;
        }

        networkText.text =
            $"<b>Multiplayer:</b> {multiplayerManager.StatusSummary}\n" +
            $"<b>Guide sync:</b> {(multiplayerManager.IsGuideSyncActive ? "On" : "Off")} | " +
            $"<b>Client requests:</b> {(multiplayerManager.AllowClientStopRequests ? "Allowed" : "Host only")}";
    }

    private void OnPreviewUpdated(Texture2D texture, string status)
    {
        if (currentPreviewTexture != null && currentPreviewTexture != texture)
        {
            Destroy(currentPreviewTexture);
        }

        currentPreviewTexture = texture;

        if (mapPreviewImage != null)
        {
            mapPreviewImage.texture = texture;
            mapPreviewImage.color = texture != null ? Color.white : new Color(1f, 1f, 1f, 0.12f);
        }

        previewStatus = status;
        RefreshMapStatusText();
    }

    private void OnDirectionsUpdated(string status)
    {
        routeStatus = status;
        RefreshMapStatusText();
    }

    private void OnStopChanged(int stopIndex, TourSystem.TourStop stop)
    {
        selectedIndex = stopIndex;
        RefreshMenu();
    }

    private int WrapSelection(int rawIndex)
    {
        if (tourSystem == null || tourSystem.StopCount == 0)
        {
            return 0;
        }

        return (rawIndex % tourSystem.StopCount + tourSystem.StopCount) % tourSystem.StopCount;
    }

    private Image CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Color color)
    {
        var panelObject = new GameObject(name, typeof(RectTransform));
        panelObject.transform.SetParent(parent, false);

        var rect = panelObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var image = panelObject.AddComponent<Image>();
        image.color = color;
        return image;
    }

    private TextMeshProUGUI CreateText(
        string name,
        Transform parent,
        Vector2 anchorMin,
        Vector2 anchorMax,
        float fontSize,
        FontStyles fontStyle,
        TextAlignmentOptions alignment)
    {
        var textObject = new GameObject(name, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);

        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = new Vector2(12f, 12f);
        rect.offsetMax = new Vector2(-12f, -12f);

        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = alignment;
        text.color = Color.white;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode = TextOverflowModes.Overflow;
        return text;
    }
}
