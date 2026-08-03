using System;
using UnityEngine;
using Unity.Netcode;

public static class EventBus {

    // 도메인 릴로드 시 이전 세션의 델리게이트를 자동 청소 (GC 핸들 경고 방지)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void AutoClear() => Clear();

    // InputManager — 각 클라이언트가 자기 입력 시 발행
    public static event Action<PingType, Vector3> OnLocalPingPlaced;
    public static event Action<int> OnLocalSkillRequested;       // slot 0/1/2
    public static event Action<int> OnLocalInventoryUsed;        // slot 0/1
    public static event Action<int> OnLocalInventoryDropped;     // slot 0/1
    public static event Action OnLocalAttackPressed;
    public static event Action OnLocalJumpPressed;
    public static event Action OnLocalRollPressed;
    public static event Action OnLocalInteractPressed;
    public static event Action OnLocalInteractReleased;
    public static event Action<ChestController> OnLocalChestItemPopupRequested; // 유저 커스텀 UI 연동용
    public static event Action<ItemId, Action, Action> OnLocalGenericItemPopupRequested; // 몬스터 드랍 등 범용 아이템 팝업 (ItemId, onAccept, onDecline)
    public static event Action<NetworkObjectReference> OnLocalInteractStarted;
    public static event Action OnLocalChangeCharacterRequested; // 디버그용 캐릭터 변경
    public static event Action<int> OnLocalDebugTeleportRequested; // 디버그용 구역 텔레포트

    // Combat Events
    public static event Action<NetworkObject, NetworkObject, int> OnDamageDealt;

    // === PhaseManager (다원) ===
    public static event Action<Phase, Phase> OnPhaseTransition;        // (from, to)
    public static event Action<int> OnZoneSeal;                        // zoneIndex 0~3
    public static event Action<float> OnZoneTimeUpdate;                // 남은 초
    public static event Action OnZoneTimeReduced;                      // 기믹(석상 파괴)으로 시간이 급감했을 때 (경고음/UI 플래시용)
    public static event Action OnPhase1to2Threshold;                   // 영혼 65 도달 (영웅 SinManager 발행)
    public static event Action OnGreatSinnerDefeated;                  // 대죄인 사망 (영웅 MatchManager 발행)
    public static event Action<int> OnMatchEnd;                        // 0: 대죄인 승리, 1: 악인 승리 (MatchResult 열거형 대신 int 사용)
    public static event Action<int, MatchStatEntry[]> OnMatchEndWithStats; // 결과와 플레이어별 통계 데이터 함께 전송

    // === AI 이벤트 ===
    public static event Action<NetworkObject, NetworkObject> OnLambKilled;     // (lamb, killer)
    public static event Action<NetworkObject, NetworkObject> OnMonsterKilled;  // (monster, killer)
    public static event Action<Vector3> OnInnocentLambSpotted;                 // (position)

    // === AltarManager ===
    public static event Action<PlayerSession, AltarController> OnAltarDonateStart;
    public static event Action<PlayerSession, AltarController> OnAltarDonateComplete;
    public static event Action<PlayerSession, AltarController> OnAltarDonateInterrupted;

    // 편의를 위해 내부적으로 호출할 수 있는 헬퍼 메서드 제공
    public static void RaiseLocalPingPlaced(PingType type, Vector3 pos) => OnLocalPingPlaced?.Invoke(type, pos);
    public static void RaiseLocalSkillRequested(int slot) => OnLocalSkillRequested?.Invoke(slot);
    public static void RaiseLocalInventoryUsed(int slot) => OnLocalInventoryUsed?.Invoke(slot);
    public static void RaiseLocalInventoryDropped(int slot) => OnLocalInventoryDropped?.Invoke(slot);
    public static void RaiseLocalAttackPressed() => OnLocalAttackPressed?.Invoke();
    public static void RaiseLocalJumpPressed() => OnLocalJumpPressed?.Invoke();
    public static void RaiseLocalRollPressed() => OnLocalRollPressed?.Invoke();
    public static void RaiseLocalInteractPressed() => OnLocalInteractPressed?.Invoke();
    public static void RaiseLocalInteractReleased() => OnLocalInteractReleased?.Invoke();
    public static void RaiseLocalChestItemPopupRequested(ChestController chest) => OnLocalChestItemPopupRequested?.Invoke(chest);
    public static void RaiseLocalGenericItemPopupRequested(ItemId itemId, Action onAccept, Action onDecline) => OnLocalGenericItemPopupRequested?.Invoke(itemId, onAccept, onDecline);
    public static void RaiseLocalInteractStarted(NetworkObjectReference target) => OnLocalInteractStarted?.Invoke(target);
    public static void RaiseLocalChangeCharacterRequested() => OnLocalChangeCharacterRequested?.Invoke();
    public static void RaiseLocalDebugTeleportRequested(int zoneIndex) => OnLocalDebugTeleportRequested?.Invoke(zoneIndex);
    public static void RaiseDamageDealt(NetworkObject attacker, NetworkObject target, int rawDamage) => OnDamageDealt?.Invoke(attacker, target, rawDamage);

