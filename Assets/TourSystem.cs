using System;
using System.Globalization;
using System.Text;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.UI;

public class TourSystem : MonoBehaviour
{
    [Serializable]
    public class TourStop
    {
        public string title;
        public string locationLabel;

        [TextArea(3, 8)]
        public string summary;

        [TextArea(2, 5)]
        public string historicalFact;

        public bool useRealWorldCoordinates;
        public Vector2 coordinates;
        public Transform anchor;
        public AudioClip narrationClip;

        [Min(0f)]
        public float extraHoldSeconds = 2f;
    }

    private struct StopTemplate
    {
        public StopTemplate(
            string title,
            string locationLabel,
            string summary,
            string historicalFact,
            bool useCoordinates,
            Vector2 coordinates)
        {
            Title = title;
            LocationLabel = locationLabel;
            Summary = summary;
            HistoricalFact = historicalFact;
            UseCoordinates = useCoordinates;
            Coordinates = coordinates;
        }

        public string Title { get; }
        public string LocationLabel { get; }
        public string Summary { get; }
        public string HistoricalFact { get; }
        public bool UseCoordinates { get; }
        public Vector2 Coordinates { get; }
    }

    [Header("Legacy Bindings")]
    public Transform[] points;
    public TextMeshProUGUI infoText;
    public AudioSource audioSource;
    public AudioClip[] audioClips;

    [Header("Tour Stops")]
    public TourStop[] tourStops;

    [Header("Playback")]
    public bool startAutoTour;
    public bool loopTour = true;
    public bool rotateToMatchStopView = true;

    [Header("Selection Flow")]
    public bool requireMenuSelectionBeforeStart = true;
    public bool restrictStopChangesToMenu = true;

    [Min(0f)]
    public float minimumStopDuration = 8f;

    [Min(60f)]
    public float readingWordsPerMinute = 115f;

    [Header("Desktop Controls")]
    public KeyCode nextStopKey = KeyCode.Space;
    public KeyCode previousStopKey = KeyCode.Backspace;
    public KeyCode replayNarrationKey = KeyCode.R;
    public KeyCode toggleAutoTourKey = KeyCode.T;
    public KeyCode openMapKey = KeyCode.M;

    [Header("UI")]
    public bool autoStyleInfoPanel = true;
    public Vector2 panelSize = new Vector2(780f, 420f);

    [Header("Runtime Support")]
    public bool autoCreateNarrationService = true;
    public bool autoCreateMapsService = true;
    public bool autoCreateMultiplayerSupport = true;
    public bool autoCreateLocomotionRig = true;
    public bool autoCreateSelectionMenu = true;

    [Header("Service Configuration")]
    [TextArea(1, 2)]
    public string googleMapsApiKey = "";

    public string multiplayerAddress = "localhost";

    [Min(1000)]
    public ushort multiplayerPort = 7777;

    public string multiplayerPlayerLabel = "Guide";
    public bool startHostOnPlay = true;
    public bool startClientOnPlay;
    public bool allowClientTourRequests = true;
    public bool preferDeviceTextToSpeech = true;

    private const float EarthRadiusKilometres = 6371f;

    private readonly StringBuilder panelBuilder = new StringBuilder(768);

    private XROrigin xrOrigin;
    private TourNarrationService narrationService;
    private TourGoogleMapsService mapsService;
    private TourMultiplayerManager multiplayerManager;
    private int index;
    private float nextAutoAdvanceTime = -1f;
    private bool autoTourEnabled;
    private bool awaitingMenuSelection;

    public event Action<int, TourStop> StopChanged;
    public event Action<bool> AutoTourModeChanged;

    public int StopCount => GetStopCount();
    public int CurrentIndex => index;
    public bool AutoTourEnabled => autoTourEnabled;
    public bool HasActiveStop => !awaitingMenuSelection && CurrentStop() != null;
    public bool IsWaitingForMenuSelection => awaitingMenuSelection;

    private void Awake()
    {
        xrOrigin = GetComponent<XROrigin>();
        EnsureRuntimeSupport();
        SynchronizeStopDefinitions();
        CacheRuntimeServices();
        ApplyRuntimeServiceConfiguration();
        EnsureInfoText();
        EnsureAudioSource();
    }

