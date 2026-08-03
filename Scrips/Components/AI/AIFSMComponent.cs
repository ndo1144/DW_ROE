using UnityEngine;
using Unity.Netcode;
/// <summary>
/// AI 상태 열거형. 양(5상태) + 몬스터 추가상태(8상태).
/// </summary>
public enum AIState
{
    Idle,      // 배회
    Alert,     // 위협 감지, 주시
    Flee,      // 도주
    Patrol,    // 순찰 (몬스터)
    Chase,     // 추격 (몬스터)
    Attack,    // 공격 (몬스터)
    Cooldown,  // 공격 후 쿨다운 (몬스터)
    Roar,      // 포효 (베히모스)
    Charmed,   // 현혹 (가스펠)
    SelfDestruct, // 자폭 돌진 (가스펠)
    Dead       // 사망
}

/// <summary>
/// [도메인 명세 §3.2] FSM 상태 전이 호스팅.
/// 어린양 5상태(Idle/Alert/Flee/Dead + Patrol은 미사용) / 몬스터 8상태.
/// 서버에서만 실행됩니다.
/// </summary>
public class AIFSMComponent : MonoBehaviour
{
    private AINetworkController _parent;
    private AIBehaviorSO _behavior;

    // 상태별 타이머
    private float _wanderTimer = 0f;
    private float _fleeOutTimer = 0f;
    private float _cooldownTimer = 0f;
    private float _roarTimer = 0f;

    // 배회 목표 위치
    private Vector3 _wanderTarget;
    private bool _hasWanderTarget = false;

    // 가스펠 스킬(현혹 및 자폭) 관련 상태 변수
    private NetworkObject _charmer;
    public NetworkObject Charmer => _charmer;
    private Vector3 _selfDestructTarget;

    // ─────────────────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────────────────

    public void Initialize(AINetworkController parent, AIBehaviorSO behavior)
    {
        _parent = parent;
        _behavior = behavior;
        _wanderTimer = Random.Range(0f, behavior.wanderInterval); // 스폰 시 즉시 동기화 방지
        _roarTimer = behavior.roarInterval;
    }

    // ─────────────────────────────────────────────────────────
    // Tick (서버 FixedUpdate)
    // ─────────────────────────────────────────────────────────

