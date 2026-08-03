using Unity.Netcode;
using UnityEngine;

/// <summary>
/// [도메인 명세 §2.1] 페이즈 FSM (P1 → P2 → P3) 호스팅.
/// 전환 트리거 평가. 4구역 시간차 제한 관리.
/// MonoBehaviour로 구현하여 NetworkObject 없이도 서버 권한 체크 가능.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PhaseManager : NetworkBehaviour
{
    public static PhaseManager Instance { get; private set; }

    [Header("설정")]
    [Tooltip("PhaseConfigSO 에셋을 인스펙터에서 등록하세요.")]
    public PhaseConfigSO config;

    // ─────────────────────────────────────────────────────────
    // 상태
    // ─────────────────────────────────────────────────────────

    public Phase CurrentPhase { get; private set; } = Phase.PreMatch;
    public float PhaseElapsedTime { get; private set; } = 0f;
    public int CurrentZoneIndex { get; private set; } = 0;
    public float ZoneTimeRemaining { get; private set; } = 0f;

    // [상태 동기화 보정용] 씬 로딩 타이밍이나 후발 참여자로 인해 ClientRpc를 놓치는 현상을 방지합니다.
    public NetworkVariable<Phase> currentPhaseNet = new NetworkVariable<Phase>(Phase.PreMatch);
    public NetworkVariable<int> currentZoneIndexNet = new NetworkVariable<int>(0);
    public NetworkVariable<int> currentSequenceStepNet = new NetworkVariable<int>(0);

    private int _currentSequenceStep = 0;
    private int[] _zoneOrder = new int[] { 0, 1, 2, 3 };

    private bool _isServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
    private bool _p3AccelerationTriggered = false;

    [Header("디버그")]
    [Tooltip("체크하면 '1','2','3' 키로 페이즈를 강제 전환할 수 있습니다.")]
    public bool enableDebugKeys = true;

    // ─────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        EventBus.OnPhase1to2Threshold += OnPhase1to2Threshold;
        EventBus.OnGreatSinnerDefeated += OnGreatSinnerDefeated;
    }

    public override void OnDestroy()
    {
        base.OnDestroy(); // NetworkBehaviour의 기본 파괴 로직 실행
        EventBus.OnPhase1to2Threshold -= OnPhase1to2Threshold;
        EventBus.OnGreatSinnerDefeated -= OnGreatSinnerDefeated;
        EventBus.Clear();
    }

    // ─────────────────────────────────────────────────────────
    // 업데이트
    // ─────────────────────────────────────────────────────────

    private void FixedUpdate()
    {
        if (CurrentPhase == Phase.PreMatch || CurrentPhase == Phase.PostMatch) return;

        PhaseElapsedTime += Time.fixedDeltaTime;

        switch (CurrentPhase)
        {
            case Phase.Phase1: 
                if (_isServer) TickPhase1(); 
                break;
            case Phase.Phase2: 
                ZoneTimeRemaining -= Time.fixedDeltaTime;
                EventBus.RaiseZoneTimeUpdate(ZoneTimeRemaining);
                
                // 30초 경고 UI/시각효과 업데이트
                UpdateZoneWarningVisual();

                if (_isServer) TickPhase2(); 
                break;
            case Phase.Phase3: 
                if (_isServer) TickPhase3(); 
                break;
        }
    }

    private GameObject _warningCube;
    private int _warningZoneIndex = -1;

    private void UpdateZoneWarningVisual()
    {
        // 30초 이하, 0초 초과일 때만 경고 표시
        if (ZoneTimeRemaining <= 30f && ZoneTimeRemaining > 0f)
        {
            if (_warningZoneIndex != CurrentZoneIndex || _warningCube == null)
            {
                ShowWarningCube(CurrentZoneIndex);
            }
        }
        else
        {
            HideWarningCube();
        }
    }

    private void ShowWarningCube(int zoneIndex)
    {
        HideWarningCube();

        ZoneBounds[] bounds = FindObjectsOfType<ZoneBounds>();
        ZoneBounds targetBounds = null;
        foreach (var b in bounds)
        {
            if (b.zoneIndex == zoneIndex)
            {
                targetBounds = b;
                break;
            }
        }

        if (targetBounds == null) return;

        _warningCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _warningCube.name = $"ZoneWarningCube_Zone{zoneIndex}";
        
        // 투명 큐브가 이동을 방해하지 않도록 콜라이더 즉시 제거
        if (_warningCube.TryGetComponent<Collider>(out var col))
        {
            Destroy(col);
        }

        BoxCollider box = targetBounds.GetComponent<BoxCollider>();
        Vector3 center, size;

        if (box != null)
        {
            center = targetBounds.transform.TransformPoint(box.center);
            size = new Vector3(
                box.size.x * targetBounds.transform.lossyScale.x,
                box.size.y * targetBounds.transform.lossyScale.y,
                box.size.z * targetBounds.transform.lossyScale.z);
        }
        else
        {
            float halfX = 5f * Mathf.Abs(targetBounds.transform.lossyScale.x);
            float halfZ = 5f * Mathf.Abs(targetBounds.transform.lossyScale.z);
            center = targetBounds.transform.position + targetBounds.transform.up * (targetBounds.zoneHeight * 0.5f);
            size = new Vector3(halfX * 2f, targetBounds.zoneHeight, halfZ * 2f);
            _warningCube.transform.rotation = targetBounds.transform.rotation;
        }

        _warningCube.transform.position = center;
        if (box != null) _warningCube.transform.rotation = targetBounds.transform.rotation;
        _warningCube.transform.localScale = size;

        // 경고 스크립트 부착 (이 안에서 재질과 색상을 설정하고 깜빡이게 만듦)
        _warningCube.AddComponent<ZoneWarningBlinker>();
        _warningZoneIndex = zoneIndex;
    }

    private void HideWarningCube()
    {
        if (_warningCube != null)
        {
            Destroy(_warningCube);
            _warningCube = null;
        }
        _warningZoneIndex = -1;
    }

    private void Update()
    {
        // [클라이언트 상태 보정] 서버의 RPC를 놓친 클라이언트를 위해 NetworkVariable 기반의 상태 동기화를 수행합니다.
        if (!_isServer)
        {
            if (CurrentPhase != currentPhaseNet.Value)
            {
                var oldPhase = CurrentPhase;
                CurrentPhase = currentPhaseNet.Value;
                PhaseElapsedTime = 0;
                EventBus.RaisePhaseTransition(oldPhase, CurrentPhase);
                Debug.Log($"[PhaseManager] 클라이언트 상태 보정 (RPC 누락 복구): {oldPhase} -> {CurrentPhase}");
            }

            if (CurrentPhase == Phase.Phase2 && CurrentZoneIndex != currentZoneIndexNet.Value)
            {
                CurrentZoneIndex = currentZoneIndexNet.Value;
                Debug.Log($"[PhaseManager] 클라이언트 구역 보정: {(char)('A' + CurrentZoneIndex)}");
            }
            return;
        }

        if (!enableDebugKeys) return;

        if (Input.GetKeyDown(KeyCode.Alpha1) && CurrentPhase != Phase.Phase1)
            TransitionTo(Phase.Phase1);
        if (Input.GetKeyDown(KeyCode.Alpha2) && CurrentPhase != Phase.Phase2)
            TransitionTo(Phase.Phase2);
        if (Input.GetKeyDown(KeyCode.Alpha3) && CurrentPhase != Phase.Phase3)
            TransitionTo(Phase.Phase3);

        // [디버그] F4: Phase 2 현재 활성 구역 즉시 봉인 (유니티 에디터 단축키 충돌로 인해 F9 -> F4 변경)
        if (Input.GetKeyDown(KeyCode.F4) && CurrentPhase == Phase.Phase2)
        {
            Debug.Log($"[PhaseManager][DEBUG] F4 — Zone {(char)('A' + CurrentZoneIndex)} 즉시 봉인 트리거! (잔여 시간 {ZoneTimeRemaining:F1}초 → 0)");
            ZoneTimeRemaining = 0f;
        }

        // [디버그] F8: 몬스터 아이템 100% 강제 드랍 토글
        if (Input.GetKeyDown(KeyCode.F8))
        {
            AIRewardComponent.forceItemDrop = !AIRewardComponent.forceItemDrop;
            Debug.Log($"[PhaseManager][DEBUG] F8 — 몬스터 아이템 강제 드랍: {(AIRewardComponent.forceItemDrop ? "ON (100%)" : "OFF (8%)")}");
        }

        // [디버그] F7: 타임스케일(시간 가속) 5배속 토글
        if (Input.GetKeyDown(KeyCode.F7))
        {
            Time.timeScale = (Time.timeScale > 1f) ? 1f : 5f;
            Debug.Log($"[PhaseManager][DEBUG] F7 — 게임 전체 시간 배속 변경: {Time.timeScale}배속");
        }

        // [디버그] F6: 대죄인 즉사 (3페이즈 테스트용)
        if (Input.GetKeyDown(KeyCode.F6))
        {
            if (PlayerManager.Instance != null && PlayerManager.Instance.greatSinner != null)
            {
                var gsObj = PlayerManager.Instance.greatSinner.avatarObject;
                if (gsObj != null && gsObj.TryGetComponent<PlayerHealthComponent>(out var gsHealth))
                {
                    Debug.Log($"[PhaseManager][DEBUG] F6 — 대죄인 즉사 트리거!");
                    // CombatManager를 거치거나 직접 데미지 적용
                    if (CombatManager.Instance != null)
                        CombatManager.Instance.ApplyDamage(gsHealth.NetworkObject, gsHealth.NetworkObject, 99999);
                    else
                        gsHealth.ServerApplyDamage(99999);
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // Tick
    // ─────────────────────────────────────────────────────────

    private void TickPhase1()
    {
        float duration = config != null ? config.Phase1Duration : 360f;
        if (PhaseElapsedTime >= duration)
        {
            Debug.Log("[PhaseManager] Phase 1 시간 만료 → Phase 2 전환.");
            TransitionTo(Phase.Phase2);
        }
    }

    private float _bonusZoneTime = 0f;

    private void TickPhase2()
    {
        if (_currentSequenceStep >= 4)
        {
            return;
        }

        if (ZoneTimeRemaining <= 0)
        {
            OnZoneTimeUp(CurrentZoneIndex); // 이 안에서 토템 보너스(ExtendZoneTime)가 호출되어 _bonusZoneTime이 쌓임
            
            _currentSequenceStep++;
            currentSequenceStepNet.Value = _currentSequenceStep; // 클라이언트 동기화

            if (_currentSequenceStep >= 4)
            {
                Debug.Log("[PhaseManager] 모든 구역 봉인 완료. (대죄인이 사망할 때까지 3페이즈 전환 대기)");
                CurrentZoneIndex = -1;
                ZoneTimeRemaining = 0f;
                // TransitionTo(Phase.Phase3); // 제거됨: 도메인 룰에 의해 3페이즈는 오직 대죄인 사망 시에만 진입
            }
            else
            {
                CurrentZoneIndex = _zoneOrder[_currentSequenceStep];
                currentZoneIndexNet.Value = CurrentZoneIndex; // 클라이언트 동기화용
                float baseTime = GetSequenceTimeLimit(_currentSequenceStep);
                ZoneTimeRemaining = baseTime + _bonusZoneTime;
                
                Debug.Log($"[PhaseManager] 랜덤 Zone {(char)('A' + CurrentZoneIndex)} 시작. (기본: {baseTime}초 + 토템 보너스: {_bonusZoneTime}초) = {ZoneTimeRemaining}초.");
                
                if (IsSpawned)
                {
                    SyncZoneStartClientRpc(CurrentZoneIndex, ZoneTimeRemaining);
                }
                
                WarnIfInDangerZone(CurrentZoneIndex);
                
                _bonusZoneTime = 0f; // 보너스 적용 후 초기화
            }
        }
    }

    private void TickPhase3()
    {
        float duration = config != null ? config.Phase3Duration : 180f;
        if (!_p3AccelerationTriggered && PhaseElapsedTime >= duration)
        {
            _p3AccelerationTriggered = true;
            Debug.Log("[PhaseManager] Phase 3 시간 만료 → 성화 침식 2배 가속 트리거.");
            HolyFireRingController.Instance.AccelerateErosion(2.0f);
        }
    }

    // ─────────────────────────────────────────────────────────
    // 이벤트 핸들러
    // ─────────────────────────────────────────────────────────

    private void OnPhase1to2Threshold()
    {
        if (!_isServer || CurrentPhase != Phase.Phase1) return;
        Debug.Log("[PhaseManager] 영혼 헌납 임계치 도달 → Phase 2 전환.");
        TransitionTo(Phase.Phase2);
    }

    private void OnGreatSinnerDefeated()
    {
        if (!_isServer) return;
        if (CurrentPhase == Phase.Phase1 || CurrentPhase == Phase.Phase2)
        {
            Debug.Log("==================================================");
            Debug.Log($"[PhaseManager] 🚨 대죄인 사망 감지! 현재 진행 중이던 {CurrentPhase} 타이머 및 이벤트가 즉시 멈춥니다!");
            Debug.Log("==================================================");
            TransitionTo(Phase.Phase3);
        }
    }

    // ─────────────────────────────────────────────────────────
    // TransitionTo
    // ─────────────────────────────────────────────────────────

    public void TransitionTo(Phase newPhase)
    {
        if (!_isServer) return;

        var oldPhase = CurrentPhase;
        CurrentPhase = newPhase;
        currentPhaseNet.Value = newPhase; // 클라이언트 보정용
        PhaseElapsedTime = 0;

        Debug.Log($"[PhaseManager] ===== 페이즈 전환: {oldPhase} → {newPhase} =====");

        switch (newPhase)
        {
            case Phase.Phase1: OnEnterPhase1(oldPhase); break;
            case Phase.Phase2: OnEnterPhase2(oldPhase); break;
            case Phase.Phase3: OnEnterPhase3(oldPhase); break;
            default: EventBus.RaisePhaseTransition(oldPhase, newPhase); break;
        }

        // 네트워크 객체가 정상적으로 Spawn된 상태에서만 RPC를 호출하여 NullReferenceException 방지
        if (IsSpawned)
        {
            SyncPhaseTransitionClientRpc(oldPhase, newPhase);
        }
        else
        {
            Debug.LogWarning("[PhaseManager] IsSpawned가 false입니다. ClientRpc를 호출하지 않습니다.");
        }
    }

    [ClientRpc]
    private void SyncPhaseTransitionClientRpc(Phase oldPhase, Phase newPhase)
    {
        if (_isServer) return;

        // NetworkVariable(currentPhaseNet) 폴링 보정(Update())이 이 RPC보다 먼저 도착해
        // 이미 같은 전환을 반영했다면 중복 처리한다. 그대로 두면 아나운서 멘트 등
        // OnPhaseTransition 구독자들이 같은 이벤트를 두 번 받아 멘트가 중복 출력된다.
        if (CurrentPhase == newPhase) return;

        CurrentPhase = newPhase;
        PhaseElapsedTime = 0;
        EventBus.RaisePhaseTransition(oldPhase, newPhase);
    }

    [ClientRpc]
    private void SyncZoneStartClientRpc(int zoneIndex, float timeRemaining)
    {
        if (!_isServer)
        {
            CurrentZoneIndex = zoneIndex;
            ZoneTimeRemaining = timeRemaining;
        }
        
        WarnIfInDangerZone(zoneIndex);
    }
    
    private void WarnIfInDangerZone(int zoneIndex)
    {
        if (Unity.Netcode.NetworkManager.Singleton == null || !Unity.Netcode.NetworkManager.Singleton.IsClient) return;
        var localObj = Unity.Netcode.NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
        if (localObj == null) return;

        foreach (var zone in ZoneBounds.AllZones)
        {
            if (zone.zoneIndex == zoneIndex)
            {
                if (zone.Contains(localObj.transform.position))
                {
                    if (AnnouncerManager.Instance != null)
                    {
                        AnnouncerManager.Instance.Announce($"<color=red>경고: 현재 위치한 구역({ZoneBounds.GetDisplayName(zoneIndex)})이 다음 제한구역으로 지정되었습니다! 신속히 대피하십시오!</color>");
                    }
                    break;
                }
            }
        }
    }

    [ClientRpc]
    private void SyncZoneSealClientRpc(int zoneIndex)
    {
        if (_isServer) return;
        EventBus.RaiseZoneSeal(zoneIndex);
    }

    // ─────────────────────────────────────────────────────────
    // Phase 진입
    // ─────────────────────────────────────────────────────────

    private void OnEnterPhase1(Phase oldPhase)
    {
        Debug.Log("[PhaseManager] Phase 1 진입: 양 + 제단 스폰.");
        if (SpawnManager.Instance != null)
            SpawnManager.Instance.StartPhase1();
        EventBus.RaisePhaseTransition(oldPhase, Phase.Phase1);

        // 프리매치 → 페이즈1로 진입하는 순간이 곧 매치 시작. 모든 클라이언트에 시작음 재생.
        if (oldPhase == Phase.PreMatch)
            Client_PlayMatchStartClientRpc();
    }

    [ClientRpc]
    private void Client_PlayMatchStartClientRpc()
    {
        SoundManager.Instance?.PlaySFX(SFXType.MatchStart);
    }

    private void OnEnterPhase2(Phase oldPhase)
    {
        Debug.Log("[PhaseManager] Phase 2 진입: 랜덤 구역 타이머 시작.");

        // 랜덤으로 구역 폐쇄 순서 섞기 (Fisher-Yates)
        _zoneOrder = new int[] { 0, 1, 2, 3 };
        System.Random rng = new System.Random();
        for (int i = 3; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            int temp = _zoneOrder[i];
            _zoneOrder[i] = _zoneOrder[j];
            _zoneOrder[j] = temp;
        }

        _currentSequenceStep = 0;
        currentSequenceStepNet.Value = _currentSequenceStep; // 클라이언트 동기화
        CurrentZoneIndex = _zoneOrder[_currentSequenceStep];
        currentZoneIndexNet.Value = CurrentZoneIndex; // 클라이언트 동기화용
        ZoneTimeRemaining = GetSequenceTimeLimit(_currentSequenceStep);

        if (SinManager.Instance != null)
            SinManager.Instance.OnP1toP2_DistributeShared();
        if (SpawnManager.Instance != null)
            SpawnManager.Instance.StartPhase2();

        if (TotemManager.Instance != null)
            TotemManager.Instance.InitializeForPhase2();

        EventBus.RaisePhaseTransition(oldPhase, Phase.Phase2);
        
        if (IsSpawned)
        {
            SyncZoneStartClientRpc(CurrentZoneIndex, ZoneTimeRemaining);
        }
        
        WarnIfInDangerZone(CurrentZoneIndex);
        
        Debug.Log($"[PhaseManager] 첫 번째 랜덤 Zone {(char)('A' + CurrentZoneIndex)} 시작. 봉인 시간: {ZoneTimeRemaining}초.");
    }

    private void OnEnterPhase3(Phase oldPhase)
    {
        Debug.Log("==================================================");
        Debug.Log("[PhaseManager] 🔥 Phase 3 (최후의 전투) 가 공식적으로 시작되었습니다!");
        Debug.Log("==================================================");
        _p3AccelerationTriggered = false;

        if (SpawnManager.Instance != null)
            SpawnManager.Instance.DespawnAllPhase2Objects();

        // 2페이즈 구역별 성화도 모두 제거 (3페이즈 전용 성화로 교체되기 전에 정리)
        if (SpawnManager.Instance != null)
            SpawnManager.Instance.DespawnAllZoneSealFires();

        if (TotemManager.Instance != null)
            TotemManager.Instance.Cleanup();

        EventBus.RaisePhaseTransition(oldPhase, Phase.Phase3);
        
        if (SpawnManager.Instance != null)
        {
            Vector3 centerPos = Vector3.zero;
            if (PlayerManager.Instance != null && PlayerManager.Instance.greatSinner != null && PlayerManager.Instance.greatSinner.avatarObject != null)
            {
                centerPos = PlayerManager.Instance.greatSinner.avatarObject.transform.position;
            }
            // 대죄인 사망 위치를 기점으로 큰 원형의 자기장 생성 (초기 반경 50f)
            SpawnManager.Instance.SpawnHolyFireRing(centerPos, 50f);
        }

        if (PlayerManager.Instance != null)
        {
            PlayerManager.Instance.TransitionAlivePlayersToPhase3();
        }


        // 3페이즈 시작 직후, 생존 악인이 1명 이하면 즉시 매치 종료 판정을 하도록 호출
        if (MatchManager.Instance != null)
        {
            MatchManager.Instance.EvaluateMatchEnd();
        }
    }

    // ─────────────────────────────────────────────────────────
    // 구역 봉인
    // ─────────────────────────────────────────────────────────

    private void OnZoneTimeUp(int zoneIndex)
    {
        Debug.Log($"[PhaseManager] Zone {(char)('A' + zoneIndex)} 봉인!");
        if (IsSpawned)
        {
            SyncZoneSealClientRpc(zoneIndex);
        }
        else
        {
            Debug.LogWarning("[PhaseManager] IsSpawned가 false입니다. SyncZoneSealClientRpc를 호출하지 않습니다.");
        }
        EventBus.RaiseZoneSeal(zoneIndex);
    }

    // ─────────────────────────────────────────────────────────
    // 공개 유틸리티
    // ─────────────────────────────────────────────────────────

    public bool IsCurrentPhase(Phase phase) => CurrentPhase == phase;

    public bool IsZoneSealed(int zoneIndex)
    {
        // Phase 1 이하라면 아무 구역도 봉인되지 않음
        if (CurrentPhase == Phase.PreMatch || CurrentPhase == Phase.Phase1) return false;
        
        // Phase 3 이상이라면 모든 구역이 봉인됨
        if (CurrentPhase == Phase.Phase3 || CurrentPhase == Phase.PostMatch) return true;

        // Phase 2 진행 중이라면, 현재 _currentSequenceStep 이전에 지나온 구역들인지 확인
        for (int i = 0; i < _currentSequenceStep; i++)
        {
            if (_zoneOrder[i] == zoneIndex) return true;
        }
        return false;
    }

    public int GetNextZoneIndex()
    {
        if (CurrentPhase != Phase.Phase2) return -1;
        if (_currentSequenceStep + 1 < 4)
            return _zoneOrder[_currentSequenceStep + 1];
        return -1;
    }

    /// <summary>
    /// 주어진 구역이 몇 번째로 봉인되는지 순서(0~3)를 반환합니다.
    /// </summary>
    public int GetSequenceIndexOfZone(int zoneIndex)
    {
        if (_zoneOrder == null || _zoneOrder.Length == 0) return 0;
        for (int i = 0; i < 4; i++)
        {
            if (_zoneOrder[i] == zoneIndex) return i;
        }
        return 0;
    }

    /// <summary>
    /// 순서(0=1번째, 1=2번째, 2=3번째, 3=4번째)에 따른 구역 제한 시간을 반환합니다. (기획: 2분, 3분, 3분, 4분)
    /// </summary>
    public float GetSequenceTimeLimit(int step)
    {
        return step switch
        {
            0 => 120f, // 첫 번째 구역 2분
            1 => 180f, // 두 번째 구역 3분
            2 => 180f, // 세 번째 구역 3분
            3 => 240f, // 네 번째 구역 4분
            _ => 120f
        };
    }

    public void ReduceZoneTime(float seconds)
    {
        if (!_isServer || CurrentPhase != Phase.Phase2) return;
        ZoneTimeRemaining -= seconds;
        if (ZoneTimeRemaining < 0f) ZoneTimeRemaining = 0f;
        Debug.Log($"[PhaseManager] Zone {(char)('A' + CurrentZoneIndex)} 시간 -{seconds}초. (잔여 {ZoneTimeRemaining:F1}초)");

        // 서버에서만 감소하던 시간을 모든 클라이언트(악인 포함)에 즉시 동기화하고,
        // 경고음 1회 + 상단 UI 텍스트 플래시 연출을 트리거합니다.
        if (IsSpawned)
        {
            SyncZoneTimeReducedClientRpc(ZoneTimeRemaining);
        }
        else
        {
            // 네트워크 스폰 전이라면 로컬(호스트) 연출만 실행
            EventBus.RaiseZoneTimeUpdate(ZoneTimeRemaining);
            SoundManager.Instance?.PlaySFX(SFXType.ZoneSealWarning);
            EventBus.RaiseZoneTimeReduced();
        }
    }

    [ClientRpc]
    private void SyncZoneTimeReducedClientRpc(float newTimeRemaining)
    {
        // 악인(클라이언트)의 로컬 카운트다운이 서버가 반영한 감소분을 놓치지 않도록 값을 강제로 맞춥니다.
        // (서버/호스트는 이미 반영되어 있으므로 값 갱신은 클라이언트에서만 수행)
        if (!_isServer)
        {
            ZoneTimeRemaining = newTimeRemaining;
        }

        EventBus.RaiseZoneTimeUpdate(newTimeRemaining);

        // 모든 화면에서 구역제한 경고음 1회 재생
        SoundManager.Instance?.PlaySFX(SFXType.ZoneSealWarning);

        // 상단 UI 텍스트를 붉게 깜빡였다가 원래 색으로 복구
        EventBus.RaiseZoneTimeReduced();
    }

    public void ExtendZoneTime(float seconds)
    {
        if (!_isServer || CurrentPhase != Phase.Phase2) return;
        
        if (ZoneTimeRemaining <= 0f) 
        {
            // 이미 타이머가 0이 되어 OnZoneTimeUp이 호출 중인 상태 (봉인 직전)
            _bonusZoneTime += seconds;
            Debug.Log($"[PhaseManager] Zone {(char)('A' + CurrentZoneIndex)} 봉인 보너스 발생. 다음 구역 시간에 +{seconds}초 누적.");
        }
        else
        {
            ZoneTimeRemaining += seconds;
            Debug.Log($"[PhaseManager] Zone {(char)('A' + CurrentZoneIndex)} 시간 직접 연장 +{seconds}초.");
        }
    }
}