    private void OnValidate()
    {
        minimumStopDuration = Mathf.Max(0f, minimumStopDuration);
        readingWordsPerMinute = Mathf.Max(60f, readingWordsPerMinute);
        panelSize.x = Mathf.Max(420f, panelSize.x);
        panelSize.y = Mathf.Max(220f, panelSize.y);
        multiplayerPort = (ushort)Mathf.Clamp(multiplayerPort, 1000, ushort.MaxValue);
        SynchronizeStopDefinitions();
    }

    private void Start()
    {
        autoTourEnabled = startAutoTour && !restrictStopChangesToMenu;

        if (GetStopCount() == 0)
        {
            ShowMissingSetupMessage();
            return;
        }

        index = Mathf.Clamp(index, 0, GetStopCount() - 1);
        awaitingMenuSelection = ShouldWaitForInitialMenuSelection();

        if (awaitingMenuSelection)
        {
            nextAutoAdvanceTime = -1f;
            RefreshPanel();
            return;
        }

        MoveToPoint();
    }

    private void Update()
    {
        if (GetStopCount() == 0)
        {
            return;
        }

        if (awaitingMenuSelection)
        {
            return;
        }

        if (!restrictStopChangesToMenu && Input.GetKeyDown(nextStopKey))
        {
            GoToNextStop();
        }

        if (!restrictStopChangesToMenu && Input.GetKeyDown(previousStopKey))
        {
            GoToPreviousStop();
        }

        if (Input.GetKeyDown(replayNarrationKey))
        {
            ReplayNarration();
        }

        if (!restrictStopChangesToMenu && Input.GetKeyDown(toggleAutoTourKey))
        {
            ToggleAutoTour();
        }

        if (Input.GetKeyDown(openMapKey))
        {
            OpenCurrentStopInMaps();
        }

        if (autoTourEnabled && nextAutoAdvanceTime > 0f && Time.time >= nextAutoAdvanceTime)
        {
            GoToNextStop();
        }
    }

    public void GoToNextStop()
    {
        if (GetStopCount() == 0)
        {
            return;
        }

        var nextIndex = index + 1;
        if (!loopTour && nextIndex >= GetStopCount())
        {
            var autoTourWasEnabled = autoTourEnabled;
            index = GetStopCount() - 1;
            autoTourEnabled = false;
            nextAutoAdvanceTime = -1f;
            RefreshPanel();

            if (autoTourWasEnabled)
            {
                AutoTourModeChanged?.Invoke(false);
            }

            return;
        }

        GoToStop(nextIndex);
    }

    public void GoToPreviousStop()
    {
        if (GetStopCount() == 0)
        {
            return;
        }

        GoToStop(index - 1);
    }

    public void ReplayNarration()
    {
        if (!HasActiveStop)
        {
            return;
        }

        PlayNarration(CurrentStop());
        ScheduleAutoAdvance(CurrentStop());
        RefreshPanel();
    }

    public void ToggleAutoTour()
    {
        SetAutoTour(!autoTourEnabled);
    }

    public void SetAutoTour(bool enabled)
    {
        if (enabled && (restrictStopChangesToMenu || awaitingMenuSelection))
        {
            enabled = false;
        }

        var changed = autoTourEnabled != enabled;
        autoTourEnabled = enabled;
        ScheduleAutoAdvance(CurrentStop());
        RefreshPanel();

        if (changed)
        {
            AutoTourModeChanged?.Invoke(autoTourEnabled);
        }
    }

    public void GoToStop(int stopIndex)
    {
        if (GetStopCount() == 0)
        {
            return;
        }

        awaitingMenuSelection = false;
        index = WrapIndex(stopIndex);
        MoveToPoint();
    }

    public TourStop GetStopAt(int stopIndex)
    {
        if (tourStops == null || stopIndex < 0 || stopIndex >= tourStops.Length)
        {
            return null;
        }

        return tourStops[stopIndex];
    }

    public void OpenCurrentStopInMaps()
    {
        if (!HasActiveStop)
        {
            Debug.LogWarning("Choose a landmark from the menu before opening map directions.", this);
            return;
        }

        var stop = CurrentStop();
        if (stop == null || !stop.useRealWorldCoordinates)
        {
            Debug.LogWarning("This stop does not have real-world coordinates assigned yet.", this);
            return;
        }

        var latitude = stop.coordinates.x.ToString("0.000000", CultureInfo.InvariantCulture);
        var longitude = stop.coordinates.y.ToString("0.000000", CultureInfo.InvariantCulture);
        var url = $"https://www.google.com/maps/search/?api=1&query={latitude},{longitude}";
        Application.OpenURL(url);
    }

