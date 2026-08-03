using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// [도메인 명세 §3.1] AI Prefab 루트에 부착. NetworkBehaviour 상속.
/// 호스트만 FSM 실행, 클라는 시각 동기화만.
/// AIManager에 자동 등록·해제.
/// </summary>
[RequireComponent(typeof(AIFSMComponent))]
[RequireComponent(typeof(AIPerceptionComponent))]
[RequireComponent(typeof(AINavMeshAgent))]
[RequireComponent(typeof(AIHealthComponent))]
[RequireComponent(typeof(AIRewardComponent))]
public class AINetworkController : NetworkBehaviour
{
    // ─────────────────────────────────────────────────────────
    // NetworkVariable (서버→클라 동기화)
    // ─────────────────────────────────────────────────────────

    public NetworkVariable<AIState> currentState = new NetworkVariable<AIState>(AIState.Idle);
    public NetworkVariable<int> currentHP = new NetworkVariable<int>(0);
    // 실효 최대 체력(타입별 배율 반영). 클라이언트 체력바 비율 계산에 사용.
    public NetworkVariable<int> maxHP = new NetworkVariable<int>(0);
    public NetworkVariable<ulong> lastAttackerId = new NetworkVariable<ulong>(ulong.MaxValue); // 가장 마지막으로 공격한 플레이어 ID
    public NetworkVariable<bool> isStunned = new NetworkVariable<bool>(false); // 스턴(무력화) 상태

    // ─────────────────────────────────────────────────────────
    // 설정
    // ─────────────────────────────────────────────────────────

    [Header("행동 설정")]
    [Tooltip("AIBehaviorSO 에셋을 등록하세요.")]
    public AIBehaviorSO behavior;

    [Header("스탯 덮어쓰기 (0이면 SO 기본값 사용)")]
    [Tooltip("에셋을 수정하지 않고 이 프리팹만 특별히 데미지를 변경하고 싶을 때 사용합니다.")]
    public int overrideAttackDamage = 0;

    // ─────────────────────────────────────────────────────────
    // 컴포넌트 참조
    // ─────────────────────────────────────────────────────────

    [HideInInspector] public AIFSMComponent fsm;
    [HideInInspector] public AIPerceptionComponent perception;
    [HideInInspector] public AINavMeshAgent navAgent;
    [HideInInspector] public AIHealthComponent health;
    [HideInInspector] public AIRewardComponent reward;

    public bool IsAlive => currentState.Value != AIState.Dead;

    // ─────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────

    private void Awake()
    {
        fsm = GetComponent<AIFSMComponent>();
        perception = GetComponent<AIPerceptionComponent>();
        navAgent = GetComponent<AINavMeshAgent>();
        health = GetComponent<AIHealthComponent>();
        reward = GetComponent<AIRewardComponent>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        if (behavior == null)
        {
            Debug.LogError($"[AINetworkController] {gameObject.name}: AIBehaviorSO가 등록되지 않았습니다!");
            return;
        }

        Initialize(behavior);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;
        AIManager.Instance?.UnregisterAgent(this);
    }

    // ─────────────────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────────────────

    public void Initialize(AIBehaviorSO behaviorSO)
    {
        if (!IsServer) return;

        this.behavior = behaviorSO;
        maxHP.Value = behaviorSO.maxHP;
        currentHP.Value = behaviorSO.maxHP;
        // 양은 Idle(배회)에서 시작, 몬스터는 Patrol(순찰)에서 시작
        currentState.Value = behaviorSO.isLamb ? AIState.Idle : AIState.Patrol;

        fsm.Initialize(this, behaviorSO);
        perception.Initialize(this, behaviorSO.perceptionRadius);
        navAgent.Initialize(behaviorSO.moveSpeed);
        health.Initialize(this, behaviorSO);
        reward.Initialize(this, behaviorSO);

        AIManager.Instance?.RegisterAgent(this);
        Debug.Log($"[AINetworkController] {gameObject.name} 초기화 완료. HP:{behaviorSO.maxHP} 속도:{behaviorSO.moveSpeed}");
    }

    /// <summary>
    /// 타입별 체력 배율을 실효 최대 체력에 반영하고 현재 체력을 풀피로 맞춥니다. (스폰 직후 서버에서 호출)
    /// </summary>
    public void ApplyMaxHPMultiplier(float multiplier)
    {
        if (!IsServer || behavior == null) return;
        int newMax = Mathf.Max(1, Mathf.RoundToInt(behavior.maxHP * multiplier));
        maxHP.Value = newMax;
        currentHP.Value = newMax;
    }

