using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public struct LobbyPlayerState : INetworkSerializable, IEquatable<LobbyPlayerState>
{
    public ulong ClientId;
    public FixedString32Bytes PlayerName;
    public CharacterId SelectedCharacter;
    public bool IsReady;
    public bool IsHost;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref PlayerName);
        serializer.SerializeValue(ref SelectedCharacter);
        serializer.SerializeValue(ref IsReady);
        serializer.SerializeValue(ref IsHost);
    }

    public bool Equals(LobbyPlayerState other)
    {
        return ClientId == other.ClientId &&
               PlayerName == other.PlayerName &&
               SelectedCharacter == other.SelectedCharacter &&
               IsReady == other.IsReady &&
               IsHost == other.IsHost;
    }
}

public class LobbyStateSync : NetworkBehaviour
{
    public static LobbyStateSync Instance { get; private set; }
    
    public NetworkList<LobbyPlayerState> LobbyPlayers;

    public event Action OnLobbyPlayersUpdated;

    // 씬 전환 시 파괴되는 Instance 대신 정보를 저장해둘 정적 딕셔너리
    public static System.Collections.Generic.Dictionary<ulong, CharacterId> SavedCharacterSelections = new System.Collections.Generic.Dictionary<ulong, CharacterId>();
    public static System.Collections.Generic.Dictionary<ulong, string> SavedPlayerNames = new System.Collections.Generic.Dictionary<ulong, string>();

    public static void SaveSelections()
    {
        SavedCharacterSelections.Clear();
        SavedPlayerNames.Clear();
        if (Instance != null)
        {
            foreach (var p in Instance.LobbyPlayers)
            {
                SavedCharacterSelections[p.ClientId] = p.SelectedCharacter;
                SavedPlayerNames[p.ClientId] = p.PlayerName.ToString();
                Debug.Log($"[LobbyStateSync] Client {p.ClientId}의 선택 캐릭터({p.SelectedCharacter}) 및 닉네임({p.PlayerName})을 저장했습니다.");
            }
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        LobbyPlayers = new NetworkList<LobbyPlayerState>();
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        
        if (Instance == this)
        {
            Instance = null;
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
        }
        
        if (LobbyPlayers != null)
        {
            LobbyPlayers.OnListChanged -= HandleLobbyPlayersChanged;
            // NetworkList is automatically disposed by NGO, so we don't call Dispose() here directly.
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;

            // 로컬 호스트 본인 추가
            AddPlayerToList(NetworkManager.Singleton.LocalClientId);
        }

        LobbyPlayers.OnListChanged += HandleLobbyPlayersChanged;
        
        // 스폰 시점에 이미 리스트가 있을 수 있으므로 수동으로 한번 호출
        OnLobbyPlayersUpdated?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
            }
        }
        LobbyPlayers.OnListChanged -= HandleLobbyPlayersChanged;
    }

    private void HandleClientConnected(ulong clientId)
    {
        AddPlayerToList(clientId);
    }

    private void HandleClientDisconnect(ulong clientId)
    {
        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            if (LobbyPlayers[i].ClientId == clientId)
            {
                LobbyPlayers.RemoveAt(i);
                break;
            }
        }
    }

    private void AddPlayerToList(ulong clientId)
    {
        // 중복 방지
        foreach (var player in LobbyPlayers)
        {
            if (player.ClientId == clientId) return;
        }

        bool isHost = (clientId == NetworkManager.ServerClientId);
        int playerIndex = LobbyPlayers.Count; // 호스트 추가 전이면 0, 첫 번째 참여자는 1
        string defaultName = isHost ? "Host" : $"Player{playerIndex}";

        LobbyPlayers.Add(new LobbyPlayerState
        {
            ClientId = clientId,
            PlayerName = new FixedString32Bytes(defaultName),
            SelectedCharacter = isHost ? CharacterId.Angel : CharacterId.Pereshte, // 기획상 대죄인/악인 분리
            IsReady = isHost, // 호스트는 기본적으로 레디(시작 버튼 권한자)
            IsHost = isHost
        });
    }

    private void HandleLobbyPlayersChanged(NetworkListEvent<LobbyPlayerState> changeEvent)
    {
        OnLobbyPlayersUpdated?.Invoke();
    }

    [ServerRpc(RequireOwnership = false)]
    public void SelectCharacterServerRpc(ulong clientId, CharacterId characterId)
    {
        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            if (LobbyPlayers[i].ClientId == clientId)
            {
                var state = LobbyPlayers[i];
                state.SelectedCharacter = characterId;
                LobbyPlayers[i] = state; // NetworkList 갱신 트리거
                break;
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void ChangeNameServerRpc(ulong clientId, string newName)
    {
        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            if (LobbyPlayers[i].ClientId == clientId)
            {
                var state = LobbyPlayers[i];
                state.PlayerName = new FixedString32Bytes(newName);
                LobbyPlayers[i] = state; // NetworkList 갱신 트리거
                break;
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void ToggleReadyServerRpc(ulong clientId)
    {
        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            if (LobbyPlayers[i].ClientId == clientId)
            {
                // 호스트는 레디 상태 변경 불가 (항상 게임 시작 권한)
                if (LobbyPlayers[i].IsHost) return;

                var state = LobbyPlayers[i];
                state.IsReady = !state.IsReady;
                LobbyPlayers[i] = state; // NetworkList 갱신 트리거
                break;
            }
        }
    }

    public bool AreAllClientsReady()
    {
        foreach (var player in LobbyPlayers)
        {
            if (!player.IsHost && !player.IsReady)
            {
                return false;
            }
        }
        return true;
    }

    [ServerRpc(RequireOwnership = false)]
    public void AddBotServerRpc()
    {
        if (LobbyPlayers.Count >= 5) return; // 최대 5인 (호스트 + 악인 4명)

        ulong botId = 9000 + (ulong)LobbyPlayers.Count;
        string botName = $"Bot_{LobbyPlayers.Count}";

        LobbyPlayers.Add(new LobbyPlayerState
        {
            ClientId = botId,
            PlayerName = new FixedString32Bytes(botName),
            SelectedCharacter = CharacterId.Pereshte, // 기본 봇 캐릭터 (원거리 악인)
            IsReady = true, // 봇은 항상 레디 상태
            IsHost = false
        });
        
        Debug.Log($"[LobbyStateSync] 봇 추가: {botName} (ID: {botId})");
    }
}
