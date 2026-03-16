using PurrNet;
using PurrNet.Modules;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MultiRoomNetworkManager : MonoBehaviour
{
    public static MultiRoomNetworkManager Instance;
    public static NetworkManager networkManager;

    public NetworkIdentity lobbyPlayerPrefab;
    public string[] allowedSceneNames;
    public int maxRoomNameLength = 64;
    public int maxRoomDataLength = 256;
    public int maxPlayersPerRoom = 32;
    public int maxTotalRooms = 50;
    public float createRoomCooldown = 5f;
    public float joinRoomCooldown = 2f;
    public float roomListCooldown = 1f;
    public float leaveRoomCooldown = 1f;
    public bool allowJoinInProgress = true;

    public Dictionary<string, RoomInfo> rooms = new();

    public enum RoomState
    {
        InProgress,
        Ended
    }

    public class RoomInfo
    {
        public string roomName;
        public string roomData;
        public string sceneName;
        public int currentPlayers;
        public int maxPlayers;
        public SceneID scene;
        public PlayerID roomMaster;
        public RoomState state;
        public List<PlayerID> playerConnections = new List<PlayerID>();
    }

    private readonly Dictionary<PlayerID, RoomInfo> playerToRoom = new();

    // need to track who's actually connected because purrnet doesn't
    // tell us in a way that's easy to check later
    private readonly HashSet<PlayerID> _connectedPlayers = new();

    // tracks room names that are currently being created (scene still loading)
    // so two people can't make a room with the same name at the same time
    private readonly HashSet<string> _pendingRoomNames = new();

    // only one room creation coroutine runs at a time so lastSceneId doesn't get clobbered
    // when two rooms try to load simultaneously
    private readonly Queue<(PlayerID player, CreateRoomMessage msg)> _roomCreationQueue = new();
    private bool _isCreatingRoom;

    // tracks last time each player sent each message type
    private readonly Dictionary<PlayerID, Dictionary<System.Type, float>> _playerMessageTimestamps = new();


    private void Awake()
    {
        if (Instance != null)
            Destroy(Instance.gameObject);

        Instance = this;
        networkManager = GetComponent<NetworkManager>();
        DontDestroyOnLoad(this.gameObject);

        networkManager.onPlayerJoined += NetworkManager_onPlayerJoined;
        networkManager.onPlayerLeft += NetworkManager_onPlayerLeft;
        networkManager.onServerConnectionState += NetworkManager_onServerConnectionState;
        networkManager.onClientConnectionState += NetworkManager_onClientConnectionState;

        networkManager.Subscribe<RoomListRequestMessage>(OnRoomListRequest, asServer: true);
        networkManager.Subscribe<CreateRoomMessage>(OnCreateRoom, asServer: true);
        networkManager.Subscribe<JoinRoomMessage>(OnJoinRoom, asServer: true);
        networkManager.Subscribe<LeaveRoomMessage>(OnLeaveRoom, asServer: true);
    }

    private void OnDestroy()
    {
        networkManager.onPlayerJoined -= NetworkManager_onPlayerJoined;
        networkManager.onPlayerLeft -= NetworkManager_onPlayerLeft;
        networkManager.onServerConnectionState -= NetworkManager_onServerConnectionState;
        networkManager.onClientConnectionState -= NetworkManager_onClientConnectionState;

        networkManager.Unsubscribe<RoomListRequestMessage>(OnRoomListRequest, asServer: true);
        networkManager.Unsubscribe<CreateRoomMessage>(OnCreateRoom, asServer: true);
        networkManager.Unsubscribe<JoinRoomMessage>(OnJoinRoom, asServer: true);
        networkManager.Unsubscribe<LeaveRoomMessage>(OnLeaveRoom, asServer: true);
    }

    private bool IsRateLimited(PlayerID player, System.Type msgType, float cooldown)
    {
        float now = Time.unscaledTime;

        if (!_playerMessageTimestamps.TryGetValue(player, out var timestamps))
        {
            timestamps = new Dictionary<System.Type, float>();
            _playerMessageTimestamps[player] = timestamps;
        }

        if (timestamps.TryGetValue(msgType, out float lastTime) && now - lastTime < cooldown)
            return true;

        timestamps[msgType] = now;
        return false;
    }

    private void NetworkManager_onPlayerJoined(PlayerID player, bool isReconnect, bool asServer)
    {
        if (!asServer) return;

        _connectedPlayers.Add(player);

        if (lobbyPlayerPrefab != null)
        {
            NetworkIdentity newLobbyPlayer = Instantiate(lobbyPlayerPrefab);
            newLobbyPlayer.GiveOwnership(player);
        }
    }

    private void NetworkManager_onPlayerLeft(PlayerID player, bool asServer)
    {
        if (!asServer) return;

        _connectedPlayers.Remove(player);
        _playerMessageTimestamps.Remove(player);

        // if they had a pending room creation in the queue, clean it up
        // so the room name doesn't stay locked and we don't waste a scene load
        CleanupPlayerFromQueue(player);

        RemovePlayerFromRoom(player);
    }

    private void RemovePlayerFromRoom(PlayerID player)
    {
        if (!playerToRoom.TryGetValue(player, out RoomInfo info))
            return;

        info.currentPlayers--;
        info.playerConnections.Remove(player);
        playerToRoom.Remove(player);

        // if the room master left, promote the next player or close the room
        if (info.roomMaster.Equals(player) && info.currentPlayers > 0)
        {
            info.roomMaster = info.playerConnections[0];
        }

        if (info.currentPlayers <= 0)
        {
            StartCoroutine(UnloadEmptyScene(info.scene));
            rooms.Remove(info.roomName);
        }
    }

    // removes a disconnected player's entries from the creation queue
    private void CleanupPlayerFromQueue(PlayerID player)
    {
        int count = _roomCreationQueue.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = _roomCreationQueue.Dequeue();
            if (entry.player.Equals(player))
            {
                _pendingRoomNames.Remove(entry.msg.roomName);
            }
            else
            {
                _roomCreationQueue.Enqueue(entry);
            }
        }
    }

    private void NetworkManager_onServerConnectionState(PurrNet.Transports.ConnectionState state)
    {
        if (state == PurrNet.Transports.ConnectionState.Disconnected)
        {
            SceneManager.LoadScene(0);
            Destroy(this.gameObject);
        }
    }

    private void NetworkManager_onClientConnectionState(PurrNet.Transports.ConnectionState state)
    {
        if (state == PurrNet.Transports.ConnectionState.Disconnected)
        {
            SceneManager.LoadScene(0);
            Destroy(this.gameObject);
        }
    }

    private void OnRoomListRequest(PlayerID player, RoomListRequestMessage msg, bool asServer)
    {
        if (IsRateLimited(player, typeof(RoomListRequestMessage), roomListCooldown)) return;

        int n = rooms.Count;
        var resp = new RoomListResponseMessage
        {
            roomNames = new string[n],
            roomDatas = new string[n],
            sceneNames = new string[n],
            currentCounts = new int[n],
            maxCounts = new int[n],
            joinableFlags = new bool[n]
        };

        int i = 0;
        foreach (var r in rooms.Values)
        {
            resp.roomNames[i] = r.roomName;
            resp.roomDatas[i] = r.roomData;
            resp.sceneNames[i] = r.sceneName;
            resp.currentCounts[i] = r.currentPlayers;
            resp.maxCounts[i] = r.maxPlayers;
            resp.joinableFlags[i] = r.currentPlayers < r.maxPlayers
                && (allowJoinInProgress || r.state != RoomState.InProgress);
            i++;
        }

        networkManager.Send(player, resp);
    }

    private void OnCreateRoom(PlayerID player, CreateRoomMessage msg, bool asServer)
    {
        if (!asServer) return;
        if (IsRateLimited(player, typeof(CreateRoomMessage), createRoomCooldown)) return;

        if (playerToRoom.ContainsKey(player))
        {
            Debug.LogWarning($"[Server] Player {player} already in room; create ignored.");
            return;
        }

        if (string.IsNullOrWhiteSpace(msg.roomName) || msg.roomName.Length > maxRoomNameLength)
        {
            Debug.LogWarning($"[Server] Invalid room name from {player}; ignoring.");
            return;
        }

        if (msg.roomData != null && msg.roomData.Length > maxRoomDataLength)
        {
            Debug.LogWarning($"[Server] Room data too long from {player}; ignoring.");
            return;
        }

        if (msg.maxPlayers < 1 || msg.maxPlayers > maxPlayersPerRoom)
        {
            Debug.LogWarning($"[Server] Invalid maxPlayers ({msg.maxPlayers}) from {player}; ignoring.");
            return;
        }

        // check both existing rooms AND rooms that are still loading
        if (rooms.ContainsKey(msg.roomName) || _pendingRoomNames.Contains(msg.roomName))
        {
            Debug.LogWarning($"[Server] Room '{msg.roomName}' already exists; ignoring.");
            return;
        }

        // don't let clients load scenes that aren't in the allowed list
        if (allowedSceneNames != null && allowedSceneNames.Length > 0
            && System.Array.IndexOf(allowedSceneNames, msg.sceneName) < 0)
        {
            Debug.LogWarning($"[Server] Scene '{msg.sceneName}' not in allowed list, ignoring.");
            return;
        }

        if (rooms.Count + _pendingRoomNames.Count >= maxTotalRooms)
        {
            Debug.LogWarning($"[Server] Room limit reached ({maxTotalRooms}); ignoring create from {player}.");
            return;
        }

        _pendingRoomNames.Add(msg.roomName);
        _roomCreationQueue.Enqueue((player, msg));

        if (!_isCreatingRoom)
            StartCoroutine(ProcessRoomCreationQueue());
    }

    private void OnJoinRoom(PlayerID player, JoinRoomMessage msg, bool asServer)
    {
        if (!asServer) return;
        if (IsRateLimited(player, typeof(JoinRoomMessage), joinRoomCooldown)) return;
        if (playerToRoom.ContainsKey(player)) return;

        if (!rooms.TryGetValue(msg.roomName, out var info)) return;
        if (info.currentPlayers >= info.maxPlayers) return;
        if (!allowJoinInProgress && info.state == RoomState.InProgress) return;

        networkManager.scenePlayersModule.MovePlayerToSingleScene(player, info.scene);
        playerToRoom[player] = info;
        info.currentPlayers++;
        info.playerConnections.Add(player);
    }

    private void OnLeaveRoom(PlayerID player, LeaveRoomMessage msg, bool asServer)
    {
        if (!asServer) return;
        if (IsRateLimited(player, typeof(LeaveRoomMessage), leaveRoomCooldown)) return;
        if (!playerToRoom.ContainsKey(player)) return;

        RemovePlayerFromRoom(player);
    }

    public void CloseRoom(string roomName, string reason = "Room closed")
    {
        if (!rooms.TryGetValue(roomName, out var info)) return;

        var closedMsg = new RoomClosedMessage { reason = reason };

        for (int i = info.playerConnections.Count - 1; i >= 0; i--)
        {
            var player = info.playerConnections[i];
            networkManager.Send(player, closedMsg);
            playerToRoom.Remove(player);
        }

        info.playerConnections.Clear();
        info.currentPlayers = 0;

        StartCoroutine(UnloadEmptyScene(info.scene));
        rooms.Remove(roomName);
    }

    public void SetRoomState(string roomName, RoomState state)
    {
        if (rooms.TryGetValue(roomName, out var info))
            info.state = state;
    }

    public RoomInfo GetPlayerRoom(PlayerID player)
    {
        return playerToRoom.TryGetValue(player, out var info) ? info : null;
    }

    public bool IsRoomMaster(PlayerID player)
    {
        return playerToRoom.TryGetValue(player, out var info) && info.roomMaster.Equals(player);
    }

    IEnumerator ProcessRoomCreationQueue()
    {
        _isCreatingRoom = true;
        while (_roomCreationQueue.Count > 0)
        {
            var (player, msg) = _roomCreationQueue.Dequeue();
            yield return CreateRoomCoroutine(player, msg);
        }
        _isCreatingRoom = false;
    }

    IEnumerator CreateRoomCoroutine(PlayerID player, CreateRoomMessage msg)
    {
        var settings = new PurrSceneSettings();
        settings.isPublic = false;
        settings.mode = LoadSceneMode.Additive;
        var scene = networkManager.sceneModule.LoadSceneAsync(msg.sceneName, settings);
        while (!scene.isDone)
            yield return null;

        SceneID sceneID = networkManager.sceneModule.lastSceneId;

        _pendingRoomNames.Remove(msg.roomName);

        // if they disconnected or joined another room while we were loading,
        // unload the scene and bail
        if (!_connectedPlayers.Contains(player) || playerToRoom.ContainsKey(player))
        {
            StartCoroutine(UnloadEmptyScene(sceneID));
            yield break;
        }

        var info = new RoomInfo
        {
            roomName = msg.roomName,
            roomData = msg.roomData,
            sceneName = msg.sceneName,
            currentPlayers = 0,
            maxPlayers = msg.maxPlayers,
            scene = sceneID,
            roomMaster = player,
            state = RoomState.InProgress
        };

        networkManager.scenePlayersModule.MovePlayerToSingleScene(player, sceneID);
        playerToRoom[player] = info;
        info.currentPlayers++;
        info.playerConnections.Add(player);

        rooms[msg.roomName] = info;
    }

    IEnumerator UnloadEmptyScene(SceneID sceneID)
    {
        if (networkManager.sceneModule.TryGetSceneState(sceneID, out SceneState state))
        {
            var scene = networkManager.sceneModule.UnloadSceneAsync(state.scene);
            while (!scene.isDone)
                yield return null;
        }
    }
}
