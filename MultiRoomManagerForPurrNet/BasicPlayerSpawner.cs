using PurrNet;

public class BasicPlayerSpawner : NetworkBehaviour
{
    public NetworkIdentity roomPlayerPrefab;

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
        NetworkIdentity newRoomPlayer = (NetworkIdentity)UnityProxy.Instantiate(roomPlayerPrefab, gameObject.scene);
        newRoomPlayer.GiveOwnership(info.sender);
    }
}
