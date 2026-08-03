using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 상자 스폰 상한(8), 재생성 쿨(30초), 등급별 루트. SpawnManager와 연동합니다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ChestManager : NetworkBehaviour
{
    public static ChestManager Instance { get; private set; }

    [Header("스폰")]
    public int maxChests = 8;
    public float respawnCooldown = 30f;
    public ItemDropTableSO dropTable;

    private readonly List<ChestController> _activeChests = new List<ChestController>();
    private Coroutine _respawnCoroutine;
    private bool _respawnPending;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        EventBus.OnPhaseTransition += HandlePhaseTransition;
        EventBus.OnMonsterKilled += HandleMonsterKilled;

        // [NGO 네트워크 가드] 페이즈 1에서는 상자가 나오지 않으며, 오직 페이즈 2일 때만 상자를 즉시 보충 스폰합니다!
        if (IsServer && PhaseManager.Instance != null && 
            PhaseManager.Instance.IsCurrentPhase(Phase.Phase2))
        {
            FillChestsToMax();
            Debug.Log("[ChestManager] OnNetworkSpawn: 페이즈 2 상태 감지에 따른 상자 즉각 스폰 완료.");
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        EventBus.OnPhaseTransition -= HandlePhaseTransition;
        EventBus.OnMonsterKilled -= HandleMonsterKilled;
    }

    private void HandlePhaseTransition(Phase oldPhase, Phase newPhase)
    {
        if (!IsServer) return;

        // 상자는 오직 페이즈 2로 전환될 때만 보충 및 스폰됩니다.
        if (newPhase == Phase.Phase2)
            FillChestsToMax();
    }

    public void RegisterChest(ChestController chest)
    {
        if (chest == null || _activeChests.Contains(chest)) return;
        _activeChests.Add(chest);
    }

    public void UnregisterChest(ChestController chest)
    {
        _activeChests.Remove(chest);
    }

    public IReadOnlyList<ChestController> ActiveChests => _activeChests;

    public int ActiveChestCount
    {
        get
        {
            _activeChests.RemoveAll(c => c == null || !c.IsSpawned);
            return _activeChests.Count;
        }
    }

    private void FillChestsToMax()
    {
        if (!IsServer || SpawnManager.Instance == null) return;

        _activeChests.RemoveAll(c => c == null || !c.IsSpawned);

        int toSpawn = maxChests - _activeChests.Count;
        for (int i = 0; i < toSpawn; i++)
        {
            var chest = SpawnManager.Instance.SpawnChestAtRandomPosition();
            if (chest != null)
                RegisterChest(chest);
        }

        Debug.Log($"[ChestManager] 상자 {ActiveChestCount}/{maxChests}개 유지");
    }

    public void FinishOpening(ChestController chest)
    {
        if (!IsServer || chest == null) return;

        string tierLabel = "빈 상자";
        ItemId rolled = ItemId.None;
        
        if (dropTable != null)
        {
            rolled = dropTable.RollItem(out tierLabel);
        }
        else
        {
            rolled = RollItemByProbability(out tierLabel);
        }

        chest.SetStoredLoot(rolled);
        Debug.Log($"[ChestManager] 상자 개봉 결과 — 등급: {tierLabel} | 획득 아이템: {rolled}");

        // 등급별 연출음은 아이템 UI 팝업이 뜨는 순간(ChestItemPopupUI.ShowItemPopup)에 재생하도록 이전함.
        // (개봉 완료 시점이 아니라 플레이어가 실제로 아이템 정보를 확인하는 순간에 들려주기 위함)

        if (rolled == ItemId.None)
            StartCoroutine(RemoveEmptyChestAfterDelay(chest, 2f));
    }

    /// <summary>등급 라벨 문자열을 ChestTier로 매핑합니다. (드랍 테이블/확률 롤 양쪽 라벨 공통)</summary>
    private ChestController.ChestTier TierFromLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return ChestController.ChestTier.None;
        if (label.Contains("서사")) return ChestController.ChestTier.Epic;
        if (label.Contains("전승")) return ChestController.ChestTier.Legendary;
        if (label.Contains("일반")) return ChestController.ChestTier.Common;
        return ChestController.ChestTier.None; // 빈 상자
    }

    /// <summary>
    /// 기획서에 의거하여 빈 상자 30% / 일반급 59% / 전승급 10% / 서사급 1% 확률로 아이템 추첨
    /// </summary>
    private ItemId RollItemByProbability(out string tierLabel)
    {
        float r = Random.value; // 0.0f ~ 1.0f

        // 1. 빈 상자 : 30% (0.0f ~ 0.30f)
        if (r < 0.30f)
        {
            tierLabel = "빈 상자 (30%)";
            return ItemId.None;
        }
        // 2. 일반급 : 59% (0.30f ~ 0.89f)
        else if (r < 0.89f)
        {
            tierLabel = "일반급 (59%)";
            ItemId[] commonPool = new ItemId[]
            {
                ItemId.CursedRosary,
                ItemId.ProfaneBlessing,
                ItemId.VenialSin,
                ItemId.PrisonersDilemma,
                ItemId.ParchedPhial,
                ItemId.HallowedParasite
            };
            return commonPool[Random.Range(0, commonPool.Length)];
        }
        // 3. 전승급 : 10% (0.89f ~ 0.99f)
        else if (r < 0.99f)
        {
            tierLabel = "전승급 (10%)";
            ItemId[] legendaryPool = new ItemId[]
            {
                ItemId.GuidingHorn,
                ItemId.IllusoryTotem,
                ItemId.FracturedBarrier,
                ItemId.CovenantOfSanctuary,
                ItemId.TailOfGuiltless
            };
            return legendaryPool[Random.Range(0, legendaryPool.Length)];
        }
        // 4. 서사급 : 1% (0.99f ~ 1.00f)
        else
        {
            tierLabel = "서사급 (1%)";
            ItemId[] mythicPool = new ItemId[]
            {
                ItemId.ProfaneTrophy,
                ItemId.VestigeOfSaint,
                ItemId.HallowedOne,
                ItemId.Satanism
            };
            return mythicPool[Random.Range(0, mythicPool.Length)];
        }
    }

    private IEnumerator RemoveEmptyChestAfterDelay(ChestController chest, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (chest != null && chest.IsSpawned && chest.storedItem.Value == ItemId.None)
            ScheduleRespawnAfterRemoval(chest);
    }

    /// <summary>아이템 획득으로 상자가 비었을 때 — 제거 후 쿨타임 뒤 재스폰</summary>
    public void OnChestEmptied(ChestController chest)
    {
        if (!IsServer || chest == null) return;
        ScheduleRespawnAfterRemoval(chest);
    }

    private void ScheduleRespawnAfterRemoval(ChestController chest)
    {
        if (!IsServer) return;
        StartCoroutine(RemoveChestAndQueueRespawn(chest));
    }

    private IEnumerator RemoveChestAndQueueRespawn(ChestController chest)
    {
        if (chest != null && chest.IsSpawned)
        {
            UnregisterChest(chest);
            chest.NetworkObject.Despawn(true);
        }

        if (!_respawnPending)
        {
            _respawnPending = true;
            if (_respawnCoroutine != null) StopCoroutine(_respawnCoroutine);
            _respawnCoroutine = StartCoroutine(RespawnCooldownRoutine());
        }

        yield return null;
    }

    private IEnumerator RespawnCooldownRoutine()
    {
        yield return new WaitForSeconds(respawnCooldown);
        _respawnPending = false;
        _respawnCoroutine = null;

        if (!IsServer) yield break;
        if (PhaseManager.Instance != null &&
            !PhaseManager.Instance.IsCurrentPhase(Phase.Phase1) &&
            !PhaseManager.Instance.IsCurrentPhase(Phase.Phase2))
            yield break;

        FillChestsToMax();
        Debug.Log($"[ChestManager] 재생성 쿨({respawnCooldown}초) 종료 — 상자 보충");
    }

    // ─────────────────────────────────────────────────────────
    // 몬스터 처치 이벤트 처리 (베히모스 고정 드랍)
    // ─────────────────────────────────────────────────────────
    private void HandleMonsterKilled(NetworkObject monster, NetworkObject killer)
    {
        if (!IsServer || monster == null) return;

        // 베히모스 여부 확인 (기본적으로 이름에 Behemoth가 포함된 것으로 필터링)
        if (monster.gameObject.name.Contains("Behemoth"))
        {
            Vector3 dropPos = monster.transform.position;
            
            // 1. 성 베드로의 눈물 - 100% 고정 드랍
            SpawnManager.Instance.SpawnDroppedItem(dropPos, ItemId.StPetersTear);
            
            // 2. 부정한 수급 - 30% 확률 드랍
            if (Random.Range(0, 100) < 30)
                SpawnManager.Instance.SpawnDroppedItem(dropPos + new Vector3(1, 0, 0), ItemId.ProfaneTrophy);
                
            // 3. 성자의 유해 - 20% 확률 드랍
            if (Random.Range(0, 100) < 20)
                SpawnManager.Instance.SpawnDroppedItem(dropPos + new Vector3(0, 0, 1), ItemId.VestigeOfSaint);
                
            // 4. 전승급 1종 - 50% 확률 드랍
            if (Random.Range(0, 100) < 50)
            {
                ItemId[] legendaryPool = new ItemId[]
                {
                    ItemId.GuidingHorn,
                    ItemId.IllusoryTotem,
                    ItemId.FracturedBarrier,
                    ItemId.CovenantOfSanctuary,
                    ItemId.TailOfGuiltless
                };
                
                ItemId randomLegendary = legendaryPool[Random.Range(0, legendaryPool.Length)];
                SpawnManager.Instance.SpawnDroppedItem(dropPos + new Vector3(-1, 0, 0), randomLegendary);
            }
            
            Debug.Log($"[ChestManager] 베히모스({monster.NetworkObjectId}) 처치 감지! 고정 드랍 롤 수행 완료.");
        }
    }
}
