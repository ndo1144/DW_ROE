using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// [도메인 명세 §3.4] Unity NavMeshAgent 래퍼. 이동 경로 계산.
/// NavMesh는 맵 빌드 시 사전 생성 필요.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class AINavMeshAgent : MonoBehaviour
{
    private NavMeshAgent _agent;

    public float speed
    {
        get => _agent != null ? _agent.speed : 0f;
        set { if (_agent != null) _agent.speed = value; }
    }

    public bool IsMoving => _agent != null && _agent.hasPath && _agent.remainingDistance > _agent.stoppingDistance;
    public bool IsOnNavMesh => _agent != null && _agent.isOnNavMesh;

    // ─────────────────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────────────────

    public void Initialize(float moveSpeed)
    {
        _agent = GetComponent<NavMeshAgent>();
        if (_agent == null)
        {
            Debug.LogError($"[AINavMeshAgent] {gameObject.name}: NavMeshAgent 컴포넌트가 없습니다!");
            return;
        }

        // NavMesh 유효성 검증 (주변 2.0m 내에 구워진 NavMesh가 있는지 체크 - 다리 밑이나 아래층으로 워프되는 현상 방지)
        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
        {
            Debug.LogWarning($"[AINavMeshAgent] {gameObject.name}: 주변 2m 이내에 유효한 NavMesh를 찾을 수 없습니다! " +
                             "Window > AI > Navigation에서 씬을 Bake했는지 확인해 주세요. 치명적 에러 방지를 위해 에이전트를 비활성화합니다.");
            _agent.enabled = false;
            return;
        }

        // [핵심] 찾은 완벽한 네비메쉬 표면으로 에이전트를 강제로 스냅(Warp)시킵니다.
        bool warped = _agent.Warp(hit.position);
        if (!warped) Debug.LogWarning($"[AINavMeshAgent] {gameObject.name}: NavMesh Warp에 실패했습니다! 에이전트가 맵 바깥에 있을 수 있습니다.");

        _agent.enabled = true;
        _agent.speed = moveSpeed;
        
        if (moveSpeed <= 0.1f) Debug.LogWarning($"[AINavMeshAgent] {gameObject.name}: 주의! moveSpeed가 {moveSpeed}로 너무 낮습니다. AIBehaviorSO의 속도 설정을 확인하세요.");
        
        // 방향 전환(회전) 속도를 초당 360도에서 120도로 낮추어 고개를 서서히 돌리도록 수정
        _agent.angularSpeed = 120f; 
        
        // 가속도도 살짝 낮추어 스무스하게 걷기 시작하도록 설정
        _agent.acceleration = 8f;
        _agent.stoppingDistance = 0.5f;
        _agent.autoBraking = true;
    }

    // ─────────────────────────────────────────────────────────
    // 공개 API
    // ─────────────────────────────────────────────────────────

    private Vector3 _lastDestination = Vector3.zero;

    /// <summary>
    /// 목적지를 설정합니다. (과도한 재계산 방지)
    /// </summary>
    public void SetDestination(Vector3 destination)
    {
        if (_agent == null || !_agent.isActiveAndEnabled) return;
        
        if (!_agent.isOnNavMesh)
        {
            Debug.LogWarning($"[AINavMeshAgent] {gameObject.name}가 NavMesh 위에 있지 않아서 이동 명령(SetDestination)이 무시되었습니다! 피벗이나 스폰 위치를 다시 확인해주세요.");
            return;
        }
        
        // 목적지가 0.5m 이상 변경되었을 때만 경로 재계산 (Stuttering 방지)
        if (Vector3.Distance(_lastDestination, destination) > 0.5f || _agent.isStopped)
        {
            _agent.isStopped = false; // Stop()으로 멈춘 상태 해제
            
            // 대상의 중심점(Pivot)이 공중에 떠 있거나 지형 불일치로 인해 도달 불가능 판정이 나오는 것을 방지하기 위해,
            // 도착 지점 반경 2m 이내의 가장 가까운 NavMesh(Walkable: 1) 표면으로 보정합니다. (범위가 너무 넓으면 다리 밑으로 목적지가 찍히는 버그 발생)
            Vector3 validDestination = destination;
            if (UnityEngine.AI.NavMesh.SamplePosition(destination, out UnityEngine.AI.NavMeshHit hit, 2.0f, UnityEngine.AI.NavMesh.AllAreas))
            {
                validDestination = hit.position;
            }

            bool pathFound = _agent.SetDestination(validDestination);
            _lastDestination = validDestination;
            
            if (!pathFound)
            {
                // 보정 후에도 실패할 경우, 강제로 일직선 방향으로 1미터 앞의 좌표라도 찍어줍니다.
                Vector3 dir = (validDestination - transform.position).normalized;
                _agent.SetDestination(transform.position + dir * 1.0f);
                Debug.LogWarning($"[AINavMeshAgent] {gameObject.name}: 보정된 목적지({validDestination})조차 도달 불가하여 1m 앞을 목표로 강제 이동합니다.");
            }
        }
    }

    /// <summary>
    /// 이동을 중지합니다.
    /// </summary>
    public void Stop()
    {
        if (_agent == null || !_agent.isActiveAndEnabled || !_agent.isOnNavMesh) return;
        _agent.isStopped = true;
        _agent.ResetPath();
        _agent.velocity = Vector3.zero;
        _lastDestination = Vector3.zero;
    }

    /// <summary>
    /// 수동으로 강제 이동(돌진 등)을 수행합니다.
    /// </summary>
    public void Move(Vector3 offset)
    {
        if (_agent != null && _agent.isActiveAndEnabled && _agent.isOnNavMesh)
        {
            _agent.Move(offset);
        }
    }

    /// <summary>
    /// 속도를 설정합니다.
    /// </summary>
    public void SetSpeed(float newSpeed)
    {
        if (_agent != null) _agent.speed = newSpeed;
    }

    /// <summary>
    /// 남은 거리를 반환합니다.
    /// </summary>
    public float GetRemainingDistance()
    {
        if (_agent == null || !_agent.isActiveAndEnabled || !_agent.hasPath) return 0f;
        return _agent.remainingDistance;
    }

    /// <summary>
    /// 목적지에 완전히 도달했는지 확인합니다. (경로 계산 중인 상태 제외)
    /// </summary>
    public bool HasReachedDestination()
    {
        if (_agent == null || !_agent.isActiveAndEnabled || _agent.pathPending) return false;
        if (!_agent.hasPath) return true; // 경로가 없으면 정지 상태로 간주
        return _agent.remainingDistance <= _agent.stoppingDistance;
    }

    /// <summary>
    /// 현재 계산된 이동 경로가 봉인된 구역을 통과하는지 확인합니다.
    /// </summary>
    public bool IsPathCrossingSealedZone()
    {
        if (_agent == null || !_agent.isActiveAndEnabled || !_agent.hasPath) return false;
        
        // 경로의 각 꺾이는 지점(코너) 중 하나라도 제한구역 내부에 있다면 true 반환
        foreach (var corner in _agent.path.corners)
        {
            if (ZoneBounds.IsPositionInSealedZone(corner))
            {
                return true;
            }
        }
        return false;
    }
}
