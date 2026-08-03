using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 어린양 스폰포인트 5개를 관리하는 시스템입니다.
/// 4개 활성 + 1개 예비. 제단 반경 30m 내 NavMesh 위에서 반경 3m 빈 공간을 찾아 스폰합니다.
/// SpawnManager.StartPhase1()에서 초기화합니다.
/// </summary>
public class LambSpawnSystem : NetworkBehaviour
{
    public static LambSpawnSystem Instance { get; private set; }

    [Header("스폰 설정")]
    [Tooltip("어린양 프리팹 (NetworkObject 필수)")]
    public GameObject lambPrefab;

    [Tooltip("제단 반경 내 스폰 탐색 거리(m)")]
    public float altarSearchRadius = 30f;

    [Tooltip("스폰포인트 확보에 필요한 최소 빈 공간 반경(m)")]
    public float clearSpaceRadius = 3f;

    [Tooltip("스폰포인트당 양 최소 수")]
    public int minLambsPerPoint = 4;

    [Tooltip("스폰포인트당 양 최대 수")]
    public int maxLambsPerPoint = 6;

    [Tooltip("클리어 후 리스폰 대기 최소 시간(초)")]
    public float respawnDelayMin = 30f;

    [Tooltip("클리어 후 리스폰 대기 최대 시간(초)")]
    public float respawnDelayMax = 45f;

    [Tooltip("예비 포인트 활성화 딜레이(초)")]
    public float reserveActivateDelay = 1.5f;

    [Tooltip("제단 대비 Y좌표 최대 허용 차이(m). 지붕 스폰 방지.")]
    public float maxYDifference = 5f;

    [Tooltip("무구한 어린양 확률(0~1)")]
    public float innocentChance = 0.05f;

    [Tooltip("스폰을 허용할 NavMesh Area Mask (1 = Walkable). 건물 위, 계단, 다리에 스폰되는 것을 방지합니다.")]
    public int walkableAreaMask = 1;

    // ─── 내부 상태 ───
    private List<SpawnPointData> _spawnPoints = new List<SpawnPointData>();
    private List<Vector3> _altarPositions = new List<Vector3>();
    private int _innocentAliveCount = 0;
    private bool _initialized = false;

    /// <summary>
    /// 개별 스폰포인트 데이터
    /// </summary>
    private class SpawnPointData
    {
        public int index;
        public Vector3 position;
        public List<NetworkObject> lambs = new List<NetworkObject>();
        public bool isActive;       // true=활성(양 스폰됨), false=예비/쿨다운
        public bool isOnCooldown;   // 쿨다운 중
        public bool hasInnocent;    // 이 포인트에 무구한 양이 있는지
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    // ─── 초기화 API (SpawnManager에서 호출) ───

    /// <summary>
    /// 스폰된 제단 위치를 받아 시스템을 초기화하고 4개 포인트를 활성화합니다.
    /// </summary>
    public void InitializeWithAltars(List<Vector3> altarPositions)
    {
        if (!IsServer) return;

        _altarPositions = altarPositions;
        _innocentAliveCount = 0;
        _spawnPoints.Clear();

        // 스폰포인트 5개 생성
        for (int i = 0; i < 5; i++)
        {
            var sp = new SpawnPointData
            {
                index = i,
                position = Vector3.zero,
                isActive = false,
                isOnCooldown = false,
                hasInnocent = false
            };
            _spawnPoints.Add(sp);
        }

        // 4개 활성화 + 1개 예비
        bool innocentAssigned = false;
        for (int i = 0; i < 4; i++)
        {
            Vector3 pos = FindValidSpawnPosition();
            _spawnPoints[i].position = pos;
            _spawnPoints[i].isActive = true;
            SpawnLambsAtPoint(_spawnPoints[i], ref innocentAssigned);
        }

        // 무구한 양 최소 1마리 보장
        if (!innocentAssigned && _spawnPoints.Count > 0)
            ForceInnocentAtPoint(_spawnPoints[0]);

        // 5번째는 예비
        _spawnPoints[4].position = FindValidSpawnPosition();
        _spawnPoints[4].isActive = false;

        _initialized = true;
        Debug.Log($"[LambSpawnSystem] 초기화 완료. 제단 {altarPositions.Count}개, 활성 포인트 4개, 예비 1개.");
    }

    // ─── Update (서버 전용) ───

