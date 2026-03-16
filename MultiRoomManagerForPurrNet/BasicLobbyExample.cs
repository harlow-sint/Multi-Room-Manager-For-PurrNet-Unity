using PurrNet;
using System.Collections.Generic;
using UnityEngine;

public class BasicLobbyExample : MonoBehaviour
{
    string nameField = "Room";
    string dataField = "";
    string sceneField = "RoomScene";
    string maxField = "12";

    const int panelWidth = 340;
    const int marginRight = 10;

    // cached to avoid per-frame GUILayoutOption allocations
    static readonly GUILayoutOption _w120 = GUILayout.Width(120);
    static readonly GUILayoutOption _w200 = GUILayout.Width(200);
    static readonly GUILayoutOption _w100 = GUILayout.Width(100);
    static readonly GUILayoutOption _w150 = GUILayout.Width(150);
    static readonly GUILayoutOption _h20 = GUILayout.Height(20);

    void OnEnable()
    {
        nameField = "Room " + Random.Range(100, 999).ToString();
    }

    void Start()
    {
        MultiRoomNetworkManager.networkManager.Subscribe<RoomListResponseMessage>(OnRoomList, asServer: false);
    }

    void OnDestroy()
    {
        MultiRoomNetworkManager.networkManager.Unsubscribe<RoomListResponseMessage>(OnRoomList, asServer: false);
    }

    void OnGUI()
    {
        if (MultiRoomNetworkManager.networkManager.clientState != PurrNet.Transports.ConnectionState.Connected)
        {
            GUILayout.BeginArea(new Rect(Screen.width - 210, 10, 200, 20));
            GUILayout.Label("Not connected");
            GUILayout.EndArea();
            return;
        }

        int baseX = Screen.width - panelWidth - marginRight;
        GUILayout.BeginArea(new Rect(baseX, 10, panelWidth, Screen.height - 10));
        GUILayout.Label("Create Room", _h20);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Room Name", _w120);
        nameField = GUILayout.TextField(nameField, _w200);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Room Data", _w120);
        dataField = GUILayout.TextField(dataField, _w200);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Room Scene", _w120);
        sceneField = GUILayout.TextField(sceneField, _w200);
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Max Players", _w120);
        maxField = GUILayout.TextField(maxField, _w200);
        GUILayout.EndHorizontal();

        GUILayout.Space(10);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Create Room", _w100))
        {
            if (int.TryParse(maxField, out int m))
            {
                var msg = new CreateRoomMessage
                {
                    roomName = nameField,
                    roomData = dataField,
                    sceneName = sceneField,
                    maxPlayers = m
                };

                MultiRoomNetworkManager.networkManager.SendToServer(msg);
            }
            Destroy(this.gameObject);
        }

        if (GUILayout.Button("Refresh Room List", _w150))
        {
            MultiRoomNetworkManager.networkManager.SendToServer(new RoomListRequestMessage());
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(20);
        GUILayout.Label("Room List", _h20);
        foreach (var e in rooms)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button($"Join {e.name} ({e.cur}/{e.max})", _w200))
            {
                MultiRoomNetworkManager.networkManager.SendToServer(new JoinRoomMessage { roomName = e.name });
                Destroy(this.gameObject);
            }

            GUILayout.Label("Data: " + e.data);
            GUILayout.EndHorizontal();
            GUILayout.Space(5);
        }

        GUILayout.EndArea();
    }

    struct Entry { public string name, data, scene; public int cur, max; }
    List<Entry> rooms = new List<Entry>();
    void OnRoomList(PlayerID sender, RoomListResponseMessage msg, bool asServer)
    {
        rooms.Clear();
        if (msg.roomNames == null) return;

        for (int i = 0; i < msg.roomNames.Length; i++)
        {
            rooms.Add(new Entry
            {
                name = msg.roomNames[i],
                data = msg.roomDatas[i],
                scene = msg.sceneNames[i],
                cur = msg.currentCounts[i],
                max = msg.maxCounts[i]
            });
        }
    }
}