    public void MoveToPoint()
    {
        var stop = CurrentStop();
        if (stop == null)
        {
            ShowMissingSetupMessage();
            return;
        }

        MoveRigToStop(stop);
        PlayNarration(stop);
        ScheduleAutoAdvance(stop);
        RefreshPanel();
        StopChanged?.Invoke(index, stop);
    }

    private void MoveRigToStop(TourStop stop)
    {
        if (stop.anchor == null)
        {
            return;
        }

        if (xrOrigin != null)
        {
            xrOrigin.MoveCameraToWorldLocation(stop.anchor.position);

            if (rotateToMatchStopView)
            {
                var desiredForward = Vector3.ProjectOnPlane(stop.anchor.forward, Vector3.up);
                if (desiredForward.sqrMagnitude > 0.0001f)
                {
                    xrOrigin.MatchOriginUpCameraForward(Vector3.up, desiredForward.normalized);
                }
            }

            return;
        }

        transform.position = stop.anchor.position;

        if (rotateToMatchStopView)
        {
            transform.rotation = stop.anchor.rotation;
        }
    }

    private void PlayNarration(TourStop stop)
    {
        if (narrationService != null)
        {
            narrationService.PlayNarration(stop, audioSource);
            return;
        }

        if (audioSource == null)
        {
            return;
        }

        audioSource.Stop();
        audioSource.clip = stop != null ? stop.narrationClip : null;

        if (audioSource.clip != null)
        {
            audioSource.Play();
        }
    }

    private void ScheduleAutoAdvance(TourStop stop)
    {
        if (!autoTourEnabled || stop == null || GetStopCount() <= 1)
        {
            nextAutoAdvanceTime = -1f;
            return;
        }

        var narrationDuration = GetNarrationDuration(stop);
        var holdDuration = Mathf.Max(minimumStopDuration, narrationDuration) + stop.extraHoldSeconds;
        nextAutoAdvanceTime = Time.time + holdDuration;
    }

    private float GetNarrationDuration(TourStop stop)
    {
        if (narrationService != null)
        {
            return narrationService.GetNarrationDuration(stop, readingWordsPerMinute, minimumStopDuration);
        }

        if (stop != null && stop.narrationClip != null)
        {
            return stop.narrationClip.length;
        }

        var words = CountWords(stop?.summary) + CountWords(stop?.historicalFact);
        var wordsPerSecond = Mathf.Max(1f, readingWordsPerMinute / 60f);
        return Mathf.Max(minimumStopDuration, words / wordsPerSecond);
    }

    private int CountWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private void EnsureInfoText()
    {
        if (infoText == null)
        {
            infoText = UnityEngine.Object.FindAnyObjectByType<TextMeshProUGUI>();
        }

        if (infoText == null || LooksMisconfigured(infoText))
        {
            infoText = CreateRuntimeInfoPanel();
        }

        if (autoStyleInfoPanel)
        {
            ConfigureInfoText(infoText);
        }
    }

    private bool LooksMisconfigured(TextMeshProUGUI candidate)
    {
        if (candidate == null)
        {
            return true;
        }

        var canvas = candidate.canvas;
        if (canvas == null)
        {
            return true;
        }

        if (canvas.transform.localScale.sqrMagnitude < 0.01f)
        {
            return true;
        }

        var rect = candidate.rectTransform.rect;
        return rect.width < 250f || rect.height < 100f;
    }

    private TextMeshProUGUI CreateRuntimeInfoPanel()
    {
        var canvasObject = new GameObject("Tour Info Canvas");
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasObject.AddComponent<GraphicRaycaster>();

        var panelObject = new GameObject("Tour Info Panel");
        panelObject.transform.SetParent(canvasObject.transform, false);

        var panelRect = panelObject.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0f, 1f);
        panelRect.anchorMax = new Vector2(0f, 1f);
        panelRect.pivot = new Vector2(0f, 1f);
        panelRect.anchoredPosition = new Vector2(24f, -24f);
        panelRect.sizeDelta = panelSize;