    private void Update()
    {
        if (!IsServer || !_initialized) return;

        // 활성 포인트의 클리어 조건 체크
        for (int i = 0; i < _spawnPoints.Count; i++)
        {
            var sp = _spawnPoints[i];
            if (!sp.isActive || sp.isOnCooldown) continue;

            // Despawn된 레퍼런스 정리
            sp.lambs.RemoveAll(obj => obj == null || !obj.IsSpawned);

            // 클리어 판정: 1마리 이하
            if (sp.lambs.Count <= 1)
            {
                OnPointCleared(sp);
            }
        }

        // [디버그용] F3키 입력 시 활성화된 스폰 구역마다 양들을 추가로 한 세트 더 스폰합니다 (결과적으로 2배)
        if (Input.GetKeyDown(KeyCode.F3))
        {
            Debug.Log("[LambSpawnSystem] 디버그: F3 입력 감지. 활성화된 스폰 구역에 양을 추가로 스폰합니다!");
            bool dummyInnocentAssigned = true; // 무구한 양 중복 생성 방지용
            foreach (var sp in _spawnPoints)
            {
                if (sp.isActive && !sp.isOnCooldown)
                {
                    SpawnLambsAtPoint(sp, ref dummyInnocentAssigned);
                }
            }
        }
    }

    // ─── 클리어 처리 ───

    private void OnPointCleared(SpawnPointData clearedPoint)
    {
        Debug.Log($"[LambSpawnSystem] 포인트 {clearedPoint.index} 클리어! 남은 양: {clearedPoint.lambs.Count}마리");

        clearedPoint.isActive = false;
        clearedPoint.isOnCooldown = true;

        // 클리어 순간 다음 위치 결정
        clearedPoint.position = FindValidSpawnPosition();

        // 예비 포인트 찾아서 활성화
        var reserve = FindReservePoint();
        if (reserve != null)
        {
            StartCoroutine(ActivateReserveCoroutine(reserve));
        }

        // 쿨다운 시작 (30~45초 후 예비로 전환)
        float cooldown = Random.Range(respawnDelayMin, respawnDelayMax);
        StartCoroutine(CooldownCoroutine(clearedPoint, cooldown));
    }

    private IEnumerator ActivateReserveCoroutine(SpawnPointData reserve)
    {
        yield return new WaitForSeconds(reserveActivateDelay);

        if (!IsServer) yield break;

        reserve.isActive = true;
        bool innocentAssigned = _innocentAliveCount > 0;
        SpawnLambsAtPoint(reserve, ref innocentAssigned);

        // 무구한 양이 0마리면 이 포인트에 강제 할당
        if (_innocentAliveCount <= 0)
            ForceInnocentAtPoint(reserve);

        Debug.Log($"[LambSpawnSystem] 예비 포인트 {reserve.index} 활성화. 위치: {reserve.position}");
    }

    private IEnumerator CooldownCoroutine(SpawnPointData point, float delay)
    {
        yield return new WaitForSeconds(delay);

        if (!IsServer) yield break;

        // 고아 양 전부 제거
        DespawnOrphanLambs(point);

        point.isOnCooldown = false;
        // 이제 예비 상태로 대기
        Debug.Log($"[LambSpawnSystem] 포인트 {point.index} 쿨다운 완료 → 예비 전환.");
    }

    // ─── 양 스폰 ───

    private void SpawnLambsAtPoint(SpawnPointData sp, ref bool innocentAssigned)
    {
        int count = Random.Range(minLambsPerPoint, maxLambsPerPoint + 1);

        for (int i = 0; i < count; i++)
        {
            // 포인트 위치 기준 기존 3m 반경에서 12m 반경으로 크게 늘려 옹기종기 모이는 현상 완화
            Vector2 offset = Random.insideUnitCircle * 12f;
            Vector3 spawnPos = sp.position + new Vector3(offset.x, 0f, offset.y);

            // NavMesh 위 보정 (보정 범위도 5f -> 12f로 확장, 지정된 AreaMask만 허용)
            if (NavMesh.SamplePosition(spawnPos, out NavMeshHit hit, 12f, walkableAreaMask))
                spawnPos = hit.position;

            // 무구한 양 결정
            bool isInnocent = !innocentAssigned && Random.value < innocentChance;

            var netObj = SpawnSingleLamb(spawnPos, isInnocent);
            if (netObj != null)
            {
                sp.lambs.Add(netObj);
                if (isInnocent)
                {
                    innocentAssigned = true;
                    sp.hasInnocent = true;
                    _innocentAliveCount++;
                }
            }
        }
    }

