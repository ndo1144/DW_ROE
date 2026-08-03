using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// [도메인 명세 §3.9] 제단 컴포넌트.
/// 플레이어가 F키를 눌러 8초간 상호작용하면 영혼을 죄업 게이지로 헌납합니다.
/// </summary>
public class AltarController : NetworkBehaviour, IInteractable
{
    [Header("헌납 설정")]
    public float donateDuration = 8f;
    public float cancelDistance = 1.0f;

    [Header("시각 효과")]
    [Tooltip("헌납 중에만 활성화할 파티클 오브젝트들 (예: PS_Parent, PS_Parent (1))")]
    public GameObject[] activeDuringDonateParticles;

    // 현재 헌납 중인 플레이어 (0이면 아무도 없음)
    public NetworkVariable<ulong> currentDonatorClientId = new NetworkVariable<ulong>(ulong.MaxValue);
    // 헌납 진행도 (0~1)
    public NetworkVariable<float> donateProgress = new NetworkVariable<float>(0f);

    private Coroutine _donateCoroutine;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // 헌납 상태 변경 시 클라이언트 측에서 파티클 켜고 끄기 동기화
        currentDonatorClientId.OnValueChanged += OnDonatorChanged;
        OnDonatorChanged(ulong.MaxValue, currentDonatorClientId.Value); // 초기 상태 갱신

        if (IsServer)
        {
            currentDonatorClientId.Value = ulong.MaxValue;
            donateProgress.Value = 0f;
        }

