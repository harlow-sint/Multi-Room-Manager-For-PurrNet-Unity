using PurrNet;
using System.Collections.Generic;

public class BasicPlayerSpawner : NetworkBehaviour
{
    public NetworkIdentity roomPlayerPrefab;

    private readonly HashSet<PlayerID> _spawnedPlayers = new();

    void Start()
    {
        // only the client should ask for a spawn, otherwise host double-spawns
        if (isServer) return;

        if (networkManager.clientState == PurrNet.Transports.ConnectionState.Connected)
        {
            SpawnPlayer();
        }
    }

    [ServerRpc(requireOwnership:false)]
    void SpawnPlayer(RPCInfo info = default)
    {
        if (_spawnedPlayers.Contains(info.sender)) return;

        _spawnedPlayers.Add(info.sender);
        NetworkIdentity newRoomPlayer = (NetworkIdentity)UnityProxy.Instantiate(roomPlayerPrefab, gameObject.scene);
        newRoomPlayer.GiveOwnership(info.sender);
    }
}
