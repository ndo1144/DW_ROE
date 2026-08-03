using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using System;

/// <summary>
/// [도메인 명세 §2.5] 4개의 제단을 총괄하고 등록/조회를 관리합니다.
/// (직선 빔 렌더링은 제거됨. 악인의 제단 안내는 AltarNavigationGuide의 바닥 경로선으로 일원화)
/// </summary>
public class AltarManager : NetworkBehaviour
{
    public static AltarManager Instance { get; private set; }

    private List<AltarController> _altars = new List<AltarController>();
    public IReadOnlyList<AltarController> Altars => _altars;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        // OnNetworkSpawn 순서 문제(제단이 매니저보다 먼저 스폰됨)를 방지하기 위해 Awake에서 직접 씬의 모든 제단을 긁어옵니다.
        var altarsInScene = FindObjectsOfType<AltarController>();
        foreach (var altar in altarsInScene)
        {
            RegisterAltar(altar);
        }
    }

    public void RegisterAltar(AltarController altar)
    {
        if (!_altars.Contains(altar)) _altars.Add(altar);
    }

    public void UnregisterAltar(AltarController altar)
    {
        _altars.Remove(altar);
    }

    public IReadOnlyList<AltarController> GetAllAltars() => _altars;

    // 위치 기준 가장 가까운 제단 반환 (클라이언트에서도 사용 가능).
    public AltarController GetClosestAltarByPosition(Vector3 pos)
    {
        AltarController closest = null;
        float minDist = float.MaxValue;
        foreach (var altar in _altars)
        {
            if (altar == null) continue;
            float dist = Vector3.Distance(pos, altar.transform.position);
            if (dist < minDist)
            {
                minDist = dist;
                closest = altar;
            }
        }
        return closest;
    }

    public void Disable()
    {
        // P3 진입 시 호출
        _altars.Clear();
    }
}
