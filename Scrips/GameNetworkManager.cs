using System;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

public class GameNetworkManager : MonoBehaviour
{
    public static GameNetworkManager Instance { get; private set; }

    public string JoinCode { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private async void Start()
    {
        // [Contract 0-2, 0-3] Unity Services 및 익명 로그인 초기화
        try
        {
            await UnityServices.InitializeAsync();
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log($"[GameNetworkManager] 유니티 서비스 익명 로그인 성공. PlayerID: {AuthenticationService.Instance.PlayerId}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[GameNetworkManager] 유니티 서비스 초기화 실패: {e}");
        }
    }

    /// <summary>
    /// [Contract 0-5] 호스트 시작 (Relay 할당 및 Join Code 생성)
    /// </summary>
    public async Task<string> StartHost()
    {
        try
        {
            // 최대 4명의 클라이언트 접속 허용 (총 5인)
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(4);
            JoinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            if (NetworkManager.Singleton == null)
            {
                throw new Exception("NetworkManager.Singleton이 null입니다! 씬에 NetworkManager 오브젝트가 존재하는지 확인하세요.");
            }

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetHostRelayData(
                allocation.RelayServer.IpV4, 
                (ushort)allocation.RelayServer.Port, 
                allocation.AllocationIdBytes, 
                allocation.Key, 
                allocation.ConnectionData
            );

            NetworkManager.Singleton.StartHost();
            Debug.Log($"[GameNetworkManager] Host 시작됨. Join Code: {JoinCode}");
            
            return JoinCode;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameNetworkManager] 릴레이 서버 할당 실패(서비스 장애 의심): {e.Message}");
            Debug.Log("[GameNetworkManager] [로컬 폴백] 로컬 호스트(127.0.0.1)로 게임을 시작합니다.");
            
            JoinCode = "LOCAL_HOST";
            if (NetworkManager.Singleton != null)
            {
                var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
                transport.SetConnectionData("127.0.0.1", 7777);
                NetworkManager.Singleton.StartHost();
            }
            return JoinCode;
        }
    }

    /// <summary>
    /// [Contract 0-5] 클라이언트 접속 (Join Code 이용)
    /// </summary>
    public async Task<bool> JoinRoom(string joinCode)
    {
        try
        {
            if (NetworkManager.Singleton == null)
            {
                throw new Exception("NetworkManager.Singleton이 null입니다! 씬에 NetworkManager 오브젝트가 존재하는지 확인하세요.");
            }

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();

            // 로컬 폴백 코드인 경우 릴레이를 거치지 않고 바로 로컬 접속
            if (joinCode == "LOCAL_HOST")
            {
                Debug.Log("[GameNetworkManager] [로컬 폴백] 로컬 호스트(127.0.0.1)로 접속을 시도합니다.");
                transport.SetConnectionData("127.0.0.1", 7777);
                return NetworkManager.Singleton.StartClient();
            }

            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
            
            transport.SetClientRelayData(
                joinAllocation.RelayServer.IpV4, 
                (ushort)joinAllocation.RelayServer.Port, 
                joinAllocation.AllocationIdBytes, 
                joinAllocation.Key, 
                joinAllocation.ConnectionData, 
                joinAllocation.HostConnectionData
            );

            bool success = NetworkManager.Singleton.StartClient();
            Debug.Log($"[GameNetworkManager] 클라이언트 접속 요청. Code: {joinCode}. Success: {success}");
            return success;
        }
        catch (Exception e)
        {
            Debug.LogError($"[GameNetworkManager] 룸 접속 실패: {e}");
            return false;
        }
    }
}
