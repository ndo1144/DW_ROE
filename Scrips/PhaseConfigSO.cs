using UnityEngine;

/// <summary>
/// [도메인 명세 §4.2] 페이즈 시간·구역 봉인·토템 효과 등 페이즈 관련 수치를 담는 SO 에셋입니다.
/// 인스펙터에서 'Create > 교화제 > Phase Config'로 생성하십시오.
/// </summary>
[CreateAssetMenu(fileName = "PhaseConfigSO_New", menuName = "교화제/Phase Config")]
public class PhaseConfigSO : ScriptableObject
{
    [Header("페이즈 시간 (콘텐츠기획서 5.1)")]
    [Tooltip("Phase 1 지속 시간(초). 기본 6분.")]
    public float Phase1Duration = 360f;

    [Tooltip("Phase 3 지속 시간(초). 기본 3분.")]
    public float Phase3Duration = 180f;

    [Header("P2 구역 시간 (콘텐츠기획서 5.3.1)")]
    [Tooltip("Zone A 봉인 제한 시간(초).")]
    public float ZoneATime = 120f;

    [Tooltip("Zone B 봉인 제한 시간(초).")]
    public float ZoneBTime = 180f;

    [Tooltip("Zone C 봉인 제한 시간(초).")]
    public float ZoneCTime = 180f;

    [Tooltip("Zone D 봉인 제한 시간(초).")]
    public float ZoneDTime = 240f;

    [Header("토템 효과")]
    [Tooltip("토템 파괴 시 해당 구역 시간 감소량(초).")]
    public float TotemDestroyedTimeReduction = 30f;

    [Tooltip("토템 자연 소멸 시 해당 구역 시간 보너스(초).")]
    public float TotemDepletedTimeBonus = 30f;

    [Tooltip("Zone D 토템 파괴 시 P3 성화 침식 가속 비율.")]
    public float ZoneDTotemDestroyedErosionBonus = 0.25f;

    [Tooltip("Zone D 토템 자연 소멸 시 P3 성화 침식 감속 비율.")]
    public float ZoneDTotemDepletedErosionPenalty = 0.25f;

    [Header("구역 봉인 도트")]
    [Tooltip("봉인된 구역 잔류 시 초당 데미지(최대 체력 비율). 0.5 = 50%/초.")]
    public float ZoneSealDotDamagePerSecond = 0.5f;

    [Tooltip("봉인 전 경고 시간(초). 이 시간 전부터 경고 효과 표시.")]
    public float ZoneSealWarningTime = 30f;

    /// <summary>
    /// Zone 인덱스(0~3)에 해당하는 봉인 제한 시간을 반환합니다.
    /// </summary>
    public float GetZoneTime(int zoneIndex)
    {
        return zoneIndex switch
        {
            0 => ZoneATime,
            1 => ZoneBTime,
            2 => ZoneCTime,
            3 => ZoneDTime,
            _ => 120f,
        };
    }
}
