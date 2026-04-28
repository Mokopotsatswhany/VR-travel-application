using System;
using System.Reflection;
using Mirror;
using UnityEngine;

[DisallowMultipleComponent]
public class TourMultiplayerManager : NetworkManager
{
    public struct TourStopSyncMessage : NetworkMessage
    {
        public int stopIndex;
        public bool autoTourEnabled;
    }

    public struct TourStopRequestMessage : NetworkMessage
    {
        public int stopIndex;
    }

    [Header("Networking")]
    public bool autoCreateTelepathyTransport = true;
    public bool addDesktopNetworkHud = true;
    public bool startHostOnPlay;
    public bool startClientOnPlay;

    [Min(1000)]
    public ushort telepathyPort = 7777;

    [Header("Guided Tour Sync")]
    public bool synchronizeGuideStops = true;
    public bool allowClientStopRequests;

    [Header("Player Identity")]
    public string localPlayerLabel = "Visitor";

    [Header("Desktop Shortcuts")]
    public KeyCode startHostKey = KeyCode.H;
    public KeyCode startClientKey = KeyCode.J;
    public KeyCode stopNetworkKey = KeyCode.K;
    public KeyCode toggleGuideSyncKey = KeyCode.G;

    private const string RuntimePlayerGuid = "36f33bc9-3287-488d-a2f2-a407fe23046a";
    private static readonly FieldInfo NetworkIdentityAssetIdField =
        typeof(NetworkIdentity).GetField("_assetId", BindingFlags.Instance | BindingFlags.NonPublic);

    private TourSystem tourSystem;
    private bool applyingRemoteState;

    public string LocalPlayerLabel => localPlayerLabel;
    public bool AllowClientStopRequests => allowClientStopRequests;
    public bool IsGuideSyncActive => synchronizeGuideStops;

    public string StatusSummary
    {
        get
        {
            if (NetworkServer.active && NetworkClient.isConnected)
            {
                return $"Host running on {networkAddress}:{GetActivePort()}";
            }

            if (NetworkServer.active)
            {
                return $"Server running on port {GetActivePort()}";
            }

            if (NetworkClient.isConnected)
            {
                return $"Client connected to {networkAddress}:{GetActivePort()}";
            }

            if (NetworkClient.active)
            {
                return $"Connecting to {networkAddress}:{GetActivePort()}...";
            }

            return $"Offline. Address: {networkAddress}:{GetActivePort()}";
        }
    }

    public override void Awake()
    {
        dontDestroyOnLoad = false;
        EnsureTransport();
        EnsureRuntimePlayerPrefab();
        EnsureHud();
        CacheTourSystem();
        base.Awake();
    }

    public override void Start()
    {
        base.Start();

        if (!Application.isPlaying || isNetworkActive)
        {
            return;
        }

        if (startHostOnPlay)
        {
            StartHostedTour();
        }
        else if (startClientOnPlay)
        {
            StartTourClient();
        }
    }

    private void OnEnable()
    {
        SubscribeToTourEvents();
    }

    private void OnDisable()
    {
        UnsubscribeFromTourEvents();
    }

    public override void Update()
    {
        base.Update();

        if (Input.GetKeyDown(startHostKey) && !isNetworkActive)
        {
            StartHostedTour();
        }

        if (Input.GetKeyDown(startClientKey) && !isNetworkActive)
        {
            StartTourClient();
        }

        if (Input.GetKeyDown(stopNetworkKey) && isNetworkActive)
        {
            StopNetworking();
        }

        if (Input.GetKeyDown(toggleGuideSyncKey))
        {
            ToggleGuideSync();
        }
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        NetworkServer.RegisterHandler<TourStopRequestMessage>(OnTourStopRequested);
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        NetworkClient.ReplaceHandler<TourStopSyncMessage>(OnTourStopSynchronized, false);
    }

    public override void OnServerReady(NetworkConnectionToClient conn)
    {
        base.OnServerReady(conn);
        SendCurrentTourState(conn);
    }

    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        var startPosition = GetStartPosition();
        var playerObject = startPosition != null
            ? Instantiate(playerPrefab, startPosition.position, startPosition.rotation)
            : Instantiate(playerPrefab);