    // PhaseManager 이벤트 헬퍼
    public static void RaisePhaseTransition(Phase from, Phase to) => OnPhaseTransition?.Invoke(from, to);
    public static void RaiseZoneSeal(int zoneIndex) => OnZoneSeal?.Invoke(zoneIndex);
    public static void RaiseZoneTimeUpdate(float remaining) => OnZoneTimeUpdate?.Invoke(remaining);
    public static void RaiseZoneTimeReduced() => OnZoneTimeReduced?.Invoke();
    public static void RaisePhase1to2Threshold() => OnPhase1to2Threshold?.Invoke();
    public static void RaiseGreatSinnerDefeated() => OnGreatSinnerDefeated?.Invoke();
    public static void RaiseLambKilled(NetworkObject lamb, NetworkObject killer) => OnLambKilled?.Invoke(lamb, killer);
    public static void RaiseMatchEnd(int result) => OnMatchEnd?.Invoke(result);
    public static void RaiseMatchEndWithStats(int result, MatchStatEntry[] stats) => OnMatchEndWithStats?.Invoke(result, stats);
    public static void RaiseMonsterKilled(NetworkObject monster, NetworkObject killer) => OnMonsterKilled?.Invoke(monster, killer);
    public static void RaiseInnocentLambSpotted(Vector3 position) => OnInnocentLambSpotted?.Invoke(position);

    // AltarManager 이벤트 헬퍼
    public static void RaiseAltarDonateStart(PlayerSession player, AltarController altar) => OnAltarDonateStart?.Invoke(player, altar);
    public static void RaiseAltarDonateComplete(PlayerSession player, AltarController altar) => OnAltarDonateComplete?.Invoke(player, altar);
    public static void RaiseAltarDonateInterrupted(PlayerSession player, AltarController altar) => OnAltarDonateInterrupted?.Invoke(player, altar);

    /// <summary>
    /// 플레이 모드 종료나 도메인 릴로드 시점에 등록된 정적 이벤트를 일괄 청소하여
    /// Release of invalid GC handle 오류 및 메모리 누수를 완전히 방지합니다.
    /// </summary>
    public static void Clear()
    {
        OnLocalPingPlaced = null;
        OnLocalSkillRequested = null;
        OnLocalInventoryUsed = null;
        OnLocalInventoryDropped = null;
        OnLocalAttackPressed = null;
        OnLocalJumpPressed = null;
        OnLocalRollPressed = null;
        OnLocalInteractPressed = null;
        OnLocalInteractReleased = null;
        OnLocalChestItemPopupRequested = null;
        OnLocalGenericItemPopupRequested = null;
        OnLocalInteractStarted = null;
        OnLocalChangeCharacterRequested = null;
        OnLocalDebugTeleportRequested = null;
        OnDamageDealt = null;
        OnPhaseTransition = null;
        OnZoneSeal = null;
        OnZoneTimeUpdate = null;
        OnZoneTimeReduced = null;
        OnPhase1to2Threshold = null;
        OnGreatSinnerDefeated = null;
        OnLambKilled = null;
        OnMonsterKilled = null;
        OnMatchEnd = null;
        OnMatchEndWithStats = null;
        OnAltarDonateStart = null;
        OnAltarDonateComplete = null;
        OnAltarDonateInterrupted = null;
        OnInnocentLambSpotted = null;
    }
}

public enum PingType { EnemyHere, HelpMe, RallyHere, Retreat, AltarActive }

/// <summary>
/// [도메인 명세 §2.1] 게임 페이즈 열거형.
/// PhaseManager가 단일 소유자이며, 다른 매니저는 읽기만 합니다.
/// </summary>
public enum Phase { PreMatch, Phase1, Phase2, Phase3, PostMatch }
