using UnityEngine;

[CreateAssetMenu(fileName = "HolyFireConfigSO", menuName = "교화제/HolyFireConfigSO")]
public class HolyFireConfigSO : ScriptableObject
{
    [Header("Phase 2 (Zone Seal)")]
    [Tooltip("2페이즈: 존 바닥에 깔리는 불 장판의 최대 높이 (기획 요구사항: 0.7m 이하)")]
    public float p2FireHeightMax = 0.7f;
    [Tooltip("2페이즈: 존 바닥에 생성될 랜덤 넓은 장판의 개수")]
    public int p2FirePatchCount = 6;

    [Header("Phase 3 (Ring)")]
    [Tooltip("3페이즈: 시작 시 링 반경")]
    public float p3InitialRadius = 50f;
    [Tooltip("3페이즈: 10초마다 침식되어 줄어드는 반경")]
    public float ErosionAmountPer10s = 5f;
    [Tooltip("3페이즈: 성화 데미지 쿨타임(초)")]
    public float p3DamageInterval = 1.0f;
    [Tooltip("3페이즈: 성화 데미지 량")]
    public int p3DamageAmount = 10;
}
