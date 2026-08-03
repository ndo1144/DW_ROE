using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// [도메인 명세 §2.4] 맵 정적 토템 12개의 상태 추적. 파괴/소멸 결과를 PhaseManager에 알림.
/// </summary>
public class TotemManager : NetworkBehaviour
{
    public static TotemManager Instance { get; private set; }

    private List<TotemController> _allTotems = new();
    private Dictionary<int, List<TotemController>> _totemsByZone = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        for (int i = 0; i < 4; i++)
        {
            _totemsByZone[i] = new List<TotemController>();
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
        {
            EventBus.OnZoneSeal += OnZoneSeal;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            EventBus.OnZoneSeal -= OnZoneSeal;
        }
        base.OnNetworkDespawn();
    }

    public void RegisterTotem(TotemController totem)
    {
        if (!_allTotems.Contains(totem))
        {
            _allTotems.Add(totem);
            if (_totemsByZone.TryGetValue(totem.zoneIndex, out var list))
            {
                list.Add(totem);
            }
        }
    }

    public void UnregisterTotem(TotemController totem)
    {
        _allTotems.Remove(totem);
        if (_totemsByZone.TryGetValue(totem.zoneIndex, out var list))
        {
            list.Remove(totem);
        }
    }

    /// <summary>
    /// Phase 2 진입 시 호출되어 모든 토템들의 내구도와 상태를 활성화시킵니다.
    /// </summary>
    public void InitializeForPhase2()
    {
        if (!IsServer) return;

        Debug.Log("[TotemManager] ===== Phase 2 토템 시스템 활성화 (총 12개 토템) =====");
        foreach (var totem in _allTotems)
        {
            if (totem == null || totem.zoneIndex < 0) continue; // 삭제되었거나 가짜 토템이면 건너뜀

            int maxDur = GetTotemMaxDurabilityForZone(totem.zoneIndex);
            totem.currentDurability.Value = maxDur;
            totem.state.Value = TotemState.Active;
            totem.gameObject.SetActive(true);
            Debug.Log($"[TotemManager] Totem {totem.name} (Zone {totem.zoneIndex}) 활성화. 내구도: {maxDur}");
        }
    }

    /// <summary>
    /// 토템이 대죄인에 의해 완전히 파괴되었을 때 호출됩니다.
    /// </summary>
    public void OnTotemDestroyed(TotemController totem)
    {
        if (!IsServer) return;

        Debug.Log($"[TotemManager] 토템 파괴 감지! (Zone: {totem.zoneIndex}) -> 해당 구역 봉인 시간 -30초 적용.");
        
        // PhaseManager에게 시간 단축 요청
        if (PhaseManager.Instance != null)
        {
            PhaseManager.Instance.ReduceZoneTime(30f);
        }
    }

    /// <summary>
    /// 구역이 강제로 봉인되었을 때 호출되어, 해당 구역 내 살아있던 토템들을 NaturalDepleted로 소멸시킵니다.
    /// </summary>
    private void OnZoneSeal(int zoneIndex)
    {
        if (!IsServer) return;

        if (_totemsByZone.TryGetValue(zoneIndex, out var totems))
        {
            int depletedCount = 0;
            foreach (var totem in totems)
            {
                if (totem != null && totem.state.Value == TotemState.Active)
                {
                    totem.state.Value = TotemState.NaturalDepleted;
                    depletedCount++;
                    
                    // 악인 사수 보너스: 봉인될 때까지 파괴되지 않고 살아남은 토템당 효과
                    if (PhaseManager.Instance != null)
                    {
                        int step = PhaseManager.Instance.GetSequenceIndexOfZone(zoneIndex);
                        if (step == 3) // 4번째(마지막) 구역
                        {
                            // 최종 구역 자연 소멸 효과 분기 (사수 성공)
                            Debug.Log($"[TotemManager] 최종 구역 토템 사수 성공! 토템 자연 소멸로 인한 Phase 3 특수 분기 발동 준비!");
                            // TODO: Phase 3에서 악인에게 유리한 버프 부여 등 추가 작업 필요 시 작성
                        }
                        else
                        {
                            PhaseManager.Instance.ExtendZoneTime(30f);
                        }
                    }
                }
            }
            Debug.Log($"[TotemManager] Zone {zoneIndex} 봉인으로 인해 미파괴 토템 {depletedCount}개 NaturalDepleted 처리 완료.");
        }
    }

    public int GetTotemMaxDurabilityForZone(int zoneIndex)
    {
        // [설계 변경] Zone에 관계없이 모든 토템은 3번 타격으로 파괴
        return 3;
    }

    public void Cleanup()
    {
        if (!IsServer) return;
        Debug.Log("[TotemManager] Phase 3 진입으로 인한 토템 시스템 Cleanup 실행.");
        foreach (var totem in _allTotems)
        {
            if (totem != null)
            {
                totem.gameObject.SetActive(false);
            }
        }
    }
}
