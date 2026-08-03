using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

/// <summary>
/// [도메인 명세 §3.10] 구역 봉인 시 잔류 플레이어에게 도트 데미지를 가하는 볼륨입니다.
/// 인스펙터에서 대죄인/악인별 데미지를 각각 설정할 수 있습니다.
/// </summary>
public class ZoneTriggerVolume : MonoBehaviour
{
    [Header("구역 설정")]
    [Tooltip("이 트리거가 관리하는 구역 인덱스 (0=ZoneA, 1=ZoneB, 2=ZoneC, 3=ZoneD)")]
    public int zoneIndex = 0;

    [Tooltip("도트 데미지 틱 간격 (초 단위, 1.0 = 매 1초마다)")]
    [SerializeField, Range(0.1f, 5f)] private float tickInterval = 1.0f;

    [Header("대죄인 도트 데미지")]
    [Tooltip("true: 최대 체력 비율(%) / false: 고정 수치")]
    [SerializeField] private bool sinnerUsePercent = true;

    [Tooltip("비율 모드 — 틱당 최대 체력의 몇 %를 깎을지 (0.5 = 50%)")]
    [SerializeField, Range(0.01f, 1f)] private float sinnerDamagePercent = 0.5f;

    [Tooltip("고정 모드 — 틱당 깎을 고정 수치")]
    [SerializeField] private int sinnerFlatDamage = 50;

    [Header("악인 도트 데미지")]
    [Tooltip("true: 최대 체력 비율(%) / false: 고정 수치")]
    [SerializeField] private bool criminalUsePercent = true;

    [Tooltip("비율 모드 — 틱당 최대 체력의 몇 %를 깎을지 (0.5 = 50%)")]
    [SerializeField, Range(0.01f, 1f)] private float criminalDamagePercent = 0.5f;

    [Tooltip("고정 모드 — 틱당 깎을 고정 수치")]
    [SerializeField] private int criminalFlatDamage = 50;

    [Header("런타임 상태 (읽기 전용)")]
    [SerializeField] private bool isSealed = false;
    
    [Header("유예 시간 설정")]
    [Tooltip("구역 봉인 직후, 대죄인이 도망갈 수 있도록 데미지를 받지 않는 유예 시간(초)")]
    [SerializeField] private float sinnerGracePeriod = 10.0f;

    private float _damageTimer = 0f;
    private float _sealedTime = 0f;
    private ZoneBounds _bounds;

    private void Start()
    {
        _bounds = GetComponent<ZoneBounds>();
        if (_bounds == null)
        {
            _bounds = GetComponentInParent<ZoneBounds>();
        }

        // 인스펙터 입력 실수로 ZoneBounds와 ZoneTriggerVolume의 인덱스가 다르게 설정되는 것을 방지
        if (_bounds != null)
        {
            zoneIndex = _bounds.zoneIndex;
        }

        EventBus.OnZoneSeal += OnZoneSeal;
        
        // [방어 코드] 씬/프리팹 인스펙터에서 실수로 isSealed가 true로 체크되어 있는 경우를 방지
        isSealed = false;

        // 만약 이미 진행 중인 게임 중간에 활성화되었다면 체크 (랜덤 순서 대응)
        if (PhaseManager.Instance != null && PhaseManager.Instance.IsZoneSealed(zoneIndex))
        {
            isSealed = true;
        }
    }

    private void OnDestroy()
    {
        EventBus.OnZoneSeal -= OnZoneSeal;
    }

    private void OnZoneSeal(int sealedZoneIndex)
    {
        if (sealedZoneIndex == zoneIndex)
        {
            isSealed = true;
            _sealedTime = Time.time; // 구역 봉인 시간 기록 (유예 시간 계산용)
            Debug.Log($"[ZoneTriggerVolume] Zone {zoneIndex} 봉인! 대죄인: {(sinnerUsePercent ? sinnerDamagePercent * 100 + "%" : sinnerFlatDamage + "고정")}, 악인: {(criminalUsePercent ? criminalDamagePercent * 100 + "%" : criminalFlatDamage + "고정")}, 간격: {tickInterval}초");
        }
    }

