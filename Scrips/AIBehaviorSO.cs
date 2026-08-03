using UnityEngine;

/// <summary>
/// [도메인 명세 §3.x] AI 개체의 행동 수치를 정의하는 ScriptableObject.
/// 양/몬스터 종류별로 에셋을 별도 생성하여 인스펙터에서 등록하세요.
/// Create > 교화제 > AI Behavior
/// </summary>
[CreateAssetMenu(fileName = "AIBehaviorSO", menuName = "교화제/AI Behavior")]
public class AIBehaviorSO : ScriptableObject
{
    [Header("기본 정보")]
    [Tooltip("이 AI 개체의 이름 (디버그용)")]
    public string agentName = "Lamb";

    [Tooltip("양이면 true, 몬스터면 false")]
    public bool isLamb = true;

    [Tooltip("몬스터 종류 번호 (1 = 종자, 2 = 습격자, 3 = 베히모스). 양이면 0으로 두세요.")]
    [Range(0, 3)]
    public int monsterIndex = 0;

    [Header("체력")]
    public int maxHP = 3;

    [Header("이동")]
    [Tooltip("일반 이동 속도 (m/s)")]
    public float moveSpeed = 2.5f;

    [Tooltip("도주 시 이동 속도 (m/s)")]
    public float fleeSpeed = 5.0f;

    [Tooltip("배회 반경 (m)")]
    public float wanderRadius = 10.0f;

    [Tooltip("배회 시 방향 전환 간격 (초)")]
    public float wanderInterval = 5.0f;

    [Header("감지")]
    [Tooltip("위협(악인) 감지 반경 (m)")]
    public float perceptionRadius = 4.0f;

    [Tooltip("도주 해제 — 위협이 사라진 후 몇 초 뒤 Idle 복귀")]
    public float fleeOutTime = 10.0f;

    [Header("전투 (몬스터 전용)")]
    [Tooltip("공격 사거리 (m)")]
    public float attackRange = 2.0f;

    [Tooltip("공격 쿨다운 (초)")]
    public float attackCooldown = 2.0f;

    [Tooltip("공격 데미지")]
    public int attackDamage = 1;

    [Tooltip("포효(광역 스턴) 간격 (초) — 베히모스 전용")]
    public float roarInterval = 60.0f;

    [Tooltip("포효 스턴 반경 (m)")]
    public float roarRadius = 10.0f;

    [Tooltip("포효 스턴 지속 (초)")]
    public float roarStunDuration = 2.0f;

    [Header("보상")]
    [Tooltip("처치 시 지급되는 영혼 수")]
    public int soulValue = 1;

    [Tooltip("처치 시 아이템 드랍 테이블 (미구현 — 추후 연결)")]
    public string dropTableId = "";

    [Header("Despawn")]
    [Tooltip("사망 후 Despawn까지 대기 시간 (초)")]
    public float despawnDelay = 0f;
}