    public void Tick(float deltaTime)
    {
        switch (_parent.currentState.Value)
        {
            case AIState.Idle:   TickIdle(deltaTime);   break;
            case AIState.Alert:  TickAlert(deltaTime);  break;
            case AIState.Flee:   TickFlee(deltaTime);   break;
            case AIState.Patrol: TickPatrol(deltaTime); break;
            case AIState.Chase:  TickChase(deltaTime);  break;
            case AIState.Attack: TickAttack();          break;
            case AIState.Cooldown: TickCooldown(deltaTime); break;
            case AIState.Roar:   TickRoar();            break;
            case AIState.Charmed: TickCharmed(deltaTime); break;
            case AIState.SelfDestruct: TickSelfDestruct(deltaTime); break;
            case AIState.Dead:                          break;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 양 — Idle: 반경 내 배회 + 감지 시 Alert
    // ─────────────────────────────────────────────────────────

    private void TickIdle(float deltaTime)
    {
        // 1. 배회 로직 (이동 중에는 타이머 정지, 도착 시 대기)
        if (_hasWanderTarget)
        {
            // 목적지에 완전히 도착했는지 확인
            if (_parent.navAgent.HasReachedDestination())
            {
                _hasWanderTarget = false;
                _wanderTimer = _behavior.wanderInterval + Random.Range(-1f, 3f);
                _parent.navAgent.Stop(); // 풀 뜯으며 대기
            }
        }
        else
        {
            // 대기 중일 때만 타이머 감소
            _wanderTimer -= deltaTime;
            if (_wanderTimer <= 0f)
            {
                SetNewWanderTarget();
            }
        }

        // 위협(플레이어) 감지
        var threat = _parent.perception.GetClosestThreat();
        if (threat != null)
        {
            var lamb = _parent.GetComponent<LambController>();
            // 도주형(무구한 양 등) 개체만 위협에 겁먹어 경계 상태로 진입. isFleeing 필드를 기준으로 분기.
            bool isSkittish = (lamb != null && lamb.isFleeing);

            if (isSkittish)
            {
                // 겁이 많아 경계(Alert) 상태 진입 (멈춰서 주시)
                _parent.perception.SetCurrentThreat(threat);
                _parent.currentState.Value = AIState.Alert;
            }
            // 일반 양은 먼저 공격받기 전까지는 플레이어를 신경 쓰지 않고 평화롭게 배회합니다.
        }
    }

    private void SetNewWanderTarget()
    {
        // 양은 평화로울 때 매우 느긋하고 천천히 걷도록 설정 (속도를 40%로 감소)
        float walkSpeed = _behavior.isLamb ? _behavior.moveSpeed * 0.4f : _behavior.moveSpeed;
        _parent.navAgent.SetSpeed(walkSpeed);

        // 배회 시 한곳에 옹기종기 모이지 않도록 최소 거리를 보장하여 넓게 퍼지도록 설정
        Vector2 rndDir = Random.insideUnitCircle.normalized;
        float rndDist = Random.Range(_behavior.wanderRadius * 0.5f, _behavior.wanderRadius);
        _wanderTarget = _parent.transform.position + new Vector3(rndDir.x, 0f, rndDir.y) * rndDist;
        
        _parent.navAgent.SetDestination(_wanderTarget);
        _hasWanderTarget = true;

        // 배회 이동 시작 시 Idle SFX 재생 (쿨다운은 SoundConfigSO에서 제어)
        // 단, 타겟/위협이 있는 전투 상태(분노한 양·교전 중 몬스터)에서는 평화로운 Idle 울음을 내지 않아
        // 공격 사운드(LambAttack 등)와 겹치지 않도록 한다.
        bool isPeaceful = _parent.perception.CurrentTarget == null
                          && _parent.perception.CurrentThreat == null;
        if (isPeaceful)
            SoundManager.Instance?.PlaySFX(GetIdleSFXType(), _parent.transform.position);
    }

    // ─────────────────────────────────────────────────────────
    // 무구한 양 전용 — Alert: 주시, 2m 이내면 도주
    // ─────────────────────────────────────────────────────────

    private void TickAlert(float deltaTime)
    {
        var threat = _parent.perception.CurrentThreat;
        if (threat == null)
        {
            _parent.currentState.Value = AIState.Idle;
            return;
        }

        // 위협 방향 응시 (제자리 정지 상태)
        _parent.navAgent.Stop();

        Vector3 dir = (threat.transform.position - _parent.transform.position);
        dir.y = 0f;
        if (dir != Vector3.zero)
            _parent.transform.rotation = Quaternion.Slerp(
                _parent.transform.rotation,
                Quaternion.LookRotation(dir),
                10f * Time.fixedDeltaTime);

        float dist = Vector3.Distance(_parent.transform.position, threat.transform.position);

        if (dist < 2.0f)
        {
            // 너무 가까우면 도주
            TriggerFlee(threat);
        }
        else if (dist > _parent.perception.PerceptionRadius)
        {
            // 감지 범위 벗어나면 다시 배회
            _parent.perception.ClearThreat();
            _parent.currentState.Value = AIState.Idle;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 양 — Flee: 위협 반대 방향 도주 (3초간 유지 및 감속)
    // ─────────────────────────────────────────────────────────

    // 도주 시작 시 순간적으로 치고 나가는 가속(버스트) 설정값
    private const float FLEE_BURST_MULTIPLIER = 4.0f; // 버스트 구간 속도 배율
    private const float FLEE_BURST_DURATION = 0.5f;   // 버스트 유지 시간(초)
    private const float FLEE_DECAY_DURATION = 0.6f;   // 버스트 종료 후 평소 속도(1배)까지 줄어드는 데 걸리는 시간(초)

    public void TriggerFlee(NetworkObject threat)
    {
        _parent.perception.SetCurrentThreat(threat);
        _parent.currentState.Value = AIState.Flee;
        _fleeOutTimer = 0f;
        // 도주 반응을 체감하기 쉽도록 즉시 순간 가속(버스트)으로 튀어나간다.
        _parent.navAgent.SetSpeed(_behavior.moveSpeed * FLEE_BURST_MULTIPLIER);
    }

    private void TickFlee(float deltaTime)
    {
        var threat = _parent.perception.CurrentThreat;

        // 도주 타이머는 시야와 무관하게 3초간 무조건 흐름
        _fleeOutTimer += deltaTime;

        // 0~0.5초: 4배속 버스트 유지. 0.5~1.1초: 4배속 -> 1배속으로 빠르게 감속. 이후: 평소 속도(1배) 유지.
        float currentSpeed;
        if (_fleeOutTimer < FLEE_BURST_DURATION)
        {
            currentSpeed = _behavior.moveSpeed * FLEE_BURST_MULTIPLIER;
        }
        else
        {
            float decayT = Mathf.Clamp01((_fleeOutTimer - FLEE_BURST_DURATION) / FLEE_DECAY_DURATION);
            currentSpeed = Mathf.Lerp(_behavior.moveSpeed * FLEE_BURST_MULTIPLIER, _behavior.moveSpeed, decayT);
        }
        _parent.navAgent.SetSpeed(currentSpeed);

        if (threat != null)
        {
            // 위협 반대 방향으로 계속해서 목적지 갱신
            Vector3 awayDir = (_parent.transform.position - threat.transform.position).normalized;
            Vector3 fleeTarget = _parent.transform.position + awayDir * _behavior.wanderRadius * 2f;
            _parent.navAgent.SetDestination(fleeTarget);
        }

        // 3초 경과 시 도주 완전 종료
        if (_fleeOutTimer >= 3.0f)
        {
            _parent.navAgent.SetSpeed(_behavior.moveSpeed * 0.4f); // 평화로운 속도로 복귀
            _parent.perception.ClearThreat();
            _parent.currentState.Value = AIState.Idle;
            _hasWanderTarget = false;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 몬스터 — Patrol
    // ─────────────────────────────────────────────────────────

    private void TickPatrol(float deltaTime)
    {
        if (_hasWanderTarget)
        {
            if (_parent.navAgent.HasReachedDestination())
            {
                _hasWanderTarget = false;
                _wanderTimer = _behavior.wanderInterval + Random.Range(-1f, 1f);
                _parent.navAgent.Stop();
            }
        }
        else
        {
            _wanderTimer -= deltaTime;
            if (_wanderTimer <= 0f)
            {
                SetNewWanderTarget();
            }
        }

        var target = _parent.perception.GetClosestPlayer();
        if (target != null)
        {
            _parent.perception.SetCurrentTarget(target);
            _parent.currentState.Value = AIState.Chase;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 공통 — Chase (몬스터 & 분노한 양)
    // ─────────────────────────────────────────────────────────

    private void TickChase(float deltaTime)
    {
        // 포효 타이머 (베히모스 등 몬스터 전용)
        if (!_behavior.isLamb && _behavior.roarInterval > 0)
        {
            _roarTimer -= deltaTime;
            if (_roarTimer <= 0f)
            {
                _parent.currentState.Value = AIState.Roar;
                return;
            }
        }

        var target = _parent.perception.CurrentTarget;
        if (target == null)
        {
            _parent.currentState.Value = _behavior.isLamb ? AIState.Idle : AIState.Patrol;
            return;
        }

        // 추격 중 대상이 다른 층(Y축 큰 차이)으로 벗어나면 추격을 포기한다 (위/아래층으로 끌려다니는 것 방지)
        if (Mathf.Abs(_parent.transform.position.y - target.transform.position.y) > _parent.perception.MaxVerticalDistance)
        {
            _parent.perception.ClearTarget();
            _parent.currentState.Value = _behavior.isLamb ? AIState.Idle : AIState.Patrol;
            return;
        }
        var targetPosHealth=target.GetComponent<PlayerHealthComponent>();
        if(targetPosHealth==null || targetPosHealth.currentHP.Value<=0)
        {
            _parent.perception.ClearTarget();
            _parent.currentState.Value = _behavior.isLamb ? AIState.Idle : AIState.Patrol;
            return;
        }
        _parent.navAgent.SetDestination(target.transform.position);

        // [핵심 보정] 피벗 및 높낮이(Y축) 편차로 인한 공격 사거리 오차 방지를 위해 XZ 평면 수평 거리 계산
        Vector3 myPos = _parent.transform.position;
        Vector3 targetPos = target.transform.position;
        myPos.y = 0f;
        targetPos.y = 0f;
        float dist = Vector3.Distance(myPos, targetPos);
        
        // [보정] 인스펙터에서 attackRange가 너무 작거나 0일 경우, 
        // 콜라이더에 막혀 영원히 사거리 내로 진입하지 못하는 버그 방지 (최소 2.5m 보장)
        float attackDist = _behavior.isLamb ? 2.0f : Mathf.Max(_behavior.attackRange, 2.5f);

        if (dist <= attackDist)
        {
            _parent.navAgent.Stop();
            _parent.currentState.Value = AIState.Attack;
        }
    }

    // ─────────────────────────────────────────────────────────
    // 공통 — Attack (몬스터 & 분노한 양)
    // ─────────────────────────────────────────────────────────

    private void TickAttack()
    {
        var target = _parent.perception.CurrentTarget;
        if (target != null)
        {
            var targetPosHealth=target.GetComponent<PlayerHealthComponent>();
            if(targetPosHealth==null || targetPosHealth.currentHP.Value<=0)
            {
                _parent.perception.ClearTarget();
                _parent.currentState.Value = _behavior.isLamb ? AIState.Idle : AIState.Patrol;
                return;
            }
            
            // 양의 기본 공격력을 기존 5에서 2로 대폭 하향 조정 (너무 아프다는 피드백 반영)
            int damage = _behavior.isLamb ? 2 : _behavior.attackDamage;
            
            // 인스펙터에서 강제로 덮어쓴 공격력이 있다면 우선 적용
            if (_parent.overrideAttackDamage > 0)
            {
                damage = _parent.overrideAttackDamage;
            }

            Debug.Log($"[AIFSMComponent] {gameObject.name}가 대상 {target.name}에게 공격을 수행합니다! (데미지: {damage})");
            
            if (_behavior.isLamb)
            {
                // 양은 별도의 돌진 코루틴 안에서 SFX와 데미지, 이동을 모두 처리 (플레이어 대응 딜레이 포함)
                StartCoroutine(LambBodySlamDash(target, damage));
            }
            else
            {
                // 일반 몬스터 공격 로직 — 전 클라이언트 브로드캐스트(악인도 듣도록)
                _parent.PlayAISfx(GetAttackSFXType(), _parent.transform.position);

                if (CombatManager.Instance != null)
                {
                    CombatManager.Instance.ApplyDamage(_parent.NetworkObject, target, damage);
                }
                else
                {
                    var playerHealth = target.GetComponent<PlayerHealthComponent>();
                    if (playerHealth != null)
                        playerHealth.ServerApplyDamage(damage);
                }
            }
        }

        // 공격 후 쿨다운 (양은 선딜레이 0.6초가 있으므로 쿨다운을 조금 더 길게 주어 연타 방지)
        _cooldownTimer = _behavior.isLamb ? 2.5f : _behavior.attackCooldown;
        _parent.currentState.Value = AIState.Cooldown;
    }

    // 양 전용: 선딜레이 후 몸통박치기 연출 (몸 뚫림 방지 및 회전 보정 적용)
    private System.Collections.IEnumerator LambBodySlamDash(NetworkObject target, int damage)
    {
        if (_parent.navAgent == null || !_parent.navAgent.IsOnNavMesh || target == null) yield break;

        // 1. 공격 전 타겟을 확실하게 바라보도록 강제 회전 (장애물 위 궁둥이 공격 방지)
        Vector3 targetPos = target.transform.position;
        Vector3 dir = (targetPos - _parent.transform.position).normalized;
        dir.y = 0;
        if (dir != Vector3.zero)
            _parent.transform.rotation = Quaternion.LookRotation(dir);

        // 2. 공격 전 선딜레이 (플레이어가 보고 대응할 수 있는 시간 제공)
        yield return new WaitForSeconds(0.6f);

        if (target == null) yield break;

        // 3. 돌진 직전에 방향 한 번 더 갱신
        targetPos = target.transform.position;
        dir = (targetPos - _parent.transform.position).normalized;
        dir.y = 0;
        if (dir != Vector3.zero)
            _parent.transform.rotation = Quaternion.LookRotation(dir);

        // 돌진 시점 공격 사운드 — 전 클라이언트 브로드캐스트(악인도 듣도록)
        _parent.PlayAISfx(SFXType.LambAttack, _parent.transform.position);
        
        float duration = 0.15f; 
        float elapsed = 0f;
        float dashSpeed = 10f; // 기존 15에서 10으로 하향 조정
        
        // 4. 돌진 (거리가 가까워지면 몸통이 뚫리지 않도록 멈춤)
        while (elapsed < duration)
        {
            if (target == null) break;

            elapsed += Time.deltaTime;

            Vector3 myPos = _parent.transform.position; myPos.y = 0;
            Vector3 tPos = target.transform.position; tPos.y = 0;
            
            // 대상과 1.0m 이내로 가까워지면 몸 뚫림 방지를 위해 전진 중단
            if (Vector3.Distance(myPos, tPos) < 1.0f)
            {
                break;
            }

            _parent.navAgent.Move(dir * (dashSpeed * Time.deltaTime));
            yield return null;
        }

        // 5. 돌진 후 타격 판정 및 데미지 적용
        if (target != null)
        {
            if (CombatManager.Instance != null)
            {
                CombatManager.Instance.ApplyDamage(_parent.NetworkObject, target, damage);
            }
            else
            {
                var playerHealth = target.GetComponent<PlayerHealthComponent>();
                if (playerHealth != null)
                    playerHealth.ServerApplyDamage(damage);
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // 공통 — Cooldown
    // ─────────────────────────────────────────────────────────

    private void TickCooldown(float deltaTime)
    {
        _cooldownTimer -= deltaTime;
        if (_cooldownTimer <= 0f)
        {
            var target = _parent.perception.CurrentTarget;
            _parent.currentState.Value = target != null ? AIState.Chase : (_behavior.isLamb ? AIState.Idle : AIState.Patrol);
        }
    }

    // ─────────────────────────────────────────────────────────
    // 몬스터 — Roar (베히모스)
    // ─────────────────────────────────────────────────────────

    private void TickRoar()
    {
        Debug.Log($"[AIFSMComponent] {gameObject.name} 포효! 반경 {_behavior.roarRadius}m 스턴 {_behavior.roarStunDuration}초.");
        var hits = Physics.OverlapSphere(_parent.transform.position, _behavior.roarRadius);
        foreach (var hit in hits)
        {
            var moveCtrl = hit.GetComponentInParent<PlayerMovementController>();
            if (moveCtrl != null)
            {
                // 서버 단에서 플레이어에게 네트워크 기반 스턴을 기시화하고 타이밍 해제 코루틴 작동
                StartCoroutine(ApplyStunToPlayer(moveCtrl, _behavior.roarStunDuration));
            }
        }

        _roarTimer = _behavior.roarInterval;
        _parent.currentState.Value = AIState.Chase;
    }

    private System.Collections.IEnumerator ApplyStunToPlayer(PlayerMovementController moveCtrl, float duration)
    {
        if (moveCtrl != null)
        {
            moveCtrl.isStunned.Value = true;
            Debug.Log($"[AIFSMComponent] 포효 적중: {moveCtrl.gameObject.name} 스턴 부여 ({duration}초)");
            yield return new WaitForSeconds(duration);
            if (moveCtrl != null)
            {
                moveCtrl.isStunned.Value = false;
                Debug.Log($"[AIFSMComponent] 포효 해제: {moveCtrl.gameObject.name} 스턴 해제");
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // 가스펠 스킬 — Charmed (현혹)
    // ─────────────────────────────────────────────────────────

    public void TriggerCharm(NetworkObject charmer)
    {
        if (_parent.currentState.Value == AIState.Dead || _parent.currentState.Value == AIState.SelfDestruct) return;
        
        _charmer = charmer;
        _parent.currentState.Value = AIState.Charmed;
        _parent.perception.ClearThreat();
        _parent.perception.ClearTarget();
        _hasWanderTarget = false;
        
        // 플레이어를 잘 따라오도록 이속 1.5배 증가
        _parent.navAgent.SetSpeed(_behavior.moveSpeed * 1.5f);
        
        Debug.Log($"[AIFSMComponent] {_parent.gameObject.name}이(가) {charmer.name}에게 현혹되었습니다.");
    }

    private void TickCharmed(float deltaTime)
    {
        if (_charmer == null)
        {
            _parent.currentState.Value = _behavior.isLamb ? AIState.Idle : AIState.Patrol;
            return;
        }

        float dist = Vector3.Distance(_parent.transform.position, _charmer.transform.position);
        if (dist > 3.0f)
        {
            _parent.navAgent.SetDestination(_charmer.transform.position);
        }
        else
        {
            _parent.navAgent.Stop(); // 가까이 오면 정지
        }
    }

    // ─────────────────────────────────────────────────────────
    // 가스펠 스킬 — SelfDestruct (자폭 돌진)
    // ─────────────────────────────────────────────────────────

    public void TriggerSelfDestruct(Vector3 targetPos)
    {
        if (_parent.currentState.Value == AIState.Dead) return;
        
        _selfDestructTarget = targetPos;
        _parent.currentState.Value = AIState.SelfDestruct;
        
        // 목표 지점으로 미친듯이 돌진 (속도 3배)
        _parent.navAgent.SetSpeed(_behavior.moveSpeed * 3.0f);
        _parent.navAgent.SetDestination(_selfDestructTarget);
        
        Debug.Log($"[AIFSMComponent] {_parent.gameObject.name} 자폭 돌진 시작! (목표: {_selfDestructTarget})");
    }

    private void TickSelfDestruct(float deltaTime)
    {
        // Y축 편차 무시를 위해 수평 거리만 계산
        Vector3 pos1 = _parent.transform.position; pos1.y = 0;
        Vector3 pos2 = _selfDestructTarget; pos2.y = 0;

        float dist = Vector3.Distance(pos1, pos2);
        
        // 1.5m 이내로 접근하거나 네비게이션 상 도착 판정 시 자폭
        if (dist < 1.5f || _parent.navAgent.HasReachedDestination())
        {
            ExplodeAndDie();
        }
        else
        {
            // 돌진 중에도 목표 보정
            _parent.navAgent.SetDestination(_selfDestructTarget);
        }
    }

    private void ExplodeAndDie()
    {
        // 상태 전이 방지
        _parent.currentState.Value = AIState.Dead;

        if (CombatManager.Instance != null)
        {
            // 4m 반경 광역 데미지
            float explosionRadius = 4f;
            Collider[] hits = Physics.OverlapSphere(_parent.transform.position, explosionRadius);
            
            // 시각적 피드백 (플레이어 컨트롤러의 임시 VFX 재활용)
            if (_charmer != null && _charmer.TryGetComponent<PlayerNetworkController>(out var pnc))
            {
                // S3 폭발이므로 파란색 큰 구체(SkillId=2) 재활용 (시각적 임시 표현)
                pnc.Client_PlaySkillVFXClientRpc(2, _parent.transform.position); 
            }

            foreach (var hit in hits)
            {
                if (hit.TryGetComponent<NetworkObject>(out var netObj))
                {
                    // 본인, 현혹자(가스펠), 그리고 다른 현혹된 아군은 피해 면역 (팀킬 방지)
                    if (netObj == _parent.NetworkObject || netObj == _charmer) continue;
                    
                    bool isOtherCharmedAlly = false;
                    if (netObj.TryGetComponent<AINetworkController>(out var otherAI))
                    {
                        if (otherAI.fsm.Charmer == _charmer) isOtherCharmedAlly = true;
                    }

                    if (!isOtherCharmedAlly)
                    {
                        CombatManager.Instance.ApplyDamage(_charmer != null ? _charmer : _parent.NetworkObject, netObj, 50); // 고정 자폭 딜 50
                    }
                }
            }
        }

        // 즉사 (AIHealthComponent 활용)
        if (_parent.TryGetComponent<AINetworkController>(out var aiNet))
        {
            aiNet.health.TakeDamage(9999, _charmer);
        }
    }

    // ─────────────────────────────────────────────────────────
    // SFX 헬퍼
    // ─────────────────────────────────────────────────────────

    /// <summary>monsterIndex 기반으로 공격 SFX 타입을 반환합니다.</summary>
    private SFXType GetAttackSFXType()
    {
        if (_behavior.isLamb) return SFXType.LambAttack;
        return _behavior.monsterIndex switch
        {
            2 => SFXType.Monster2_Attack,
            3 => SFXType.Monster3_Attack,
            _ => SFXType.Monster1_Attack,
        };
    }

    /// <summary>monsterIndex 기반으로 Idle SFX 타입을 반환합니다.</summary>
    private SFXType GetIdleSFXType()
    {
        if (_behavior.isLamb) return SFXType.LambIdle;
        return _behavior.monsterIndex switch
        {
            2 => SFXType.Monster2_Idle,
            3 => SFXType.Monster3_Idle,
            _ => SFXType.Monster1_Idle,
        };
    }
}
