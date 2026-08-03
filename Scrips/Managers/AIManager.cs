using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// [도메인 명세 §2.3] 어린양·몬스터 AI FSM 호스트. 모든 NPC AI의 Tick 관리.
/// 서버(호스트) 단독 권한으로 동작합니다.
/// </summary>
public class AIManager : MonoBehaviour
{
    public static AIManager Instance { get; private set; }

    // ─────────────────────────────────────────────────────────
    // 등록된 에이전트 목록
    // ─────────────────────────────────────────────────────────

    private readonly List<AINetworkController> _activeAgents = new List<AINetworkController>();

    private bool _isServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    // ─────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private int _tickSlice = 5; // AI 연산을 5프레임으로 분산 (50Hz -> 10Hz)
    private int _currentTickFrame = 0;

    private void FixedUpdate()
    {
        if (!_isServer) return;

        // null이 된 에이전트 정리 (Despawn된 오브젝트)
        _activeAgents.RemoveAll(a => a == null || !a.IsAlive);

        int count = _activeAgents.Count;
        if (count == 0) return;

        // 부하 분산(Time-Slicing): 전체 AI를 5개 그룹으로 나누어 매 프레임마다 1개 그룹만 연산
        float slicedDeltaTime = Time.fixedDeltaTime * _tickSlice; // 호출 주기가 길어졌으므로 dt 보정

        for (int i = _currentTickFrame; i < count; i += _tickSlice)
        {
            var agent = _activeAgents[i];
            if (agent != null && agent.IsAlive)
            {
                agent.Tick(slicedDeltaTime);
            }
        }

        _currentTickFrame = (_currentTickFrame + 1) % _tickSlice;
    }

    // ─────────────────────────────────────────────────────────
    // 등록 / 해제
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// AI 에이전트를 Tick 목록에 등록합니다. (AINetworkController.OnNetworkSpawn에서 호출)
    /// </summary>
    public void RegisterAgent(AINetworkController agent)
    {
        if (!_activeAgents.Contains(agent))
        {
            _activeAgents.Add(agent);
            Debug.Log($"[AIManager] 에이전트 등록: {agent.gameObject.name} (총 {_activeAgents.Count}개)");
        }
    }

    /// <summary>
    /// AI 에이전트를 Tick 목록에서 제거합니다. (사망/Despawn 시 호출)
    /// </summary>
    public void UnregisterAgent(AINetworkController agent)
    {
        if (_activeAgents.Remove(agent))
            Debug.Log($"[AIManager] 에이전트 해제: {agent.gameObject.name} (남은 {_activeAgents.Count}개)");
    }

    /// <summary>
    /// 해당 에이전트가 현재 목록에 등록되어 있는지 확인합니다.
    /// </summary>
    public bool IsAgentRegistered(AINetworkController agent)
    {
        return _activeAgents.Contains(agent);
    }

    // ─────────────────────────────────────────────────────────
    // 유틸리티
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 지정된 agent 반경 내에서 가장 가까운 악인 플레이어의 NetworkObject를 반환합니다.
    /// </summary>
    public NetworkObject GetClosestThreat(AINetworkController agent, float range)
    {
        if (PlayerManager.Instance == null) return null;

        NetworkObject closest = null;
        float closestDist = float.MaxValue;

        foreach (var session in PlayerManager.Instance.GetAllSessions())
        {
            if (session.avatarObject == null) continue;

            float dist = Vector3.Distance(agent.transform.position, session.avatarObject.transform.position);
            if (dist <= range && dist < closestDist)
            {
                closestDist = dist;
                closest = session.avatarObject;
            }
        }

        return closest;
    }

    // ─────────────────────────────────────────────────────────
    // 가스펠 스킬 (NPC 현혹 및 제어)
    // ─────────────────────────────────────────────────────────

    public void CharmNPCsInRadius(Vector3 center, float radius, NetworkObject caster)
    {
        int charmedCount = 0;
        // _activeAgents 리스트 누락 방지를 위해 확실한 물리 판정 사용
        Collider[] hits = Physics.OverlapSphere(center, radius);
        foreach (var hit in hits)
        {
            // NPC 최상위 객체의 AINetworkController를 찾음
            var agent = hit.GetComponentInParent<AINetworkController>();
            if (agent != null && agent.IsAlive)
            {
                // 중복 현혹 호출 방지
                if (agent.currentState.Value != AIState.Charmed || agent.fsm.Charmer != caster)
                {
                    agent.fsm.TriggerCharm(caster);
                    charmedCount++;
                }
            }
        }
        Debug.Log($"[AIManager] {caster.name}이(가) 반경 {radius}m 내의 NPC {charmedCount}마리를 현혹했습니다.");
    }

    public void OrderCharmedNPCsToSelfDestruct(Vector3 targetPos, NetworkObject caster)
    {
        int orderCount = 0;
        foreach (var agent in _activeAgents)
        {
            if (agent == null || !agent.IsAlive) continue;

            if (agent.fsm.Charmer == caster)
            {
                agent.fsm.TriggerSelfDestruct(targetPos);
                orderCount++;
            }
        }
        Debug.Log($"[AIManager] {caster.name}이(가) 현혹한 NPC {orderCount}마리에게 {targetPos}로의 자폭을 명령했습니다.");
    }

    /// <summary>
    /// 현재 등록된 에이전트 수를 반환합니다.
    /// </summary>
    public int ActiveAgentCount => _activeAgents.Count;

    // ─────────────────────────────────────────────────────────
    // 클라이언트 이탈 시 봇 대체 (BotPlayerAgent)
    // ─────────────────────────────────────────────────────────

    public void SwitchPlayerToBot(ulong clientId, PlayerNetworkController playerNet)
    {
        if (!_isServer || playerNet == null) return;

        Debug.Log($"[AIManager] 플레이어 {clientId} 이탈 감지. BotPlayerAgent를 부착하여 AI 봇으로 대체합니다.");
        
        // 봇 AI 컴포넌트 추가
        var botAgent = playerNet.gameObject.GetComponent<BotPlayerAgent>();
        if (botAgent == null)
        {
            botAgent = playerNet.gameObject.AddComponent<BotPlayerAgent>();
        }

        // 초기화
        botAgent.Initialize(playerNet);
    }
}