        var panelImage = panelObject.AddComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.72f);

        var textObject = new GameObject("Tour Info Text");
        textObject.transform.SetParent(panelObject.transform, false);

        var textRect = textObject.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(20f, 20f);
        textRect.offsetMax = new Vector2(-20f, -20f);

        var runtimeText = textObject.AddComponent<TextMeshProUGUI>();
        runtimeText.text = "Preparing tour...";
        runtimeText.raycastTarget = false;

        return runtimeText;
    }

    private void ConfigureInfoText(TextMeshProUGUI target)
    {
        if (target == null)
        {
            return;
        }

        target.enableAutoSizing = true;
        target.fontSizeMin = 18f;
        target.fontSizeMax = 28f;
        target.alignment = TextAlignmentOptions.TopLeft;
        target.textWrappingMode = TextWrappingModes.Normal;
        target.overflowMode = TextOverflowModes.Overflow;
        target.color = Color.white;
        target.rectTransform.anchorMin = new Vector2(0f, 1f);
        target.rectTransform.anchorMax = new Vector2(0f, 1f);
        target.rectTransform.pivot = new Vector2(0f, 1f);
        target.rectTransform.anchoredPosition = new Vector2(24f, -24f);
        target.rectTransform.sizeDelta = panelSize - new Vector2(40f, 40f);

        if (target.rectTransform.parent is RectTransform parentRect && parentRect.GetComponent<Image>() != null)
        {
            parentRect.sizeDelta = panelSize;
        }
    }

    private void EnsureAudioSource()
    {
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
        }

        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.spatialBlend = 0f;
    }

    private void RefreshPanel()
    {
        if (infoText == null)
        {
            return;
        }

        if (awaitingMenuSelection)
        {
            infoText.text =
                "<size=130%><b>Choose a landmark to begin.</b></size>\n\n" +
                "Open the landmark menu and select a location. Once you travel there, walking controls turn back on so you can explore the area.\n\n" +
                "<b>Controls:</b> Tab open menu | Enter / trigger travel | Left stick or WASD move after arrival";
            return;
        }

        var stop = CurrentStop();
        if (stop == null)
        {
            infoText.text = "No tour stop is active.";
            return;
        }

        panelBuilder.Clear();
        panelBuilder.AppendLine($"<size=130%><b>{stop.title}</b></size>");

        if (!string.IsNullOrWhiteSpace(stop.locationLabel))
        {
            panelBuilder.AppendLine(stop.locationLabel);
        }

        panelBuilder.AppendLine();

        if (!string.IsNullOrWhiteSpace(stop.summary))
        {
            panelBuilder.AppendLine(stop.summary);
            panelBuilder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(stop.historicalFact))
        {
            panelBuilder.AppendLine($"<b>Historical note:</b> {stop.historicalFact}");
        }

        if (stop.useRealWorldCoordinates)
        {
            panelBuilder.AppendLine($"<b>Approx. GPS:</b> {FormatCoordinates(stop.coordinates)}");
        }

        var distanceToNext = DistanceToNextStopKilometres();
        if (distanceToNext >= 0.2f)
        {
            panelBuilder.AppendLine($"<b>Next leg:</b> {distanceToNext:0.0} km");
        }

        panelBuilder.AppendLine();
        panelBuilder.AppendLine($"<b>Stop:</b> {index + 1}/{GetStopCount()}    <b>Mode:</b> {(autoTourEnabled ? "Auto tour" : "Manual tour")}");

        if (restrictStopChangesToMenu)
        {
            panelBuilder.AppendLine("<b>Controls:</b> Tab menu | Choose locations from the menu | R replay | M map");
            panelBuilder.AppendLine("<b>Explore:</b> Use left stick or WASD to walk around after you arrive.");
        }
        else
        {
            panelBuilder.AppendLine("<b>Controls:</b> Space next | Backspace previous | R replay | T auto | M map | Tab menu");
        }

        if (narrationService != null)
        {
            panelBuilder.AppendLine($"<b>Voice:</b> {narrationService.DescribeNarrationMode(stop)}");
        }
        else if (stop.narrationClip == null)
        {
            panelBuilder.AppendLine("<b>Narration:</b> Add an AudioClip for voice guidance or use the on-screen facts.");
        }

        if (mapsService != null)
        {
            panelBuilder.AppendLine(
                mapsService.HasApiKey
                    ? "<b>Maps:</b> Live Google Maps previews and route data are available from the landmark menu."
                    : "<b>Maps:</b> Coordinates are ready. Add your Google Maps API key in TourGoogleMapsService for live previews.");
        }

        if (multiplayerManager != null)
        {
            panelBuilder.AppendLine($"<b>Multiplayer:</b> {multiplayerManager.StatusSummary}");
        }

        infoText.text = panelBuilder.ToString();
    }

    private void ShowMissingSetupMessage()
    {
        EnsureInfoText();

        if (infoText == null)
        {
            return;
        }

        infoText.text =
            "<b>Tour setup incomplete.</b>\n\n" +
            "Assign landmark points in the TourSystem component or populate the tourStops array.\n" +
            "The script is ready to drive guided tours, narration, and map links once the stops are connected.";
    }

    private void SynchronizeStopDefinitions()
    {
        var pointCount = points != null ? points.Length : 0;
        var clipCount = audioClips != null ? audioClips.Length : 0;
        var desiredCount = Mathf.Max(pointCount, Mathf.Max(clipCount, tourStops != null ? tourStops.Length : 0));

        if (desiredCount == 0)
        {
            return;
        }

        if (tourStops == null || tourStops.Length != desiredCount)
        {
            Array.Resize(ref tourStops, desiredCount);
        }

        for (var i = 0; i < desiredCount; i++)
        {
            if (tourStops[i] == null)
            {
                tourStops[i] = new TourStop();
            }

            if (tourStops[i].anchor == null && i < pointCount)
            {
                tourStops[i].anchor = points[i];
            }

            if (tourStops[i].narrationClip == null && i < clipCount)
            {
                tourStops[i].narrationClip = audioClips[i];
            }

            ApplyTemplateIfEmpty(tourStops[i], i);
        }
    }

    private void EnsureRuntimeSupport()
    {
        if (autoCreateNarrationService && GetComponent<TourNarrationService>() == null)
        {
            gameObject.AddComponent<TourNarrationService>();
        }

        if (autoCreateMapsService && GetComponent<TourGoogleMapsService>() == null)
        {
            gameObject.AddComponent<TourGoogleMapsService>();
        }

        if (autoCreateMultiplayerSupport && GetComponent<TourMultiplayerManager>() == null)
        {
            gameObject.AddComponent<TourMultiplayerManager>();
        }

        if (autoCreateLocomotionRig && GetComponent<XRTourRigBootstrap>() == null)
        {
            gameObject.AddComponent<XRTourRigBootstrap>();
        }

        if (autoCreateSelectionMenu && GetComponent<LandmarkSelectionMenu>() == null)
        {
            gameObject.AddComponent<LandmarkSelectionMenu>();
        }
    }

    private void CacheRuntimeServices()
    {
        narrationService = GetComponent<TourNarrationService>();
        mapsService = GetComponent<TourGoogleMapsService>();
        multiplayerManager = GetComponent<TourMultiplayerManager>();
    }

    private void ApplyRuntimeServiceConfiguration()
    {
        if (narrationService != null)
        {
            narrationService.preferTextToSpeechOverAudioClips = preferDeviceTextToSpeech;
        }

        if (mapsService != null && !string.IsNullOrWhiteSpace(googleMapsApiKey))
        {
            mapsService.apiKey = googleMapsApiKey.Trim();
        }

        if (multiplayerManager != null)
        {
            multiplayerManager.ApplyExternalConfiguration(
                multiplayerAddress,
                multiplayerPort,
                multiplayerPlayerLabel,
                startHostOnPlay,
                startClientOnPlay,
                allowClientTourRequests);
        }
    }

    private void ApplyTemplateIfEmpty(TourStop stop, int stopIndex)
    {
        var template = GetTemplate(stopIndex, stop != null ? stop.anchor : null);

        if (string.IsNullOrWhiteSpace(stop.title))
        {
            stop.title = template.Title;
        }

        if (string.IsNullOrWhiteSpace(stop.locationLabel))
        {
            stop.locationLabel = template.LocationLabel;
        }

        if (string.IsNullOrWhiteSpace(stop.summary))
        {
            stop.summary = template.Summary;
        }

        if (string.IsNullOrWhiteSpace(stop.historicalFact))
        {
            stop.historicalFact = template.HistoricalFact;
        }

        if (!stop.useRealWorldCoordinates && stop.coordinates == Vector2.zero && template.UseCoordinates)
        {
            stop.useRealWorldCoordinates = true;
            stop.coordinates = template.Coordinates;
        }
    }

    private StopTemplate GetTemplate(int stopIndex, Transform anchor)
    {
        switch (stopIndex)
        {
            case 0:
                return new StopTemplate(
                    "Sehlaba sa Botha-Bothe",
                    "Butha-Buthe District, Lesotho",
                    "Use this stop to introduce the landscape, explain why it matters to the district, and connect the visitor to your original 3D model.",
                    "Replace this placeholder with your group's verified historical facts, coordinates, and Harvard references before submission.",
                    false,
                    Vector2.zero);

            case 1:
                return new StopTemplate(
                    "Maletsunyane Falls",
                    "Near Semonkong, Lesotho",
                    "Maletsunyane Falls is one of Lesotho's signature landmarks, dropping about 192 metres into a basalt gorge and creating the smoky mist that gave nearby Semonkong its name.",
                    "This stop works well for voice narration about geology, local legends around the echoing gorge, and community-led adventure tourism in the highlands.",
                    true,
                    new Vector2(-29.8678f, 28.0510f));

            case 2:
                return new StopTemplate(
                    "Maletsunyane Falls - Top View",
                    "Clifftop lookout above Semonkong",
                    "From the top view, visitors can compare the river path, cliff edge, and valley depth before moving back down to the main waterfall experience.",
                    "Use this stop to discuss elevation, erosion, and how viewpoint design makes a guided VR tour feel cinematic instead of static.",
                    true,
                    new Vector2(-29.8678f, 28.0510f));

            default:
                var fallbackName = anchor != null ? anchor.name : $"Landmark Stop {stopIndex + 1}";
                return new StopTemplate(
                    fallbackName,
                    "Lesotho",
                    "Add a landmark summary here so each stop teaches the visitor something distinct.",
                    "Add your researched fact and a Harvard reference note for the report.",
                    false,
                    Vector2.zero);
        }
    }

    private TourStop CurrentStop()
    {
        if (tourStops == null || tourStops.Length == 0 || index < 0 || index >= tourStops.Length)
        {
            return null;
        }

        return tourStops[index];
    }

    private bool ShouldWaitForInitialMenuSelection()
    {
        if (!requireMenuSelectionBeforeStart)
        {
            return false;
        }

        return autoCreateSelectionMenu || GetComponent<LandmarkSelectionMenu>() != null;
    }

    private int GetStopCount()
    {
        return tourStops != null ? tourStops.Length : 0;
    }

    private int WrapIndex(int rawIndex)
    {
        if (GetStopCount() == 0)
        {
            return 0;
        }

        if (!loopTour)
        {
            return Mathf.Clamp(rawIndex, 0, GetStopCount() - 1);
        }

        return (rawIndex % GetStopCount() + GetStopCount()) % GetStopCount();
    }

    private float DistanceToNextStopKilometres()
    {
        if (GetStopCount() <= 1)
        {
            return -1f;
        }

        var current = CurrentStop();
        var next = tourStops[WrapIndex(index + 1)];

        if (current == null || next == null || !current.useRealWorldCoordinates || !next.useRealWorldCoordinates)
        {
            return -1f;
        }

        return CalculateDistanceKilometres(current.coordinates, next.coordinates);
    }

    private float CalculateDistanceKilometres(Vector2 first, Vector2 second)
    {
        var latitudeDelta = Mathf.Deg2Rad * (second.x - first.x);
        var longitudeDelta = Mathf.Deg2Rad * (second.y - first.y);

        var firstLatitude = Mathf.Deg2Rad * first.x;
        var secondLatitude = Mathf.Deg2Rad * second.x;

        var a =
            Mathf.Sin(latitudeDelta * 0.5f) * Mathf.Sin(latitudeDelta * 0.5f) +
            Mathf.Cos(firstLatitude) * Mathf.Cos(secondLatitude) *
            Mathf.Sin(longitudeDelta * 0.5f) * Mathf.Sin(longitudeDelta * 0.5f);

        var c = 2f * Mathf.Atan2(Mathf.Sqrt(a), Mathf.Sqrt(1f - a));
        return EarthRadiusKilometres * c;
    }

    private string FormatCoordinates(Vector2 coordinates)
    {
        var latitudeDirection = coordinates.x < 0f ? "S" : "N";
        var longitudeDirection = coordinates.y < 0f ? "W" : "E";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.0000}° {1}, {2:0.0000}° {3}",
            Mathf.Abs(coordinates.x),
            latitudeDirection,
            Mathf.Abs(coordinates.y),
            longitudeDirection);
    }
}