        // 클라이언트에서도 UI 연동을 위해 자신을 매니저에 등록해야 합니다.
        if (AltarManager.Instance != null)
        {
            AltarManager.Instance.RegisterAltar(this);
        }
    }

    private void Start()
    {
        // OnNetworkSpawn 때 매니저가 없었다면 Start에서 재등록 시도
        if (AltarManager.Instance != null)
        {
            AltarManager.Instance.RegisterAltar(this);
        }
    }

    public override void OnNetworkDespawn()
    {
        currentDonatorClientId.OnValueChanged -= OnDonatorChanged;
        base.OnNetworkDespawn();
        
        if (AltarManager.Instance != null)
        {
            AltarManager.Instance.UnregisterAltar(this);
        }
    }

    private void OnDonatorChanged(ulong previousValue, ulong newValue)
    {
        bool isDonating = (newValue != ulong.MaxValue);
        if (activeDuringDonateParticles != null)
        {
            foreach (var particleObj in activeDuringDonateParticles)
            {
                if (particleObj != null)
                {
                    particleObj.SetActive(isDonating);
                }
            }
        }
    }

    public bool CanInteract(GameObject player)
    {
        // 아무도 사용 중이지 않을 때만 대상 지정 가능 (영혼 부족 여부는 헌납 시 체크)
        return currentDonatorClientId.Value == ulong.MaxValue;
    }

    public string GetInteractPrompt()
    {
        return "[F] 헌납하기";
    }

    public void Interact(GameObject player)
    {
        Debug.Log($"[AltarController] 클라이언트에서 제단 상호작용 시도됨. 플레이어: {player.name}");
        var sinComp = player.GetComponent<PlayerSinComponent>();
        if (sinComp == null || sinComp.souls.Value <= 0)
        {
            Debug.LogWarning("[AltarController] 헌납할 영혼이 없습니다! (클라이언트 차단)");
            return;
        }

        var netObj = player.GetComponent<NetworkObject>();
        if (netObj != null)
        {
            Debug.Log($"[AltarController] 서버로 헌납 시작 요청 (DonatorID: {netObj.OwnerClientId})");
            StartDonate_ServerRpc(netObj.OwnerClientId);
        }
    }

    public void CancelInteract(GameObject player)
    {
        var netObj = player.GetComponent<NetworkObject>();
        if (netObj != null)
        {
            CancelDonate_ServerRpc(netObj.OwnerClientId);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void StartDonate_ServerRpc(ulong donatorId, ServerRpcParams rpcParams = default)
    {
        if (currentDonatorClientId.Value != ulong.MaxValue)
        {
            Client_DonateDebugClientRpc($"서버 거부: 이미 사용 중 (DonatorID: {currentDonatorClientId.Value})");
            return;
        }

        var clientObj = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(donatorId);
        if (clientObj == null)
        {
            Client_DonateDebugClientRpc($"서버 거부: 플레이어({donatorId})를 찾을 수 없음");
            return;
        }

        var sinComp = clientObj.GetComponent<PlayerSinComponent>();
        if (sinComp == null || sinComp.souls.Value <= 0)
        {
            int soulCount = sinComp != null ? sinComp.souls.Value : -1;
            Client_DonateDebugClientRpc($"서버 거부: 영혼 부족 (보유: {soulCount})");
            return;
        }

        currentDonatorClientId.Value = donatorId;
        donateProgress.Value = 0f;

        if (PlayerManager.Instance != null)
        {
            var session = PlayerManager.Instance.GetSessionByClientId(donatorId);
            if (session != null)
            {
                EventBus.RaiseAltarDonateStart(session, this);
            }
        }

        Client_PlayDonateSfxClientRpc((int)SFXType.AltarDonateStart);
        Client_DonateDebugClientRpc($"✅ 서버 승인! 헌납 시작 (DonatorID: {donatorId}, 영혼: {sinComp.souls.Value})");

        Debug.Log($"[AltarController-Server] {clientObj.name} 헌납 시작 성공!");

        if (_donateCoroutine != null) StopCoroutine(_donateCoroutine);
        _donateCoroutine = StartCoroutine(DonateRoutine(clientObj.gameObject, sinComp));
    }

    [ClientRpc]
    private void Client_DonateDebugClientRpc(string message)
    {
        Debug.Log($"[AltarController-클라이언트수신] {message}");
    }

    [ServerRpc(RequireOwnership = false)]
    private void CancelDonate_ServerRpc(ulong donatorId, ServerRpcParams rpcParams = default)
    {
        if (currentDonatorClientId.Value == donatorId)
        {
            CancelDonate("플레이어가 상호작용 키를 뗌");
        }
    }

    // 헌납음은 호스트/클라이언트 모두 이 ClientRpc로만 재생한다(EventBus 경로 제거).
    // 8초간 이어지는 소리이므로 loop=true로 재생하고, 완료/취소 시 StopSFX로 끊는다.
    [ClientRpc]
    private void Client_PlayDonateSfxClientRpc(int sfxType)
    {
        SoundManager.Instance?.PlaySFX((SFXType)sfxType, transform.position, loop: true);
    }

    [ClientRpc]
    private void Client_StopDonateSfxClientRpc(int sfxType)
    {
        SoundManager.Instance?.StopSFX((SFXType)sfxType);
    }

    private IEnumerator DonateRoutine(GameObject playerObj, PlayerSinComponent sinComp)
    {
        Vector3 startPos = playerObj.transform.position;
        float elapsed = 0f;

        while (elapsed < donateDuration)
        {
            // 1m 이상 이동 시 취소
            if (Vector3.Distance(startPos, playerObj.transform.position) > cancelDistance)
            {
                CancelDonate("이동으로 인해");
                yield break;
            }

            // [시야 점유율 검사] 제단을 대략 바라보고만 있으면 유지 (너무 엄격하면 첫 프레임에 취소되어 진척도 UI가 안 뜸)
            float viewRatio = GetAltarViewportRatio(playerObj);
            if (viewRatio < 0.1f)
            {
                CancelDonate($"시야에서 제단이 벗어남 ({viewRatio * 100f:F0}%)");
                yield break;
            }

            elapsed += Time.deltaTime;
            donateProgress.Value = elapsed / donateDuration;
            yield return null;
        }

        // 헌납 성공
        CompleteDonate(playerObj, sinComp);
    }

    /// <summary>
    /// 플레이어가 제단을 얼마나 정확히/가까이 바라보고 있는지 거리와 각도를 이용해 점유율(0~1)로 근사 계산합니다.
    /// (멀티플레이어 환경에서 서버는 클라이언트의 실제 Camera.main을 알 수 없고, 너무 가까우면 클리핑 현상으로 0이 나오기 때문입니다.)
    /// </summary>
    private float GetAltarViewportRatio(GameObject playerObj)
    {
        if (playerObj == null) return 0f;

        Vector3 toAltar = transform.position - playerObj.transform.position;
        // Y축(높이) 차이는 무시하고 평면상에서 바라보는 각도를 계산 (카메라 상하 각도 동기화 문제 우회)
        toAltar.y = 0; 
        Vector3 playerForward = playerObj.transform.forward;
        playerForward.y = 0;

        float distance = toAltar.magnitude;
        if (distance == 0) return 1f;

        float dot = Vector3.Dot(playerForward.normalized, toAltar.normalized);
        
        // 1. 시야각 체크: 대략 정면 쪽(dot > 0.1)을 바라보고 있으면 통과 (너무 엄격하면 진척도 UI가 즉시 취소됨)
        if (dot < 0.1f) return 0f;

        // 2. 거리 체크: 가까울수록 화면을 많이 차지한다고 근사
        // 거리가 cancelDistance(1.0f) 이하일 때 최대 점유율을 가짐
        float distanceRatio = Mathf.Clamp01(1.5f / Mathf.Max(distance, 0.1f)); 
        
        // 3. 각도 체크: 정확히 바라볼수록 점유율 높음
        float lookRatio = dot;

        // 최종 근사 점유율 (최소 0.5를 넘기 위해서는 충분히 가깝고 똑바로 바라봐야 함)
        return Mathf.Clamp01(distanceRatio * lookRatio);
    }

    private void CancelDonate(string reason = "")
    {
        if (!IsServer) return;

        if (PlayerManager.Instance != null)
        {
            var session = PlayerManager.Instance.GetSessionByClientId(currentDonatorClientId.Value);
            if (session != null)
            {
                EventBus.RaiseAltarDonateInterrupted(session, this);
            }
        }

        currentDonatorClientId.Value = ulong.MaxValue;
        donateProgress.Value = 0f;
        _donateCoroutine = null;
        
        Client_StopDonateSfxClientRpc((int)SFXType.AltarDonateStart);
        Client_DonateDebugClientRpc($"❌ 서버가 헌납을 취소함! 사유: {reason}");
        Debug.Log($"[AltarController] {gameObject.name}: 헌납 취소됨. 사유: {reason}");
    }

    private void CompleteDonate(GameObject playerObj, PlayerSinComponent sinComp)
    {
        if (!IsServer) return;
        
        int soulsToDonate = sinComp.souls.Value;
        var netObj = playerObj.GetComponent<NetworkObject>();
        PlayerSession session = null;
        if (PlayerManager.Instance != null)
        {
            session = PlayerManager.Instance.GetSessionByNetworkObject(netObj);
        }

        if (session == null) 
        {
            session = new PlayerSession {
                avatarObject = netObj,
                role = PlayerRole.Criminal
            };
        }
        
        if (SinManager.Instance != null)
        {
            SinManager.Instance.DonateAtAltar(session, soulsToDonate);
        }
        else
        {
            Debug.LogError("[AltarController] 씬에 SinManager가 없습니다! 헌납 로직이 비정상적으로 작동합니다. 임시로 영혼만 차감합니다.");
            sinComp.souls.Value -= soulsToDonate; // 매니저가 없을 때를 대비한 안전 차감
        }

        currentDonatorClientId.Value = ulong.MaxValue;
        donateProgress.Value = 0f;
        _donateCoroutine = null;
        
        EventBus.RaiseAltarDonateComplete(session, this);
        Client_StopDonateSfxClientRpc((int)SFXType.AltarDonateStart);

        Debug.Log($"[AltarController] {gameObject.name}: {soulsToDonate} 영혼 헌납 완료!");
    }
}