    private NetworkObject SpawnSingleLamb(Vector3 position, bool isInnocent)
    {
        if (lambPrefab == null) return null;

        var instance = Instantiate(lambPrefab, position, Quaternion.identity);
        var netObj = instance.GetComponent<NetworkObject>();
        if (netObj == null)
        {
            Destroy(instance);
            return null;
        }

        netObj.Spawn();

        var lamb = instance.GetComponent<LambController>();
        if (lamb != null)
        {
            lamb.lambType = isInnocent ? LambController.LambType.Innocent : LambController.LambType.Normal;
            lamb.ApplyTypeStats();
        }

        string typeLabel = isInnocent ? "Innocent" : "Normal";
        instance.name = $"Lamb_{typeLabel}_{Time.frameCount}";

        return netObj;
    }

    // ─── 무구한 양 보장 ───

    private void ForceInnocentAtPoint(SpawnPointData sp)
    {
        if (sp.lambs.Count == 0) return;

        // 첫 번째 일반 양을 무구한 양으로 교체
        for (int i = 0; i < sp.lambs.Count; i++)
        {
            var obj = sp.lambs[i];
            if (obj == null || !obj.IsSpawned) continue;

            var lamb = obj.GetComponent<LambController>();
            if (lamb != null && lamb.lambType == LambController.LambType.Normal)
            {
                lamb.lambType = LambController.LambType.Innocent;
                lamb.ApplyTypeStats();
                obj.name = obj.name.Replace("Normal", "Innocent");
                sp.hasInnocent = true;
                _innocentAliveCount++;
                Debug.Log($"[LambSpawnSystem] 무구한 양 강제 할당 (포인트 {sp.index})");
                return;
            }
        }
    }

    /// <summary>
    /// LambController.OnNetworkDespawn()에서 호출.
    /// 무구한 양 사망 시 카운트를 감소시킵니다.
    /// </summary>
    public void OnInnocentLambDied()
    {
        _innocentAliveCount--;
        if (_innocentAliveCount <= 0)
        {
            _innocentAliveCount = 0;
            Debug.Log("[LambSpawnSystem] 마지막 무구한 양 사망. 다음 스폰 시 강제 할당 예정.");
        }
    }

    // ─── 고아 양 정리 ───

    private void DespawnOrphanLambs(SpawnPointData sp)
    {
        foreach (var obj in sp.lambs)
        {
            if (obj != null && obj.IsSpawned)
                obj.Despawn();
        }
        sp.lambs.Clear();
        sp.hasInnocent = false;
    }

    // ─── 유효 위치 탐색 ───

    /// <summary>
    /// 제단 반경 30m 내, NavMesh 위, Y좌표 5m 이내, 반경 3m 빈 공간을 찾습니다.
    /// </summary>
    private Vector3 FindValidSpawnPosition()
    {
        if (_altarPositions.Count == 0)
        {
            Debug.LogWarning("[LambSpawnSystem] 제단 위치가 없습니다!");
            return Vector3.zero;
        }

        int maxAttempts = 50;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // 랜덤 제단 선택
            Vector3 altarPos = _altarPositions[Random.Range(0, _altarPositions.Count)];

            // 반경 30m 내 랜덤 좌표
            Vector2 rnd = Random.insideUnitCircle * altarSearchRadius;
            Vector3 candidate = altarPos + new Vector3(rnd.x, 0f, rnd.y);

            // NavMesh 위 보정 (지정된 AreaMask만 허용하여 건물 위, 계단, 다리 제외)
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 10f, walkableAreaMask))
                continue;

            candidate = hit.position;

            // Y좌표 차이 검사 (지붕 스폰 방지)
            if (Mathf.Abs(candidate.y - altarPos.y) > maxYDifference)
                continue;

            // 다른 활성 포인트와 최소 거리 확인 (겹침 방지) - 그룹 간 최소 20m 이격 (sqrMagnitude 400)
            bool tooClose = false;
            foreach (var sp in _spawnPoints)
            {
                if (sp.isActive && (sp.position - candidate).sqrMagnitude < 400f)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose) continue;

            return candidate;
        }

        // 실패 시 첫 번째 제단 위치 반환
        Debug.LogWarning("[LambSpawnSystem] 유효 위치 탐색 실패. 제단 위치로 폴백.");
        return _altarPositions[0];
    }

    // ─── 헬퍼 ───

    private SpawnPointData FindReservePoint()
    {
        foreach (var sp in _spawnPoints)
        {
            if (!sp.isActive && !sp.isOnCooldown)
                return sp;
        }
        return null;
    }

    /// <summary>
    /// 모든 양과 스폰포인트를 정리합니다 (Phase 전환 시 호출).
    /// </summary>
    public void Shutdown()
    {
        if (!IsServer) return;
        StopAllCoroutines();

        foreach (var sp in _spawnPoints)
            DespawnOrphanLambs(sp);

        _spawnPoints.Clear();
        _initialized = false;
        Debug.Log("[LambSpawnSystem] 시스템 종료.");
    }
}