    private void FixedUpdate()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
        if (!isSealed) return;

        _damageTimer += Time.fixedDeltaTime;
        if (_damageTimer >= tickInterval)
        {
            _damageTimer = 0f;
            ApplySealDamage();
        }
    }

    /// <summary>
    /// 대죄인/악인 여부에 따라 인스펙터에서 설정한 각각의 데미지를 적용합니다.
    /// </summary>
    private void ApplySealDamage()
    {
        if (_bounds == null) return;

        foreach (var client in Unity.Netcode.NetworkManager.Singleton.ConnectedClientsList)
        {
            if (client.PlayerObject == null) continue;
            
            var playerObj = client.PlayerObject.gameObject;
            var netObj = client.PlayerObject;

            if (_bounds.Contains(playerObj.transform.position))
            {
                var health = playerObj.GetComponent<PlayerHealthComponent>();
                if (health == null || health.currentHP.Value <= 0) continue;

                // 대죄인 여부 판별
                var pnc = playerObj.GetComponent<PlayerNetworkController>();
                bool isSinner = pnc != null && pnc.isGreatSinner;

                // [유예 시간 로직] 대죄인의 경우 구역 봉인 직후 일정 시간(sinnerGracePeriod) 동안 도트 데미지 무시
                if (isSinner && (Time.time - _sealedTime) < sinnerGracePeriod)
                {
                    continue; 
                }

                // 사타니즘(Satanism) 등으로 인한 성화 무효화 상태 체크
                if (CombatManager.Instance != null && CombatManager.Instance.HasStatusEffect(netObj, StatusEffectType.HolyFireImmune))
                {
                    continue;
                }

                // 임시 조치: 인스펙터에 설정된 데미지 비율이 너무 높을 경우(예: 0.5 = 50%) 2초 만에 즉사하는 현상을 방지하기 위해 최대 5%로 캡핑
                float sDamagePct = Mathf.Min(sinnerDamagePercent, 0.05f);
                float cDamagePct = Mathf.Min(criminalDamagePercent, 0.05f);

                // 역할별 데미지 계산
                int damageAmount;
                if (isSinner)
                {
                    int safeFlat = Mathf.Min(sinnerFlatDamage, Mathf.Max(1, Mathf.RoundToInt(health.maxHP.Value * 0.05f)));
                    damageAmount = sinnerUsePercent
                        ? Mathf.Max(1, Mathf.RoundToInt(health.maxHP.Value * sDamagePct))
                        : safeFlat;
                }
                else
                {
                    int safeFlat = Mathf.Min(criminalFlatDamage, Mathf.Max(1, Mathf.RoundToInt(health.maxHP.Value * 0.05f)));
                    damageAmount = criminalUsePercent
                        ? Mathf.Max(1, Mathf.RoundToInt(health.maxHP.Value * cDamagePct))
                        : safeFlat;
                }
                
                string role = isSinner ? "대죄인" : "악인";
                Debug.Log($"[ZoneTriggerVolume] Zone {zoneIndex} 잔류 {role} {netObj.OwnerClientId} → 도트 -{damageAmount} (HP: {health.currentHP.Value}/{health.maxHP.Value})");
                
                if (CombatManager.Instance != null)
                {
                    CombatManager.Instance.ApplyDamage(null, netObj, damageAmount);
                }
                else
                {
                    health.ServerApplyDamage(damageAmount);
                }

                // 봉인 구역 잔류 도트음(HolyFireBurn으로 통합)을 피해 입은 본인에게만 2D 재생
                if (pnc != null)
                {
                    var burnRpcParams = new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { netObj.OwnerClientId } }
                    };
                    pnc.Client_PlayHolyFireBurnClientRpc(burnRpcParams);
                }
            }
        }
    }
}