    /// <summary>
    /// 처치 이벤트(OnLambKilled/OnMonsterKilled)는 평범한 EventBus 호출만으로는 서버(호스트) 프로세스 안에서만
    /// 구독자에게 전달되어, 호스트가 아닌 다른 클라이언트(예: 대죄인이 아닌 악인들)는 받지 못한다.
    /// 이 RPC로 모든 클라이언트에 브로드캐스트하여 양 잔존 수 알림 등이 진영 구분 없이 동일하게 뜨도록 한다.
    /// </summary>
    [ClientRpc]
    public void Client_NotifyAIKilledClientRpc(NetworkObjectReference killerRef, bool isLamb)
    {
        if (IsServer) return; // 서버는 OnDeath에서 이미 로컬로 직접 이벤트를 발생시킴

        killerRef.TryGet(out NetworkObject killer);
        if (isLamb)
            EventBus.RaiseLambKilled(NetworkObject, killer);
        else
            EventBus.RaiseMonsterKilled(NetworkObject, killer);
    }

    // AI 사운드(공격/피격/사망 등)는 AIFSMComponent·AIHealthComponent에서 서버 전용으로만 재생돼
    // 악인 클라이언트에겐 들리지 않았다. 서버에서 이 메서드로 호출하면 호스트 로컬 재생 + 전 클라이언트 브로드캐스트한다.
    public void PlayAISfx(SFXType type, Vector3 pos)
    {
        if (!IsServer) return;
        SoundManager.Instance?.PlaySFX(type, pos); // 호스트(서버) 로컬 재생
        Client_PlayAISfxClientRpc((int)type, pos); // 나머지 클라이언트
    }

    [ClientRpc]
    private void Client_PlayAISfxClientRpc(int sfxType, Vector3 pos)
    {
        if (IsServer) return; // 호스트는 PlayAISfx에서 이미 로컬로 재생함
        SoundManager.Instance?.PlaySFX((SFXType)sfxType, pos);
    }

    // ─────────────────────────────────────────────────────────
    // Tick (AIManager에서 FixedUpdate마다 호출, 또는 폴백)
    // ─────────────────────────────────────────────────────────

    public void Tick(float deltaTime)
    {
        if (!IsServer || !IsAlive) return;
        if (isStunned.Value) return; // 무력화(스턴) 중이면 행동(FSM) 일시정지
        
        fsm.Tick(deltaTime);
    }

    private float _fallbackTickTimer = 0f;

    // [방어 코드] AIManager가 씬에 존재하지 않을 경우(예: 테스트 씬) 스스로 Tick을 호출합니다.
    private void FixedUpdate()
    {
        if (!IsServer) return;
        
        // AIManager가 아예 없거나, 이 에이전트가 모종의 이유로 등록되지 않았을 때 자력으로 구동
        if (AIManager.Instance == null)
        {
            _fallbackTickTimer += Time.fixedDeltaTime;
            // 0.1초(10Hz)마다 한 번씩만 연산하도록 스로틀링 (프레임 드랍 연쇄 폭발 방지)
            if (_fallbackTickTimer >= 0.1f)
            {
                Tick(_fallbackTickTimer);
                _fallbackTickTimer = 0f;
            }
        }
        else if (!AIManager.Instance.IsAgentRegistered(this))
        {
            // AIManager가 나중에 생성되었거나 누락된 경우 지연 등록
            AIManager.Instance.RegisterAgent(this);
        }
    }

    // ─────────────────────────────────────────────────────────
    // [UI] 플로팅 체력바 (OnGUI 즉시 렌더링)
    // ─────────────────────────────────────────────────────────
    private static Texture2D _hpBarBgTex;
    private static Texture2D _hpBarFgTex;

    private void OnGUI()
    {
        if (Camera.main == null || !IsAlive || behavior == null || maxHP.Value <= 0) return;

        // 체력이 가득 차 있으면 숨김 (피격당했을 때만 표시)
        if (currentHP.Value >= maxHP.Value) return;

        // [추가] 마지막으로 공격한 플레이어(나)에게만 체력바가 보이도록 필터링
        if (NetworkManager.Singleton != null && lastAttackerId.Value != NetworkManager.Singleton.LocalClientId) return;

        // 단색 텍스처 초기화 (최초 1회 캐싱)
        if (_hpBarBgTex == null)
        {
            _hpBarBgTex = new Texture2D(1, 1);
            _hpBarBgTex.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.1f, 0.8f));
            _hpBarBgTex.Apply();

            _hpBarFgTex = new Texture2D(1, 1);
            _hpBarFgTex.SetPixel(0, 0, new Color(1f, 0.2f, 0.2f, 0.9f));
            _hpBarFgTex.Apply();
        }

        Vector3 screenPos = Camera.main.WorldToScreenPoint(transform.position + Vector3.up * 1.5f);
        if (screenPos.z < 0) return;

        float width = 60f;
        float height = 8f;
        float x = screenPos.x - (width / 2f);
        float y = Screen.height - screenPos.y;

        float hpRatio = Mathf.Clamp01((float)currentHP.Value / maxHP.Value);

        GUI.DrawTexture(new Rect(x, y, width, height), _hpBarBgTex);
        GUI.DrawTexture(new Rect(x, y, width * hpRatio, height), _hpBarFgTex);
    }
}