        playerObject.name = $"Tour Visitor [connId={conn.connectionId}]";
        NetworkServer.AddPlayerForConnection(conn, playerObject);
        SendCurrentTourState(conn);
    }

    public void StartHostedTour()
    {
        if (!isNetworkActive)
        {
            StartHost();
        }
    }

    public void StartTourClient()
    {
        if (!isNetworkActive)
        {
            StartClient();
        }
    }

    public void StopNetworking()
    {
        if (NetworkServer.active && NetworkClient.isConnected)
        {
            StopHost();
        }
        else if (NetworkClient.isConnected || NetworkClient.active)
        {
            StopClient();
        }
        else if (NetworkServer.active)
        {
            StopServer();
        }
    }

    public void ToggleGuideSync()
    {
        synchronizeGuideStops = !synchronizeGuideStops;
        if (NetworkServer.active && synchronizeGuideStops)
        {
            BroadcastCurrentTourState();
        }
    }

    public void RequestGuideStop(int stopIndex)
    {
        if (!allowClientStopRequests || !NetworkClient.isConnected || NetworkServer.active)
        {
            return;
        }

        NetworkClient.Send(new TourStopRequestMessage { stopIndex = stopIndex });
    }

    public void ApplyExternalConfiguration(
        string address,
        ushort port,
        string playerLabel,
        bool autoHost,
        bool autoClient,
        bool allowClientRequests)
    {
        networkAddress = string.IsNullOrWhiteSpace(address) ? "localhost" : address.Trim();
        telepathyPort = port;
        localPlayerLabel = string.IsNullOrWhiteSpace(playerLabel) ? "Visitor" : playerLabel.Trim();
        startHostOnPlay = autoHost;
        startClientOnPlay = autoClient && !autoHost;
        allowClientStopRequests = allowClientRequests;

        if (transport is TelepathyTransport telepathyTransport)
        {
            telepathyTransport.Port = telepathyPort;
        }
    }

    private void EnsureTransport()
    {
        if (!autoCreateTelepathyTransport)
        {
            if (transport == null)
            {
                transport = GetComponent<TelepathyTransport>();
            }

            return;
        }

        if (!TryGetComponent(out TelepathyTransport telepathyTransport))
        {
            telepathyTransport = gameObject.AddComponent<TelepathyTransport>();
        }

        telepathyTransport.Port = telepathyPort;
        transport = telepathyTransport;
        networkAddress = string.IsNullOrWhiteSpace(networkAddress) ? "localhost" : networkAddress;
    }

    private void EnsureRuntimePlayerPrefab()
    {
        if (playerPrefab != null)
        {
            return;
        }

        var prefabRoot = new GameObject("Tour Network Player Prefab");
        prefabRoot.hideFlags = HideFlags.HideInHierarchy;

        var identity = prefabRoot.AddComponent<NetworkIdentity>();
        AssignRuntimeAssetId(identity, new Guid(RuntimePlayerGuid));

        var networkTransform = prefabRoot.AddComponent<NetworkTransformUnreliable>();
        networkTransform.syncDirection = SyncDirection.ClientToServer;
        networkTransform.coordinateSpace = CoordinateSpace.World;
        networkTransform.target = prefabRoot.transform;
        networkTransform.syncPosition = true;
        networkTransform.syncRotation = true;
        networkTransform.syncScale = false;
        networkTransform.onlySyncOnChange = false;
        networkTransform.interpolatePosition = true;
        networkTransform.interpolateRotation = true;

        prefabRoot.AddComponent<TourNetworkPlayer>();

        playerPrefab = prefabRoot;
    }

    private void AssignRuntimeAssetId(NetworkIdentity identity, Guid guid)
    {
        if (identity == null)
        {
            return;
        }

        if (NetworkIdentityAssetIdField == null)
        {
            Debug.LogError("Mirror NetworkIdentity asset id field could not be found, so the runtime player prefab was not initialized correctly.", this);
            return;
        }

        var assetId = NetworkIdentity.AssetGuidToUint(guid);
        NetworkIdentityAssetIdField.SetValue(identity, assetId);
    }

    private void EnsureHud()
    {
        if (!addDesktopNetworkHud || GetComponent<NetworkManagerHUD>() != null)
        {
            return;
        }

        gameObject.AddComponent<NetworkManagerHUD>();
    }

    private void CacheTourSystem()
    {
        if (tourSystem == null)
        {
            tourSystem = GetComponent<TourSystem>();
        }
    }

    private void SubscribeToTourEvents()
    {
        CacheTourSystem();

        if (tourSystem == null)
        {
            return;
        }

        tourSystem.StopChanged -= OnTourStopChanged;
        tourSystem.AutoTourModeChanged -= OnAutoTourModeChanged;
        tourSystem.StopChanged += OnTourStopChanged;
        tourSystem.AutoTourModeChanged += OnAutoTourModeChanged;
    }

    private void UnsubscribeFromTourEvents()
    {
        if (tourSystem == null)
        {
            return;
        }

        tourSystem.StopChanged -= OnTourStopChanged;
        tourSystem.AutoTourModeChanged -= OnAutoTourModeChanged;
    }

    private void OnTourStopChanged(int stopIndex, TourSystem.TourStop stop)
    {
        if (applyingRemoteState || !NetworkServer.active || !synchronizeGuideStops)
        {
            return;
        }

        BroadcastCurrentTourState();
    }

    private void OnAutoTourModeChanged(bool enabled)
    {
        if (applyingRemoteState || !NetworkServer.active || !synchronizeGuideStops)
        {
            return;
        }

        BroadcastCurrentTourState();
    }

    private void BroadcastCurrentTourState()
    {
        if (tourSystem == null || !NetworkServer.active || !tourSystem.HasActiveStop)
        {
            return;
        }

        NetworkServer.SendToAll(new TourStopSyncMessage
        {
            stopIndex = tourSystem.CurrentIndex,
            autoTourEnabled = tourSystem.AutoTourEnabled,
        });
    }

    private void SendCurrentTourState(NetworkConnectionToClient connection)
    {
        if (connection == null || tourSystem == null || !tourSystem.HasActiveStop)
        {
            return;
        }

        connection.Send(new TourStopSyncMessage
        {
            stopIndex = tourSystem.CurrentIndex,
            autoTourEnabled = tourSystem.AutoTourEnabled,
        });
    }

    private void OnTourStopSynchronized(TourStopSyncMessage message)
    {
        CacheTourSystem();

        if (tourSystem == null)
        {
            return;
        }

        applyingRemoteState = true;
        tourSystem.SetAutoTour(message.autoTourEnabled);
        tourSystem.GoToStop(message.stopIndex);
        applyingRemoteState = false;
    }

    private void OnTourStopRequested(NetworkConnectionToClient connection, TourStopRequestMessage message)
    {
        if (!allowClientStopRequests || !NetworkServer.active || tourSystem == null)
        {
            return;
        }

        applyingRemoteState = true;
        tourSystem.GoToStop(message.stopIndex);
        applyingRemoteState = false;
        BroadcastCurrentTourState();
    }

    private int GetActivePort()
    {
        if (transport is PortTransport portTransport)
        {
            return portTransport.Port;
        }

        return telepathyPort;
    }
}
