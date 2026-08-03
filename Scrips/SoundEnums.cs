using UnityEngine;

/// <summary>
/// 상황별 효과음 종류
/// </summary>
/// <remarks>
/// [중요] 각 멤버의 정수값은 SoundConfigSO.asset(및 SFXType 직렬화 필드)에 그대로 저장됩니다.
/// 따라서 모든 멤버에 명시적 값을 부여해 번호를 고정합니다.
/// 항목을 제거할 때는 다른 멤버의 값을 절대 다시 매기지 말고(번호 보존), 새 항목은 빈 번호 끝(99~)에 추가하세요.
/// </remarks>
public enum SFXType
{
    None = 0,
    // ── 사냥/전투 ──
    LambKill = 1,              // 양 사냥 성공
    InnocentLambKill = 2,      // 무구한 양 처치
    MonsterKill = 3,           // 몬스터 처치
    BehemothKill = 4,          // 베히모스 처치
    MeleeHit_Flesh = 5,        // 근접 공격 적중 (월드 3D)
    MeleeHit_Miss = 6,         // 근접 공격 빗나감 (월드 3D)
    HitFeedback_UI = 7,        // 피드백 사운드 (로컬 플레이어 전용 2D)

    // ── 플레이어 상태 (공통/악인) ──
    PlayerDamaged = 8,         // 피격 (공통)
    PlayerDamaged_Male = 9,    // 피격 (남성)
    PlayerDamaged_Female = 10, // 피격 (여성)
    // 11(구 PlayerLowHP): 제거 → 단계별 PlayerLowHP_50/_25 로 분리 (하단)
    // 12(구 PlayerDeath 공통): 제거 → PlayerDeath_Male/_Female 사용
    PlayerRevive = 13,         // 부활
    SinLevelUp = 14,           // 죄악 레벨업
    PlayerFootstep = 15,       // 악인 발소리 챱챱챱챱 (공용)
    PlayerSprintFootstep = 16, // 악인 스프린트 소리 (공용)
    PlayerAttackSwing = 17,    // 악인 공격 스윙 소리
    // 18(구 PlayerJump 공통): 제거 → PlayerJump_Male/_Female (하단)
    // 19(구 PlayerPanting 공통): 제거 → PlayerPanting_Male/_Female (하단)

    // ── 대죄인 ──
    GreatSinnerRoar = 20,        // 대죄인(베히모스) 포효
    GreatSinnerProximity = 21,   // 대죄인 근접 경고
    GreatSinnerFootstep = 22,    // 대죄인 발소리
    GreatSinnerAttackSwing = 23, // 대죄인 공격 소리
    GreatSinnerIdle = 24,        // 대죄인 Idle 소리
    GreatSinnerSkill = 25,       // 대죄인 스킬 소리
    GreatSinnerHuntSuccess = 26, // 대죄인 사냥 성공 소리
    GreatSinnerDeath = 27,       // 대죄인 사망 소리
    GreatSinnerDamaged = 28,     // 대죄인 피격 소리
    GreatSinnerTransform = 29,   // 대죄인화 소리 (이벤트 호출 X, 직접 설정)

    // ── 양 / 몬스터 ──
    LambIdle = 30,             // 양 일반 소리
    LambAttack = 31,           // 양 공격 소리
    LambDamaged = 32,          // 양 피격 소리
    InnocentLambSparkle = 33,  // 무구한 양 샤방샤방 소리
    Monster1_Idle = 34,        // 몬스터1 Idle 소리
    Monster1_Attack = 35,      // 몬스터1 공격 소리
    Monster1_Damaged = 36,     // 몬스터1 피격 소리
    Monster1_Death = 37,       // 몬스터1 사망 소리
    Monster2_Idle = 38,        // 몬스터2 Idle 소리
    Monster2_Attack = 39,      // 몬스터2 공격 소리
    Monster2_Damaged = 40,     // 몬스터2 피격 소리
    Monster2_Death = 41,       // 몬스터2 사망 소리
    Monster3_Idle = 42,        // 몬스터3 Idle 소리
    Monster3_Attack = 43,      // 몬스터3 공격 소리
    Monster3_Damaged = 44,     // 몬스터3 피격 소리
    Monster3_Death = 45,       // 몬스터3 사망 소리

    // ── 영혼 ──
    SoulAbsorb = 46,            // 영혼 흡수 소리
    SoulGaugeIncrease = 47,     // 영혼 게이지 증가 소리
    // 48(구 SoulGuideNotification): 제거

    // ── 스킬 / 기타 ──
    SkillActivate = 49,        // 스킬 사용
    SkillCooldownReady = 50,   // 스킬 쿨타임 완료 소리 (공용)
    Whoosh = 51,               // 공용 우쉬 (후웅)

    // ── 캐릭터별 스킬 및 공격 ──
    Pere_Attack = 52,
    Evan_Attack = 53,
    Pere_Skill1 = 54,
    Pere_Skill2 = 55,
    Pere_Skill3 = 56,
    Anglo_Skill1 = 57,
    Anglo_Skill2 = 58,
    Anglo_Skill3 = 59,
    Evan_Skill1 = 60,
    Evan_Skill2 = 61,
    Evan_Skill3 = 62,
    Mari_Skill1 = 63,
    Mari_Skill2 = 64,
    Mari_Skill3 = 65,

