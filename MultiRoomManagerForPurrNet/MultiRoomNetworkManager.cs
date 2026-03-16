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

    public Dictionary<string, RoomInfo> rooms = new();

    public class RoomInfo
    {
        public string roomName;
        public string roomData;
        public string sceneName;
        public int currentPlayers;
        public int maxPlayers;
        public SceneID scene;
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

        // if they had a pending room creation in the queue, clean it up
        // so the room name doesn't stay locked and we don't waste a scene load
        CleanupPlayerFromQueue(player);

        if (playerToRoom.TryGetValue(player, out RoomInfo info))
        {
            info.currentPlayers--;
            info.playerConnections.Remove(player);
            playerToRoom.Remove(player);

            if (info.currentPlayers <= 0)
            {
                StartCoroutine(UnloadEmptyScene(info.scene));
                rooms.Remove(info.roomName);
            }
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
        int n = rooms.Count;
        var resp = new RoomListResponseMessage
        {
            roomNames = new string[n],
            roomDatas = new string[n],
            sceneNames = new string[n],
            currentCounts = new int[n],
            maxCounts = new int[n]
        };

        int i = 0;
        foreach (var r in rooms.Values)
        {
            resp.roomNames[i] = r.roomName;
            resp.roomDatas[i] = r.roomData;
            resp.sceneNames[i] = r.sceneName;
            resp.currentCounts[i] = r.currentPlayers;
            resp.maxCounts[i] = r.maxPlayers;
            i++;
        }

        networkManager.Send(player, resp);
    }

    private void OnCreateRoom(PlayerID player, CreateRoomMessage msg, bool asServer)
    {
        if (!asServer) return;
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

        _pendingRoomNames.Add(msg.roomName);
        _roomCreationQueue.Enqueue((player, msg));

        if (!_isCreatingRoom)
            StartCoroutine(ProcessRoomCreationQueue());
    }

    private void OnJoinRoom(PlayerID player, JoinRoomMessage msg, bool asServer)
    {
        if (!asServer) return;
        if (playerToRoom.ContainsKey(player)) return;

        if (!rooms.TryGetValue(msg.roomName, out var info) || info.currentPlayers >= info.maxPlayers) return;

        networkManager.scenePlayersModule.MovePlayerToSingleScene(player, info.scene);
        playerToRoom[player] = info;
        info.currentPlayers++;
        info.playerConnections.Add(player);
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
            scene = sceneID
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