    // ── 제단/헌납 ──
    AltarDonateStart = 66,     // 헌납 시작
    // 67(구 AltarDonateComplete): 제거
    // 68(구 AltarDonateInterrupted): 제거
    AltarBeamActivate = 69,    // 제단 빛줄기 활성화

    // ── 토템 ──
    TotemHit = 70,             // 토템 타격
    TotemDestroyed = 71,       // 토템 파괴
    // 72(구 TotemDepleted): 제거
    // 73(구 TotemHumming): 제거

    // ── 페이즈 ──
    PhaseTransition = 74,      // 페이즈 전환 (범용)
    ZoneSealWarning = 75,      // 봉인 30초 전 경고
    ZoneSealed = 76,           // 구역 봉인 완료
    // 77(구 ZoneDotDamage): 제거 → 봉인 구역 잔류 도트음은 HolyFireBurn으로 통합
    PropDestroyed = 78,        // 파괴 가능 오브젝트 파괴

    // ── 아이템 / 상자 ──
    // 79~84(구 ItemPickup/ItemPickup_Epic/ItemPickup_Legendary/ItemUse/ItemUse_Epic/ItemUse_Legendary): 제거
    //   → 아이템 획득음은 상자/몬스터 공용 팝업 연출음(ChestOpen_*)으로 통합
    ItemEmptySlotClick = 85,   // 아이템 빈공간 클릭 소리
    ChestOpen_Common = 86,     // 상자 열기 (일반급)
    ChestOpen_Legendary = 87,  // 상자 열기 (전승급)
    ChestOpen_Epic = 88,       // 상자 열기 (서사급)
    ChestOpen_Opening = 89,    // 상자 개봉 연출 소리 (등급 공통)

    // ── 성화 (P3) ──
    HolyFireBurn = 90,         // 성화 외곽 도트 데미지
    HolyFireShrink = 91,       // 성화 침식 (10초마다)

    // ── UI / 매치 ──
    UIClick = 92,              // UI 버튼 클릭
    UIHover = 93,              // UI 버튼 호버
    MatchStart = 94,           // 매치 시작
    MatchEnd = 95,             // 매치 종료
    GameEnding = 96,           // 게임 엔딩 소리

    // ─────────────────────────────────────────
    // ▼ 최근 추가된 항목 (기존 인덱스 꼬임 방지: 항상 끝에만 추가) ▼
    // ─────────────────────────────────────────
    PlayerDeath_Male = 97,     // 사망 (남성)
    PlayerDeath_Female = 98,   // 사망 (여성)

    // 성별 구분 점프/스태미나 (공통 버전 없음 — 성별 사운드를 항상 사용)
    PlayerJump_Male = 99,      // 점프 (남성)
    PlayerJump_Female = 100,   // 점프 (여성)
    PlayerPanting_Male = 101,  // 스태미나 헉헉 (남성)
    PlayerPanting_Female = 102,// 스태미나 헉헉 (여성)

    // 체력 위험 단계 (수치별)
    PlayerLowHP_50 = 103,      // 체력 50% 이하
    PlayerLowHP_25 = 104,      // 체력 25% 이하

    AnnouncerAppear = 105,     // 아나운서 멘트 등장 공통 알림음 (모든 아나운서 종류 공용, 2D)
    
    // 엔젤 스킬음
    Angel_Skill1 = 106,
    Angel_Skill3 = 107,
    Angel_Attack = 108,

    // 대죄인 전용 히트피드백(공격 적중/피격 공용, HitFeedback.wav)
    GreatSinnerHitFeedback = 109,

    // 엔젤 스킬3 착지 사운드 (MatchStart.wav 재활용, 1초 페이드아웃으로 짧게 재생)
    Angel_Skill3_Land = 110
}

/// <summary>
/// 10가지 BGM 재생 상태
/// </summary>
public enum BGMState
{
    None,
    
    // === Phase 1 ===
    P1_GreatSinner,         // P1 대죄인 (조용하고 위압적인 탐색)
    P1_Criminal_Peace,      // P1 악인 (불안한 평화)
    P1_Criminal_Spotted,    // P1 악인 발견 (대죄인이나 몬스터에게 발각됨)
    P1_Criminal_Attacked,   // P1 악인 피습 (대죄인에게 공격당함)
    
    // === Phase 2 ===
    P2_GreatSinner,         // P2 대죄인 (본격적인 사냥)
    P2_GreatSinner_Final,   // P2 대죄인 최종 (Zone D 봉인 임박 또는 베히모스 전투 등 템포 업)
    P2_Criminal_Peace,      // P2 악인 평화 (잠입, 파밍)
    P2_Criminal_Combat,     // P2 악인 전투 (직접적인 타격 발생)
    P2_Criminal_Final,      // P2 악인 최종 (Zone D 봉인 임박, 극도의 긴장감)
    
    // === Phase 3 ===
    P3_BattleRoyale         // P3 배틀로얄 (성화 침식, 최후의 1인)
}
