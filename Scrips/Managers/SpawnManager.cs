using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// [도메인 명세 §2.2] 런타임 동적 스폰의 단일 진입점.
/// 서버(Host) 독점 권한으로 동작하며, 씬의 SpawnPointMarker를 수집·관리합니다.
/// </summary>
public class SpawnManager : NetworkBehaviour
{
    public static SpawnManager Instance { get; private set; }

    [Header("맵 설정")]
    [Tooltip("현재 맵에 해당하는 MapSO 에셋을 인스펙터에서 등록하세요.")]
    public MapSO currentMap;

    [Header("플레이어 프리팹")]
    [Tooltip("네트워크로 스폰할 단일 플레이어 캐릭터 프리팹 (PlayerNetworkController 포함)")]
    public GameObject playerPrefab;

    // Zone 인덱스 → SpawnType → 해당 마커 목록
    private Dictionary<int, Dictionary<SpawnPointMarker.SpawnType, List<SpawnPointMarker>>> _pointsByZoneAndType;

    // Zone 인덱스 → ZoneBounds (영역 정의)
    private Dictionary<int, ZoneBounds> _zoneBounds;

    // 이미 사용된 스폰 포인트 (같은 위치 중복 스폰 방지)
    private HashSet<SpawnPointMarker> _usedPoints = new HashSet<SpawnPointMarker>();

    // 스폰된 오브젝트 추적
    private List<NetworkObject> _spawnedAltars = new List<NetworkObject>();
    private List<NetworkObject> _spawnedTotems = new List<NetworkObject>();
    private List<NetworkObject> _spawnedMonsters = new List<NetworkObject>();
    private List<NetworkObject> _spawnedChests = new List<NetworkObject>();

    // Zone별 어린양 추적
    private Dictionary<int, List<NetworkObject>> _lambsByZone = new Dictionary<int, List<NetworkObject>>();

    // 구역별 리스폰 대기 중인 몬스터 수
    private Dictionary<int, int> _respawningMonstersByZone = new Dictionary<int, int>();

    // 누적 스폰 카운트 (게임 전체에서 몇 마리가 스폰되었는지)
    private int _spawnedNormalCount = 0;
    private int _spawnedInnocentCount = 0;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        Initialize();
    }

    private void Start()
    {
        // OnNetworkSpawn에서 처리합니다.
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        Debug.Log("[SpawnManager] OnNetworkSpawn — 서버 확인. Phase 1 시작.");

        // 구역 봉인 이벤트 구독
        EventBus.OnZoneSeal += HandleZoneSeal;

        // [네트워크 스폰 분리] 게임 씬 시작 시 로비 정보를 바탕으로 플레이어 수동 스폰
        SpawnAllPlayersFromLobby();

        // PhaseManager가 있으면 TransitionTo로 위임 (타이머 포함)
        // PhaseManager가 없으면 SpawnManager가 직접 Phase 1 스폰 (대안)
        if (PhaseManager.Instance != null)
            PhaseManager.Instance.TransitionTo(Phase.Phase1);
        else
            StartPhase1();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            EventBus.OnZoneSeal -= HandleZoneSeal;
        }
        base.OnNetworkDespawn();
    }

    /// <summary>
    /// 로비 씬에서 넘어온 클라이언트들을 위해 플레이어를 수동으로 스폰합니다.
    /// NetworkManager의 기본 PlayerPrefab 스폰 기능 대신 이 메서드가 역할을 담당합니다.
    /// </summary>
    private void SpawnAllPlayersFromLobby()
    {
        if (!IsServer) return;

        var connectedClients = NetworkManager.Singleton.ConnectedClientsIds;
        if (connectedClients.Count == 0) return;

        // 호스트(ClientId 0)를 대죄인으로 고정 (로딩 화면 동기화를 위함)
        ulong greatSinnerId = 0;

        // 악인들에게 로딩 화면을 먼저 띄웁니다. 대죄인은 임시 대기 상태로 설정합니다.
        ShowCriminalsLoadingClientRpc(greatSinnerId);

        // 로비 선택 캐릭터 정보를 가져오는 로컬 함수
        CharacterId GetSelectedChar(ulong cid)
        {
            CharacterId charId = CharacterId.Pereshte;
            if (LobbyStateSync.SavedCharacterSelections != null && LobbyStateSync.SavedCharacterSelections.TryGetValue(cid, out CharacterId savedChar))
            {
                charId = savedChar;
            }
            else if (LobbyStateSync.Instance != null)
            {
                foreach (var p in LobbyStateSync.Instance.LobbyPlayers)
                {
                    if (p.ClientId == cid)
                    {
                        charId = p.SelectedCharacter;
                        break;
                    }
                }
            }
            return charId;
        }

        List<NetworkObject> spawnedCriminals = new List<NetworkObject>();

        // 로비에 등록되었던 모든 클라이언트(및 봇)의 ID를 수집합니다.
        List<ulong> allPlayerIds = new List<ulong>();
        foreach (var cid in connectedClients)
        {
            allPlayerIds.Add(cid);
        }
        
        if (LobbyStateSync.SavedCharacterSelections != null)
        {
            foreach (var kvp in LobbyStateSync.SavedCharacterSelections)
            {
                if (!allPlayerIds.Contains(kvp.Key))
                {
                    allPlayerIds.Add(kvp.Key);
                }
            }
        }

        // 1. 악인(Criminal) 플레이어 먼저 스폰
        foreach (ulong clientId in allPlayerIds)
        {
            if (clientId == greatSinnerId) continue;

            CharacterId selectedChar = GetSelectedChar(clientId);
            int randomZone = Random.Range(0, 3);
            
            var criminalObj = SpawnPlayerAtRandomPoint(clientId, randomZone, false, selectedChar);
            if (criminalObj != null)
            {
                spawnedCriminals.Add(criminalObj);
                Debug.Log($"[SpawnManager] Client {clientId} 악인 플레이어 스폰 완료 (캐릭터: {selectedChar})");
            }
        }

        // 2. 대죄인(Great Sinner) 스폰: 악인들과 가장 멀리 떨어진 스폰 포인트를 찾아 배치
        CharacterId gsChar = GetSelectedChar(greatSinnerId);
        var allGSMarkers = GetAllPoints(SpawnPointMarker.SpawnType.PlayerGreatSinner);
        SpawnPointMarker bestMarker = null;
        float maxMinDist = -1f;

        if (allGSMarkers != null && allGSMarkers.Count > 0)
        {
            foreach (var marker in allGSMarkers)
            {
                float minDistToCriminal = float.MaxValue;
                foreach (var criminal in spawnedCriminals)
                {
                    if (criminal == null) continue;
                    float dist = Vector3.Distance(marker.transform.position, criminal.transform.position);
                    if (dist < minDistToCriminal)
                    {
                        minDistToCriminal = dist;
                    }
                }

                if (minDistToCriminal > maxMinDist)
                {
                    maxMinDist = minDistToCriminal;
                    bestMarker = marker;
                }
            }
        }

        if (bestMarker != null)
        {
            // 가장 안전한(멀리 떨어진) 위치에서 대죄인 스폰
            var gsObj = SpawnPlayerObject(playerPrefab, bestMarker.transform.position, bestMarker.transform.rotation, greatSinnerId);
            if (gsObj != null && gsObj.TryGetComponent<PlayerNetworkController>(out var playerCtrl))
            {
                playerCtrl.currentCharacterId.Value = gsChar;
                Debug.Log($"[SpawnManager] 대죄인 스폰 완료 (캐릭터: {gsChar}, 악인과의 최소 거리: {maxMinDist:F1}m)");
            }
        }
        else
        {
            // 마커를 찾지 못했거나 에러 발생 시 기존 방식대로 랜덤 스폰
            SpawnPlayerAtRandomPoint(greatSinnerId, Random.Range(0, 3), true, gsChar);
        }
        
        // 악인들이 완전히 스폰되고 움직일 수 있는 시점(로딩 1초 + 0.5초 여유) 이후에
        // 대죄인의 실질적인 5.5초 로딩 카운트다운을 시작합니다.
        StartCoroutine(GreatSinnerSpawnSequence(greatSinnerId));
    }

    [Unity.Netcode.ClientRpc]
    private void ShowCriminalsLoadingClientRpc(ulong greatSinnerId)
    {
        bool isMeGreatSinner = (Unity.Netcode.NetworkManager.Singleton.LocalClientId == greatSinnerId);
        
        if (GameStartLoadingUI.Instance != null)
        {
            if (isMeGreatSinner)
            {
                // 대죄인은 악인들이 도망치는 것을 기다리는 무한 대기 상태 (후에 갱신됨)
                GameStartLoadingUI.Instance.ShowLoading(999f, "악인들이 도망칠 준비를 하고 있습니다...");
            }
            else
            {
                // 악인은 1초 짧은 로딩 후 즉시 시작
                GameStartLoadingUI.Instance.ShowLoading(1f, "도망치십시오! 대죄인이 곧 강림합니다.");
            }
        }
    }

    private IEnumerator GreatSinnerSpawnSequence(ulong greatSinnerId)
    {
        // 악인 로딩 시간(1.0초) + 네트워크 딜레이(0.5초)를 주어 악인이 확실히 화면을 보고 이동할 수 있도록 대기
        yield return new WaitForSeconds(1.5f);

        // 이제부터 실질적으로 대죄인의 5.5초 로딩 카운트 시작
        StartGreatSinnerLoadingClientRpc(greatSinnerId);
    }

    [Unity.Netcode.ClientRpc]
    private void StartGreatSinnerLoadingClientRpc(ulong greatSinnerId)
    {
        bool isMeGreatSinner = (Unity.Netcode.NetworkManager.Singleton.LocalClientId == greatSinnerId);
        
        if (GameStartLoadingUI.Instance != null && isMeGreatSinner)
        {
            GameStartLoadingUI.Instance.ShowLoading(5.5f, "대죄인 강림 준비 중...");
        }
    }

    /// <summary>
    /// 구역이 봉인될 때 호출되어 해당 구역에 머물러 있는 모든 어린양, 몬스터, 상자를 30초 유예 후 강제로 Despawn 소멸시킵니다.
    /// </summary>
    // 2페이즈 구역별 성화 목록 (구역 봉인 시 생성되는 성화들)
    private Dictionary<int, NetworkObject> _zoneSealFireRings = new Dictionary<int, NetworkObject>();

    private void HandleZoneSeal(int zoneIndex)
    {
        if (!IsServer) return;

        Debug.Log($"[SpawnManager] Zone {zoneIndex} 봉인 이벤트 수신. 30초의 유예 시간을 거친 후 잔류 개체(어린양, 몬스터, 상자)를 소멸시킵니다.");
        StartCoroutine(DelayedZoneSealDespawn(zoneIndex, 30f));

        // 해당 구역 중앙에 성화 스폰 (2페이즈 구역 봉인 연출)
        SpawnZoneSealFire(zoneIndex);

        // 🐉 베히모스 스폰 조건: 봉인된 존이 2개가 되는 시점에 스폰!
        // SpawnZoneSealFire 호출 후 _zoneSealFireRings에 추가되므로 count == 2일 때 확인합니다.
        if (_zoneSealFireRings.Count == 2)
        {
            SpawnBehemothOnTwoZonesSealed();
        }
    }

    /// <summary>
    /// 제한구역이 2개 남았을 때 베히모스를 스폰합니다.
    /// </summary>
    private void SpawnBehemothOnTwoZonesSealed()
    {
        if (currentMap == null || currentMap.monsterPrefabs == null || currentMap.monsterPrefabs.Length < 3)
        {
            Debug.LogWarning("[SpawnManager] 베히모스 스폰 실패: monsterPrefabs[2]가 없습니다.");
            return;
        }

        // 이미 베히모스가 살아있는지 확인 (중복 스폰 방지)
        foreach (var monster in _spawnedMonsters)
        {
            if (monster != null && monster.gameObject.name.Contains("Behemoth"))
            {
                Debug.Log("[SpawnManager] 베히모스가 이미 존재합니다. 중복 스폰 방지.");
                return;
            }
        }

        // 봉인되지 않은 존 중 하나를 찾아 베히모스를 스폰할 위치를 결정합니다.
        Vector3 spawnPos = transform.position; // 기본값
        bool posFound = false;

        foreach (var kvp in _zoneBounds)
        {
            int zone = kvp.Key;
            if (zone == ZONE_ALL) continue;
            if (_zoneSealFireRings.ContainsKey(zone)) continue; // 이미 봉인된 존 제외

            var points = kvp.Value.GetRandomPointsInZone(1, minDistance: 5f);
            if (points != null && points.Length > 0)
            {
                spawnPos = points[0];
                posFound = true;
                Debug.Log($"[SpawnManager] 베히모스 스폰 위치 → Zone {zone} ({spawnPos})");
                break;
            }
        }

        if (!posFound)
        {
            Debug.LogWarning("[SpawnManager] 베히모스 스폰 가능한 봉인되지 않은 존을 찾지 못했습니다.");
        }

        GameObject behemothPrefab = currentMap.monsterPrefabs[2];
        var netObj = SpawnServerObject(behemothPrefab, spawnPos, Quaternion.identity);
        if (netObj != null)
        {
            netObj.gameObject.name = "Behemoth_Phase2_Special";
            _spawnedMonsters.Add(netObj);
            Debug.Log($"[SpawnManager] ✅ 제한구역 2개 봉인! 베히모스 스폰 완료 → {spawnPos}");
        }
    }


    /// <summary>
    /// 2페이즈 구역 봉인 시, 해당 구역의 중앙에 고정형 성화를 스폰합니다.
    /// 이 성화는 반경이 줄어들지 않는 고정형이며, 구역 전체를 덮습니다.
    /// </summary>
    private void SpawnZoneSealFire(int zoneIndex)
    {
        if (!IsServer) return;
        if (currentMap == null || currentMap.holyFireRingPrefab == null)
        {
            Debug.LogWarning($"[SpawnManager] 성화 프리팹이 없어 Zone {zoneIndex} 봉인 성화를 생성할 수 없습니다.");
            return;
        }

        // 이미 해당 구역에 성화가 있다면 중복 생성 방지
        if (_zoneSealFireRings.ContainsKey(zoneIndex) && _zoneSealFireRings[zoneIndex] != null)
        {
            Debug.Log($"[SpawnManager] Zone {zoneIndex}에 이미 성화가 존재합니다. 중복 생성 방지.");
            return;
        }

        if (!_zoneBounds.TryGetValue(zoneIndex, out var zoneBound))
        {
            Debug.LogWarning($"[SpawnManager] Zone {zoneIndex}의 ZoneBounds를 찾을 수 없어 성화를 생성할 수 없습니다.");
            return;
        }

        // 구역 중앙 좌표 및 반경 계산
        Vector3 zoneCenter = zoneBound.transform.position;
        float halfX = 5f * zoneBound.transform.lossyScale.x * (1f - zoneBound.edgePadding);
        float halfZ = 5f * zoneBound.transform.lossyScale.z * (1f - zoneBound.edgePadding);
        float zoneRadius = Mathf.Max(halfX, halfZ);

        // Raycast로 바닥을 찾지 않고, ZoneBounds 오브젝트의 원본 높이를 그대로 유지합니다.
        // (복층 구조에서 최상단 지붕에 레이캐스트가 맞아버려 아랫층이 무시되는 버그 방지)

        var instance = Instantiate(currentMap.holyFireRingPrefab, zoneCenter, Quaternion.identity);
        var netObj = instance.GetComponent<NetworkObject>();

        if (netObj != null)
        {
            var ring = instance.GetComponent<HolyFireRingController>();
            if (ring != null)
            {
                ring.centerPoint.Value = zoneCenter;
                ring.currentRadius.Value = zoneRadius;
                ring.isZoneSealFire.Value = true; // 2페이즈 구역 장판형으로 설정
                // zoneHeight 값을 서버 Extents의 Y축으로 전달하여 클라이언트가 구역 높이를 알 수 있게 함
                ring.zoneExtents.Value = new Vector3(halfX, zoneBound.zoneHeight, halfZ); 
            }
            netObj.Spawn();
            _zoneSealFireRings[zoneIndex] = netObj;
            Debug.Log($"[SpawnManager] ✅ Zone {(char)('A' + zoneIndex)} 봉인 성화 스폰 완료! 중심: {zoneCenter}, 반경: {zoneRadius}");
        }
    }

    /// <summary>
    /// 3페이즈 진입 등으로 인해 2페이즈 구역별 성화를 모두 제거해야 할 때 호출합니다.
    /// </summary>
    public void DespawnAllZoneSealFires()
    {
        if (!IsServer) return;

        foreach (var kvp in _zoneSealFireRings)
        {
            if (kvp.Value != null && kvp.Value.IsSpawned)
            {
                kvp.Value.Despawn(true);
            }
        }
        _zoneSealFireRings.Clear();
        Debug.Log("[SpawnManager] 모든 2페이즈 구역별 성화를 제거했습니다.");
    }

    private IEnumerator DelayedZoneSealDespawn(int zoneIndex, float delay)
    {
        yield return new WaitForSeconds(delay);

        if (!IsServer) yield break;

        Debug.Log($"[SpawnManager] Zone {zoneIndex} 봉인 후 {delay}초 경과. 해당 구역의 몬스터, 어린양, 상자를 최종 소멸 처리합니다.");

        // 1. 어린양 소멸 (해당 zoneIndex 리스트 순회)
        if (_lambsByZone.TryGetValue(zoneIndex, out var lambs))
        {
            int despawnedLambs = 0;
            foreach (var lamb in lambs)
            {
                if (lamb != null && lamb.IsSpawned)
                {
                    lamb.Despawn(true);
                    despawnedLambs++;
                }
            }
            Debug.Log($"[SpawnManager] 지연 소멸 완료: Zone {zoneIndex} 어린양 {despawnedLambs}마리.");
            _lambsByZone[zoneIndex].Clear();
        }

        // 2. 몬스터 소멸 (ZoneBounds 범위 판정)
        if (_zoneBounds.TryGetValue(zoneIndex, out var bounds))
        {
            int despawnedMonsters = 0;
            for (int i = _spawnedMonsters.Count - 1; i >= 0; i--)
            {
                var monster = _spawnedMonsters[i];
                if (monster != null && monster.IsSpawned)
                {
                    if (bounds.Contains(monster.transform.position))
                    {
                        monster.Despawn(true);
                        _spawnedMonsters.RemoveAt(i);
                        despawnedMonsters++;
                    }
                }
                else
                {
                    _spawnedMonsters.RemoveAt(i);
                }
            }
            Debug.Log($"[SpawnManager] 지연 소멸 완료: Zone {zoneIndex} 몬스터 {despawnedMonsters}마리.");

            // 3. 상자 소멸 (ZoneBounds 범위 판정)
            int despawnedChests = 0;
            for (int i = _spawnedChests.Count - 1; i >= 0; i--)
            {
                var chest = _spawnedChests[i];
                if (chest != null && chest.IsSpawned)
                {
                    if (bounds.Contains(chest.transform.position))
                    {
                        chest.Despawn(true);
                        _spawnedChests.RemoveAt(i);
                        despawnedChests++;
                    }
                }
                else
                {
                    _spawnedChests.RemoveAt(i);
                }
            }
            Debug.Log($"[SpawnManager] 지연 소멸 완료: Zone {zoneIndex} 상자 {despawnedChests}개.");

            // 4. 토템 소멸 (ZoneBounds 범위 판정)
            int despawnedTotems = 0;
            for (int i = _spawnedTotems.Count - 1; i >= 0; i--)
            {
                var totem = _spawnedTotems[i];
                if (totem != null && totem.IsSpawned)
                {
                    if (bounds.Contains(totem.transform.position))
                    {
                        totem.Despawn(true);
                        _spawnedTotems.RemoveAt(i);
                        despawnedTotems++;
                    }
                }
                else
                {
                    _spawnedTotems.RemoveAt(i);
                }
            }
            Debug.Log($"[SpawnManager] 지연 소멸 완료: Zone {zoneIndex} 토템 {despawnedTotems}개.");
        }
    }

    // ─────────────────────────────────────────────────────────
    // Phase 전환 API
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 1 시작: 양 + 제단(마커 기반) 스폰. 플레이어는 별도 RPC로 이미 처리됨.
    /// </summary>
    public void StartPhase1()
    {
        if (!NetworkManager.Singleton.IsServer) return;
        Debug.Log("[SpawnManager] ===== Phase 1 시작 =====");
        SpawnAltarsAtMarkers();

        // 어린양 스폰포인트 시스템 초기화 (제단 위치 전달)
        if (LambSpawnSystem.Instance != null)
        {
            var altarPositions = new List<Vector3>();
            foreach (var altar in _spawnedAltars)
            {
                if (altar != null && altar.IsSpawned)
                    altarPositions.Add(altar.transform.position);
            }
            LambSpawnSystem.Instance.InitializeWithAltars(altarPositions);
        }
        else
        {
            Debug.LogWarning("[SpawnManager] LambSpawnSystem을 찾을 수 없어 기존 방식으로 대체합니다.");
            SpawnInitialLambs();
        }
    }

    /// <summary>
    /// Phase 2 시작: 제단 제거 → 토템(마커 기반) + 몬스터(랜덤) + 상자(랜덤) 스폰.
    /// </summary>
    public void StartPhase2()
    {
        if (!IsServer) return;
        Debug.Log("[SpawnManager] ===== Phase 2 시작 =====");

        if (LambSpawnSystem.Instance != null)
            LambSpawnSystem.Instance.Shutdown();

        // Phase 2 오브젝트 스폰 시 렉을 방지하기 위해 전체 시퀀스를 코루틴으로 분산 처리
        StartCoroutine(StartPhase2Coroutine());
    }

    private IEnumerator StartPhase2Coroutine()
    {
        // 1. 기존 오브젝트 순차 삭제 (렉 방지)
        yield return StartCoroutine(DespawnAllAltarsCoroutine());
        yield return StartCoroutine(DespawnAllLambsCoroutine());

        // 2. 토템 스폰 분산
        yield return StartCoroutine(SpawnTotemsAtMarkersCoroutine());

        // 3. 몬스터 스폰 분산
        yield return StartCoroutine(SpawnMonstersRandomCoroutine());
    }

    /// <summary>
    /// 맵에 배치된 모든 SpawnPointMarker와 ZoneBounds를 수집하여 분류합니다.
    /// </summary>
    public void Initialize()
    {
        _pointsByZoneAndType = new Dictionary<int, Dictionary<SpawnPointMarker.SpawnType, List<SpawnPointMarker>>>();
        _zoneBounds = new Dictionary<int, ZoneBounds>();
        _usedPoints.Clear();

        // SpawnPointMarker 수집
        var allMarkers = FindObjectsOfType<SpawnPointMarker>();
        int criminalCount = 0;
        int greatSinnerCount = 0;

        foreach (var marker in allMarkers)
        {
            int zone = marker.zoneIndex;
            var type = marker.spawnType;

            if (type == SpawnPointMarker.SpawnType.PlayerCriminal) criminalCount++;
            if (type == SpawnPointMarker.SpawnType.PlayerGreatSinner) greatSinnerCount++;

            if (!_pointsByZoneAndType.ContainsKey(zone))
                _pointsByZoneAndType[zone] = new Dictionary<SpawnPointMarker.SpawnType, List<SpawnPointMarker>>();

            if (!_pointsByZoneAndType[zone].ContainsKey(type))
                _pointsByZoneAndType[zone][type] = new List<SpawnPointMarker>();

            _pointsByZoneAndType[zone][type].Add(marker);
        }

        // ZoneBounds 수집
        var allZoneBounds = FindObjectsOfType<ZoneBounds>();
        foreach (var zb in allZoneBounds)
        {
            _zoneBounds[zb.zoneIndex] = zb;

            // [자동 부착] ZoneTriggerVolume이 씬에 없으면 런타임에 자동으로 추가합니다.
            // 이 컴포넌트가 없으면 제한구역 도트 데미지가 전혀 동작하지 않습니다.
            if (zb.GetComponent<ZoneTriggerVolume>() == null)
            {
                var trigger = zb.gameObject.AddComponent<ZoneTriggerVolume>();
                trigger.zoneIndex = zb.zoneIndex;
                Debug.Log($"[SpawnManager] Zone {zb.zoneIndex}에 ZoneTriggerVolume 자동 부착 완료. (봉인 시 도트 데미지 활성화됨)");
            }
        }

        Debug.Log($"[SpawnManager] 초기화 성공! 마커 {allMarkers.Length}개(악인:{criminalCount}, 대죄인:{greatSinnerCount}), ZoneBounds {allZoneBounds.Length}개");
    }

    // ─────────────────────────────────────────────────────────
    // 플레이어 스폰 공개 API
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// [도메인 명세 §2.2] 악인 플레이어를 현재 Zone의 스폰 포인트 중 랜덤 위치에 스폰합니다.
    /// </summary>
    public NetworkObject SpawnPlayerAtRandomPoint(ulong clientId, int zoneIndex, bool isGreatSinner = false, CharacterId? specificCharacter = null)
    {
        if (!IsServer) return null;

        var spawnType = isGreatSinner
            ? SpawnPointMarker.SpawnType.PlayerGreatSinner
            : SpawnPointMarker.SpawnType.PlayerCriminal;

        var marker = GetRandomPoint(zoneIndex, spawnType);

        // 지정 Zone에 포인트가 없으면 전체 Zone에서 재탐색
        if (marker == null)
        {
            Debug.LogWarning($"[SpawnManager] Zone {zoneIndex}에 {spawnType} 포인트가 없어 전체에서 재탐색합니다.");
            marker = GetRandomPointFromAll(spawnType);
        }

        if (marker == null)
        {
            Debug.LogError($"[SpawnManager] '{spawnType}' 유형의 스폰 포인트를 찾지 못했습니다. 맵에 SpawnPointMarker를 배치했는지 확인하세요.");
            return null;
        }

        // 단일 플레이어 프리팹 스폰
        if (playerPrefab == null)
        {
            Debug.LogError("[SpawnManager] 스폰할 플레이어 프리팹이 등록되지 않았습니다! 인스펙터를 확인하세요.");
            return null;
        }

        var netObj = SpawnPlayerObject(playerPrefab, marker.transform.position, marker.transform.rotation, clientId);
        
        // 스폰된 플레이어 객체에 선택한 캐릭터 정보 주입
        if (netObj != null && specificCharacter.HasValue)
        {
            if (netObj.TryGetComponent<PlayerNetworkController>(out var playerCtrl))
            {
                playerCtrl.currentCharacterId.Value = specificCharacter.Value;
                Debug.Log($"[SpawnManager] Client {clientId}에게 캐릭터 {specificCharacter.Value} 세팅 완료.");
            }
        }

        return netObj;
    }

    /// <summary>
    /// Phase 3 전용 스폰 포인트에 악인을 재배치합니다.
    /// </summary>
    public NetworkObject SpawnPlayerPhase3(ulong clientId, int criminalIndex)
    {
        if (!IsServer) return null;

        var allP3 = GetAllPoints(SpawnPointMarker.SpawnType.Phase3Spawn);

        if (allP3 == null || allP3.Count == 0)
        {
            Debug.LogError("[SpawnManager] Phase3Spawn 포인트가 없습니다!");
            return null;
        }

        int idx = Mathf.Clamp(criminalIndex, 0, allP3.Count - 1);
        var marker = allP3[idx];

        // LobbyStateSync에서 해당 클라이언트의 마지막 선택 캐릭터 조회
        CharacterId? specificCharacter = null;
        if (LobbyStateSync.Instance != null)
        {
            foreach (var p in LobbyStateSync.Instance.LobbyPlayers)
            {
                if (p.ClientId == clientId)
                {
                    specificCharacter = p.SelectedCharacter;
                    break;
                }
            }
        }

        var netObj = SpawnPlayerObject(playerPrefab, marker.transform.position, marker.transform.rotation, clientId);
        
        if (netObj != null && specificCharacter.HasValue)
        {
            if (netObj.TryGetComponent<PlayerNetworkController>(out var playerCtrl))
            {
                playerCtrl.currentCharacterId.Value = specificCharacter.Value;
            }
        }

        return netObj;
    }

    // ─────────────────────────────────────────────────────────
    // 제단·토템 (마커 기반 스폰)
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Altar 유형의 SpawnPointMarker 중 Zone별로 altarsPerZone개를 최소 거리를 유지하며 랜덤 선택하여 제단을 스폰합니다. (Phase 1)
    /// </summary>
    private void SpawnAltarsAtMarkers()
    {
        if (currentMap == null || currentMap.altarPrefab == null)
        {
            Debug.LogWarning("[SpawnManager] altarPrefab이 MapSO에 설정되지 않았습니다.");
            return;
        }

        var allMarkers = GetAllPoints(SpawnPointMarker.SpawnType.Altar);
        var grouped = GroupMarkersByZone(allMarkers);
        int totalSpawned = 0;

        foreach (var kvp in grouped)
        {
            int zone = kvp.Key;
            var selected = SelectMarkersWithMinDistance(kvp.Value, currentMap.altarsPerZone, 15f);
            var container = GetOrCreateContainer(zone, "SpawnAltar");

            for (int i = 0; i < selected.Count; i++)
            {
                var marker = selected[i];
                var netObj = SpawnServerObject(currentMap.altarPrefab, marker.transform.position, marker.transform.rotation);
                if (netObj != null)
                {
                    netObj.gameObject.name = $"Altar_Zone{(char)('A' + zone)}_{i + 1:00}";
                    // netObj.transform.SetParent(container, true); // NGO: NetworkObject는 비-NetworkObject 부모 불가
                    _spawnedAltars.Add(netObj);
                    totalSpawned++;
                }
            }
        }
        Debug.Log($"[SpawnManager] 제단 {totalSpawned}개 스폰 완료 (마커 {allMarkers.Count}개 중 랜덤 선택, 최소 거리 15m).");
    }

    private IEnumerator DespawnAllAltarsCoroutine()
    {
        if (!IsServer) yield break;
        int count = 0;
        foreach (var obj in _spawnedAltars)
        {
            if (obj != null && obj.IsSpawned) 
            {
                obj.Despawn();
                count++;
                if (count % 3 == 0) yield return null;
            }
        }
        _spawnedAltars.Clear();
        Debug.Log("[SpawnManager] 제단 분산 Despawn 완료.");
    }

    /// <summary>
    /// Totem 유형의 SpawnPointMarker 중 Zone별로 totemsPerZone개를 최소 거리를 유지하며 랜덤 선택하여 토템을 스폰합니다. (Phase 2)
    /// </summary>
    private IEnumerator SpawnTotemsAtMarkersCoroutine()
    {
        if (currentMap == null || currentMap.totemPrefab == null)
        {
            Debug.LogWarning("[SpawnManager] totemPrefab이 MapSO에 설정되지 않았습니다.");
            yield break;
        }

        var allMarkers = GetAllPoints(SpawnPointMarker.SpawnType.Totem);
        
        // [Fallback] 토템 마커가 하나도 없다면 경고 (사용자가 직접 씬에 배치해야 함)
        if (allMarkers == null || allMarkers.Count == 0)
        {
            Debug.LogWarning("[SpawnManager] 토템 마커가 씬에 하나도 없습니다! 임시로 제단 마커를 사용합니다.");
            allMarkers = GetAllPoints(SpawnPointMarker.SpawnType.Altar);
        }

        var grouped = GroupMarkersByZone(allMarkers);
        int totalSpawned = 0;

        foreach (var kvp in grouped)
        {
            int zone = kvp.Key;
            if (kvp.Value.Count == 0) continue;
            
            // 각 구역에 배치된 마커 중 3개를 무작위 선택하되, 서로 15m 이상 간격 유지 (없으면 최대한 멀리 떨어진 것)
            var selected = SelectMarkersWithMinDistance(kvp.Value, currentMap.totemsPerZone, 15f);
            var container = GetOrCreateContainer(zone, "SpawnTotem");

            for (int i = 0; i < selected.Count; i++)
            {
                var marker = selected[i];
                float yOffset = currentMap.totemPrefab.transform.localScale.y * 0.5f;
                
                // Spawn() 호출 전에 zoneIndex를 덮어씌워야 TotemManager의 Dictionary에 올바른 구역으로 등록됨!
                // 새롭게 개선된 bounds.min.y 기반 바닥 안착 완벽 보정 로직을 적용합니다.
                var netObj = SpawnServerObject(currentMap.totemPrefab, marker.transform.position, marker.transform.rotation, 0f, 
                    onBeforeSpawn: (instance) => {
                        var controller = instance.GetComponent<TotemController>();
                        if (controller != null) controller.zoneIndex = zone;
                    });

                if (netObj != null)
                {
                    netObj.gameObject.name = $"Totem_Zone{(char)('A' + zone)}_{i + 1:00}";
                    _spawnedTotems.Add(netObj);
                    totalSpawned++;
                }

                if (i % 2 == 1) yield return null;
            }
        }
        Debug.Log($"[SpawnManager] 토템 {totalSpawned}개 분산 스폰 완료.");
    }

    /// <summary>
    /// 에반젤린 패시브 및 거짓 인도(아이템)에서 호출하는 가짜 토템 생성 메서드
    /// </summary>
    public NetworkObject SpawnIllusoryTotem(Vector3 position)
    {
        if (!IsServer) return null;
        if (currentMap == null || currentMap.totemPrefab == null)
        {
            Debug.LogWarning("[SpawnManager] totemPrefab이 없어 가짜 토템을 스폰할 수 없습니다.");
            return null;
        }

        var netObj = SpawnServerObject(currentMap.totemPrefab, position, Quaternion.identity);
        if (netObj != null)
        {
            netObj.gameObject.name = "Totem_Illusory_Fake";
            
            // 진짜 토템처럼 보이지만 상호작용 시 파괴되거나 맵 진행에 반영되지 않도록 처리
            var controller = netObj.GetComponent<TotemController>();
            if (controller != null)
            {
                controller.zoneIndex = -1; // 가짜 토템 식별용 임의값
                controller.currentDurability.Value = 1; // 1회 공격 시 파괴
                controller.state.Value = TotemState.Active;
            }
            Debug.Log($"[SpawnManager] 가짜 토템(환영) 스폰 완료: {position}");
        }
        return netObj;
    }

    /// <summary>
    /// 마커 목록을 Zone별로 그룹화합니다. Zone 4(All) 마커는 모든 Zone에 포함됩니다.
    /// </summary>
    private Dictionary<int, List<SpawnPointMarker>> GroupMarkersByZone(List<SpawnPointMarker> markers)
    {
        var grouped = new Dictionary<int, List<SpawnPointMarker>>();
        var allZoneMarkers = new List<SpawnPointMarker>(); // Zone 4(All) 마커 임시 저장

        foreach (var marker in markers)
        {
            if (marker.zoneIndex == ZONE_ALL)
            {
                allZoneMarkers.Add(marker);
                continue;
            }

            if (!grouped.ContainsKey(marker.zoneIndex))
                grouped[marker.zoneIndex] = new List<SpawnPointMarker>();
            grouped[marker.zoneIndex].Add(marker);
        }

        // Zone 4(All) 마커를 각 Zone에 추가 — 기존 Zone 그룹이 없으면 새로 만듦
        if (allZoneMarkers.Count > 0)
        {
            foreach (var kvp in _zoneBounds)
            {
                if (kvp.Key == ZONE_ALL) continue;
                if (!grouped.ContainsKey(kvp.Key))
                    grouped[kvp.Key] = new List<SpawnPointMarker>();
                grouped[kvp.Key].AddRange(allZoneMarkers);
            }
        }

        return grouped;
    }

    /// <summary>
    /// 마커 목록을 셔플한 뒤 최소 거리(minDistance)를 만족하는 것만 최대 count개 선택합니다.
    /// </summary>
    private List<SpawnPointMarker> SelectMarkersWithMinDistance(List<SpawnPointMarker> candidates, int count, float minDistance)
    {
        // Fisher-Yates 셔플
        var shuffled = new List<SpawnPointMarker>(candidates);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            var temp = shuffled[i];
            shuffled[i] = shuffled[j];
            shuffled[j] = temp;
        }

        var selected = new List<SpawnPointMarker>();
        float minDistSqr = minDistance * minDistance;

        foreach (var marker in shuffled)
        {
            if (selected.Count >= count) break;

            // 이미 선택된 마커들과 최소 거리 확인
            bool tooClose = false;
            foreach (var existing in selected)
            {
                if ((marker.transform.position - existing.transform.position).sqrMagnitude < minDistSqr)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
                selected.Add(marker);
        }

        return selected;
    }

    /// <summary>
    /// 씬 내에 배치된 모든 Phase 3 전용 스폰 포인트를 수집하여 반환합니다.
    /// </summary>
    public List<SpawnPointMarker> GetPhase3SpawnPoints()
    {
        var result = new List<SpawnPointMarker>();
        foreach (var zoneKvp in _pointsByZoneAndType)
        {
            if (zoneKvp.Value.TryGetValue(SpawnPointMarker.SpawnType.Phase3Spawn, out var list))
            {
                result.AddRange(list);
            }
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────
    // 몬스터·상자 랜덤 스폰 (Phase 2)
    // ─────────────────────────────────────────────────────────

    /// </summary>
    private IEnumerator SpawnMonstersRandomCoroutine()
    {
        if (currentMap == null || currentMap.monsterPrefabs == null || currentMap.monsterPrefabs.Length == 0)
        {
            Debug.LogWarning("[SpawnManager] monsterPrefabs가 MapSO에 설정되지 않았습니다.");
            yield break;
        }

        _respawningMonstersByZone.Clear();

        foreach (var kvp in _zoneBounds)
        {
            int zone = kvp.Key;
            if (zone == ZONE_ALL) continue; // ZoneAll은 맵 자체

            var bounds = kvp.Value;
            var container = GetOrCreateContainer(zone, "SpawnMonster");
            
            // 밸런스를 위해 구역당 몬스터 수를 6마리로 고정
            int targetMonsterCount = 6;
            _respawningMonstersByZone[zone] = 0;

            var positions = bounds.GetRandomPointsInZone(targetMonsterCount, minDistance: 8f);

            for (int i = 0; i < positions.Length; i++)
            {
                SpawnSingleRandomMonster(zone, positions[i]);
                
                // [프레임 방어] 몬스터 2마리 스폰할 때마다 1프레임 대기하여 렉(Lag) 억제
                if (i % 2 == 1)
                {
                    yield return null;
                }
            }
            Debug.Log($"[SpawnManager] Zone {zone}에 몬스터 {positions.Length}마리 분산 스폰 완료.");

            // 각 구역별로 몬스터 리젠을 관리하는 코루틴 시작
            StartCoroutine(MonitorZoneMonstersCoroutine(zone, targetMonsterCount));
        }
    }

    private void SpawnSingleRandomMonster(int zone, Vector3 position)
    {
        GameObject prefab = currentMap.monsterPrefabs[0];
        
        // 일반 구역(Zone A, B, C, D)에서는 종자(75%)와 습격자(25%)로 몬스터 스폰
        if (currentMap.monsterPrefabs.Length >= 2)
        {
            prefab = (Random.value < 0.75f) ? currentMap.monsterPrefabs[0] : currentMap.monsterPrefabs[1];
        }

        var netObj = SpawnServerObject(prefab, position, Quaternion.identity);
        if (netObj != null)
        {
            netObj.gameObject.name = $"Monster_Zone{(char)('A' + zone)}_{Time.frameCount}";
            _spawnedMonsters.Add(netObj);
        }
    }

    /// <summary>
    /// 주기적으로 구역 내 몬스터 개수를 확인하고 부족하면 리젠을 예약합니다.
    /// </summary>
    private IEnumerator MonitorZoneMonstersCoroutine(int zone, int targetCount)
    {
        while (IsServer && PhaseManager.Instance != null && PhaseManager.Instance.CurrentPhase == Phase.Phase2)
        {
            yield return new WaitForSeconds(5f);

            // 해당 구역이 봉인되었거나 3페이즈로 넘어가면 리젠 감시 중단
            if (PhaseManager.Instance.IsZoneSealed(zone) || PhaseManager.Instance.CurrentPhase != Phase.Phase2)
            {
                Debug.Log($"[SpawnManager] Zone {zone} 봉인됨. 해당 구역의 몬스터 리젠을 완전히 중단합니다.");
                yield break;
            }

            if (!_zoneBounds.TryGetValue(zone, out var bounds)) continue;

            // null이거나 죽은 몬스터 리스트에서 정리
            _spawnedMonsters.RemoveAll(m => m == null || !m.IsSpawned);

            // 현재 구역 안에 살아있는 몬스터 수 계산
            int aliveCount = 0;
            foreach (var m in _spawnedMonsters)
            {
                if (bounds.Contains(m.transform.position))
                {
                    aliveCount++;
                }
            }

            // 리젠 대기 중인 몬스터까지 합쳐서 부족한 수 계산
            int totalExpected = aliveCount + _respawningMonstersByZone[zone];
            
            if (totalExpected < targetCount)
            {
                int needToRespawn = targetCount - totalExpected;
                _respawningMonstersByZone[zone] += needToRespawn;
                
                for (int i = 0; i < needToRespawn; i++)
                {
                    StartCoroutine(RespawnMonsterWithDelayCoroutine(zone));
                }
            }
        }
    }

    private IEnumerator RespawnMonsterWithDelayCoroutine(int zone)
    {
        // 20~30초 랜덤 대기
        float respawnDelay = Random.Range(20f, 30f);
        yield return new WaitForSeconds(respawnDelay);

        // 대기 후 봉인 여부 재확인
        if (!IsServer || PhaseManager.Instance == null || PhaseManager.Instance.CurrentPhase != Phase.Phase2 || PhaseManager.Instance.IsZoneSealed(zone))
        {
            _respawningMonstersByZone[zone]--; // 리젠 취소
            yield break;
        }

        if (_zoneBounds.TryGetValue(zone, out var bounds))
        {
            var positions = bounds.GetRandomPointsInZone(1, minDistance: 8f);
            if (positions != null && positions.Length > 0)
            {
                SpawnSingleRandomMonster(zone, positions[0]);
                Debug.Log($"[SpawnManager] Zone {zone} 몬스터 리젠 완료. (대기시간: {respawnDelay:F1}초)");
            }
        }
        
        _respawningMonstersByZone[zone]--;
    }

    /// <summary>
    /// 맵 ZoneBounds 중 랜덤 위치에 상자 1개를 스폰합니다. (ChestManager 전용)
    /// </summary>
    public ChestController SpawnChestAtRandomPosition()
    {
        if (!IsServer || currentMap == null || currentMap.chestPrefab == null)
        {
            Debug.LogWarning($"[SpawnManager] 상자 스폰 실패: IsServer={IsServer}, currentMap={currentMap != null}, chestPrefab={currentMap?.chestPrefab != null}");
            return null;
        }

        var zoneKeys = new List<int>();
        foreach (var kvp in _zoneBounds)
        {
            if (kvp.Key != ZONE_ALL)
                zoneKeys.Add(kvp.Key);
        }

        if (zoneKeys.Count == 0)
        {
            Debug.LogWarning("[SpawnManager] 상자 스폰 실패: 스폰 가능한 구역 Bounds(ZoneBounds)가 0개입니다! 씬에 ZoneBounds 컴포넌트가 있는지 확인해주세요.");
            return null;
        }

        const int maxAttempts = 24;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int zone = zoneKeys[Random.Range(0, zoneKeys.Count)];
            if (!_zoneBounds.TryGetValue(zone, out var bounds))
                continue;

            var positions = bounds.GetRandomPointsInZone(1, minDistance: 12f);
            if (positions.Length == 0)
            {
                Debug.LogWarning($"[SpawnManager] 상자 스폰 시도 {attempt + 1}: {zone} 구역에서 랜덤 지점을 추출하지 못했습니다.");
                continue;
            }

            var netObj = SpawnServerObject(currentMap.chestPrefab, positions[0], Quaternion.identity);
            if (netObj == null)
            {
                Debug.LogWarning($"[SpawnManager] 상자 스폰 시도 {attempt + 1}: SpawnServerObject가 null을 반환했습니다.");
                continue;
            }

            var chest = netObj.GetComponent<ChestController>();
            if (chest == null)
            {
                Debug.LogWarning($"[SpawnManager] 상자 스폰 시도 {attempt + 1}: 스폰된 객체에 ChestController 컴포넌트가 없습니다! (이름: {netObj.name})");
                netObj.Despawn(true);
                continue;
            }

            netObj.gameObject.name = $"Chest_Zone{(char)('A' + zone)}_{Random.Range(1000, 9999)}";
            _spawnedChests.Add(netObj);

            if (ChestManager.Instance != null)
                ChestManager.Instance.RegisterChest(chest);

            Debug.Log($"[SpawnManager] 상자 스폰 성공: {netObj.gameObject.name} (위치: {positions[0]})");
            return chest;
        }

        Debug.LogWarning($"[SpawnManager] 상자 스폰 실패: {maxAttempts}번의 모든 무작위 스폰 좌표 생성이 거절되었습니다.");
        return null;
    }

    // ─────────────────────────────────────────────────────────
    // Despawn 유틸리티
    // ─────────────────────────────────────────────────────────

    private void DespawnAllAltars()
    {
        if (!IsServer) return;
        foreach (var obj in _spawnedAltars)
            if (obj != null && obj.IsSpawned) obj.Despawn();
        _spawnedAltars.Clear();
        Debug.Log("[SpawnManager] 모든 제단 Despawn 완료.");
    }

    private void DespawnAllTotems()
    {
        if (!IsServer) return;
        foreach (var obj in _spawnedTotems)
            if (obj != null && obj.IsSpawned) obj.Despawn();
        _spawnedTotems.Clear();
    }

    private void DespawnAllMonsters()
    {
        if (!IsServer) return;
        foreach (var obj in _spawnedMonsters)
            if (obj != null && obj.IsSpawned) obj.Despawn();
        _spawnedMonsters.Clear();
    }

    private void DespawnAllChests()
    {
        if (!IsServer) return;
        foreach (var obj in _spawnedChests)
            if (obj != null && obj.IsSpawned) obj.Despawn();
        _spawnedChests.Clear();
    }

    /// <summary>
    /// Phase 3 진입 시 플레이어를 제외한 모든 엔티티를 제거합니다.
    /// (제단, 토템, 몬스터, 상자, 어린양 등)
    /// </summary>
    public void DespawnAllPhase2Objects()
    {
        if (!NetworkManager.Singleton.IsServer) return;
        Debug.Log("[SpawnManager] ===== Phase 3 진입: 모든 엔티티 정리 =====");

        DespawnAllAltars();
        DespawnAllTotems();
        DespawnAllMonsters();
        DespawnAllChests();
        DespawnAllLambs(); // 페이즈 2 타락한 양 정리
        
        // [디버그용 예외 처리] 페이즈 2를 건너뛰고 3으로 바로 넘어왔을 경우 페이즈 1 양들이 남아있는 것 방지
        if (LambSpawnSystem.Instance != null)
            LambSpawnSystem.Instance.Shutdown();

        Debug.Log("[SpawnManager] 모든 이전 객체 정리 완료.");
    }

    /// <summary>
    /// Phase 3 진입 시 성화 반지를 스폰합니다.
    /// </summary>
    public NetworkObject SpawnHolyFireRing(Vector3 center, float initialRadius, bool permanent = false)
    {
        if (!NetworkManager.Singleton.IsServer) return null;
        if (currentMap == null)
        {
            Debug.LogError("[SpawnManager] currentMap이 null입니다. 3페이즈 성화 반지를 스폰할 수 없습니다!");
            return null;
        }
        if (currentMap.holyFireRingPrefab == null)
        {
            Debug.LogError("[SpawnManager] MapConfigSO에 holyFireRingPrefab이 할당되지 않았습니다. 3페이즈 성화 반지를 스폰할 수 없습니다!");
            return null;
        }

        // 존의 밑바닥에 맞추기 위해 center가 포함된 ZoneBounds를 찾아 Y값을 보정합니다.
        float targetY = center.y;
        if (_zoneBounds != null)
        {
            ZoneBounds matchedZone = null;
            foreach (var zb in _zoneBounds.Values)
            {
                if (zb.zoneIndex != ZONE_ALL && zb.Contains(center))
                {
                    matchedZone = zb;
                    break;
                }
            }

            if (matchedZone != null)
            {
                // 구역 크기에 맞춰 시작 반지름 자동 조정
                float halfX = 5f * matchedZone.transform.lossyScale.x * (1f - matchedZone.edgePadding);
                float halfZ = 5f * matchedZone.transform.lossyScale.z * (1f - matchedZone.edgePadding);
                initialRadius = Mathf.Max(halfX, halfZ);
            }
            else if (_zoneBounds.TryGetValue(ZONE_ALL, out var allZone))
            {
                float halfX = 5f * allZone.transform.lossyScale.x * (1f - allZone.edgePadding);
                float halfZ = 5f * allZone.transform.lossyScale.z * (1f - allZone.edgePadding);
                initialRadius = Mathf.Max(halfX, halfZ);
            }
            
            // 대죄인이 죽은 위치(center)에서 아래로 Raycast를 쏴서, 
            // 0층이나 1층 등 플레이어가 실제로 서 있는 층의 바닥을 정확히 찾습니다.
            if (Physics.Raycast(new Vector3(center.x, center.y + 5f, center.z), Vector3.down, out RaycastHit hit, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                targetY = hit.point.y;
            }
            else
            {
                targetY = center.y; // Raycast 실패 시 대죄인 사망 높이 유지
            }
        }
        
        center.y = targetY;

        // 대죄인의 크기를 기준으로 허리 높이(절반) 오프셋 계산
        float waistOffset = 1.0f; // 기본 오프셋
        if (PlayerManager.Instance != null && PlayerManager.Instance.greatSinner != null && PlayerManager.Instance.greatSinner.avatarObject != null)
        {
            var gsObj = PlayerManager.Instance.greatSinner.avatarObject;
            if (gsObj.TryGetComponent<CharacterController>(out var cc))
            {
                waistOffset = cc.height * 0.5f;
            }
            else if (gsObj.TryGetComponent<CapsuleCollider>(out var cap))
            {
                waistOffset = cap.height * 0.5f;
            }
        }

        // SpawnServerObject는 지형 굴곡(NavMesh)을 따라 Y가 올라가버릴 수 있으므로 직접 Instantiate하여 밑바닥(Y)을 고정시킵니다.
        var instance = Instantiate(currentMap.holyFireRingPrefab, center, Quaternion.identity);
        var netObj = instance.GetComponent<NetworkObject>();

        if (netObj != null)
        {
            var ring = instance.GetComponent<HolyFireRingController>();
            if (ring != null)
            {
                ring.centerPoint.Value = center;
                ring.currentRadius.Value = initialRadius;
            }
            netObj.Spawn();
            Debug.Log($"[SpawnManager] 성화 반지 스폰 완료 (존 밑바닥 고정 적용). 중심: {center}, 반경: {initialRadius}");
        }
        return netObj;
    }

    // ─────────────────────────────────────────────────────────
    // 하이어라키 컨테이너
    // ─────────────────────────────────────────────────────────

    private Transform GetOrCreateContainer(int zoneIndex, string childName)
    {
        string zoneName = $"SpawnZone{(char)('A' + zoneIndex)}";

        var zoneObj = GameObject.Find(zoneName);
        if (zoneObj == null)
            zoneObj = new GameObject(zoneName);

        var childTransform = zoneObj.transform.Find(childName);
        if (childTransform == null)
        {
            var childObj = new GameObject(childName);
            childObj.transform.SetParent(zoneObj.transform);
            childTransform = childObj.transform;
        }

        return childTransform;
    }

    // ─────────────────────────────────────────────────────────
    // 어린양 랜덤 스폰 + 리스폰 API
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// 모든 Zone(A~D)에 어린양을 maxAliveLambs까지 균등 분배하여 랜덤 생성합니다.
    /// Zone 4(All)은 맵 자체이므로 스폰 대상에서 제외됩니다.
    /// 인스펙터(서버)에서만 호출하세요.
    /// </summary>
    public void SpawnInitialLambs()
    {
        if (!IsServer || currentMap == null) return;
        if (currentMap.lambPrefab == null)
        {
            Debug.LogWarning("[SpawnManager] lambPrefab이 MapSO에 설정되지 않았습니다.");
            return;
        }

        _spawnedNormalCount = 0;
        _spawnedInnocentCount = 0;

        // Zone 4(All)을 제외한 실제 스폰 가능 Zone 수
        int zoneCount = 0;
        foreach (var kvp in _zoneBounds)
            if (kvp.Key != ZONE_ALL) zoneCount++;

        if (zoneCount == 0) return;

        // maxAliveLambs를 실제 Zone 수로 균등 분배
        int basePerZone = currentMap.maxAliveLambs / zoneCount;
        int remainder = currentMap.maxAliveLambs % zoneCount;

        int idx = 0;
        foreach (var kvp in _zoneBounds)
        {
            int zone = kvp.Key;
            if (zone == ZONE_ALL) continue; // ZoneAll은 맵 자체 — 스폰 대상 아님

            int quota = basePerZone + (idx < remainder ? 1 : 0);
            idx++;

            if (!_lambsByZone.ContainsKey(zone))
                _lambsByZone[zone] = new List<NetworkObject>();

            FillLambsInZone(zone, quota);
        }

        int totalAlive = GetTotalAliveLambs();
        Debug.Log($"[SpawnManager] 어린양 초기 스폰 완료. 동시 생존: {totalAlive}/{currentMap.maxAliveLambs}, 누적 (일반:{_spawnedNormalCount}/{currentMap.totalNormalLambs}, 무구:{_spawnedInnocentCount}/{currentMap.totalInnocentLambs})");
    }

    /// <summary>
    /// 특정 Zone의 어린양을 quota만큼 채웁니다. 누적 총수를 초과하면 스폰하지 않습니다.
    /// </summary>
    private void FillLambsInZone(int zoneIndex, int quota = -1)
    {
        if (!_lambsByZone.ContainsKey(zoneIndex))
            _lambsByZone[zoneIndex] = new List<NetworkObject>();

        // Despawn된 네트워크 정리
        _lambsByZone[zoneIndex].RemoveAll(obj => obj == null || !obj.IsSpawned);

        // quota가 -1이면 동시 생존 최대치에서 현재 맵 전체 생존 수를 빼서 이 Zone에 배정 가능한 수 계산
        if (quota < 0)
        {
            int totalAlive = GetTotalAliveLambs();
            int remaining = currentMap.maxAliveLambs - totalAlive;
            quota = Mathf.Min(remaining, currentMap.maxAliveLambs / Mathf.Max(_zoneBounds.Count, 1));
        }

        int currentCount = _lambsByZone[zoneIndex].Count;
        int toSpawn = quota - currentCount;
        if (toSpawn <= 0) return;

        // 동시 생존 최대치 체크
        int totalAliveNow = GetTotalAliveLambs();
        toSpawn = Mathf.Min(toSpawn, currentMap.maxAliveLambs - totalAliveNow);
        if (toSpawn <= 0) return;

        // 제단 위치 기반으로 어린양 스폰 좌표 생성 (제단 반경 lambSpawnRadius m 이내)
        var positions = GetLambSpawnPositionsNearAltars(zoneIndex, toSpawn);

        var container = GetOrCreateContainer(zoneIndex, "SpawnLamb");

        for (int i = 0; i < positions.Count; i++)
        {
            // 누적 총수 체크 — 99마리 모두 소진되면 더 이상 스폰하지 않음
            var lambType = PickNextLambType();
            if (lambType == null) break;

            var spawnPos = positions[i]; // Y좌표를 0으로 강제하지 않고 원본 유지
            var netObj = SpawnServerObject(currentMap.lambPrefab, spawnPos, Quaternion.identity);
            if (netObj != null)
            {
                var lamb = netObj.GetComponent<LambController>();
                if (lamb != null)
                {
                    lamb.zoneIndex = zoneIndex;
                    lamb.lambType = lambType.Value;
                    lamb.ApplyTypeStats();
                }

                string typeLabel = lambType.Value == LambController.LambType.Innocent ? "Innocent" : "Normal";
                netObj.gameObject.name = $"Lamb_{typeLabel}_Zone{(char)('A' + zoneIndex)}_{currentCount + i + 1:00}";
                // netObj.transform.SetParent(container, true); // NGO: NetworkObject는 비-NetworkObject 부모 불가
                _lambsByZone[zoneIndex].Add(netObj);
            }
        }
    }

    /// <summary>
    /// 해당 Zone의 제단 근처 + ZoneBounds 영역에 어린양 스폰 좌표를 분산 생성합니다.
    /// 절반은 제단 반경 내, 나머지 절반은 ZoneBounds 영역에 배치합니다.
    /// </summary>
    private List<Vector3> GetLambSpawnPositionsNearAltars(int zoneIndex, int count)
    {
        float minDistSqr = 64f; // 8m^2 = 64 (각 어린양 최소 거리 8m)
        var result = new List<Vector3>();

        // 해당 Zone의 제단만 필터 (제단 이름에 Zone 문자 포함 여부)
        char zoneLetter = (char)('A' + zoneIndex);
        var altarPositions = new List<Vector3>();
        foreach (var altar in _spawnedAltars)
        {
            if (altar == null || !altar.IsSpawned) continue;
            // 제단 이름으로 Zone 매칭 (예: Altar_ZoneA_01)
            if (altar.gameObject.name.Contains($"Zone{zoneLetter}"))
                altarPositions.Add(altar.transform.position);
        }

        // ZoneBounds 참조
        ZoneBounds zoneBounds = null;
        _zoneBounds.TryGetValue(zoneIndex, out zoneBounds);

        // 제단과 ZoneBounds가 없으면 빈 목록 반환
        if (altarPositions.Count == 0 && zoneBounds == null)
            return result;

        // 제단이 없으면 ZoneBounds 영역에서만 스폰
        if (altarPositions.Count == 0 && zoneBounds != null)
            return new List<Vector3>(zoneBounds.GetRandomPointsInZone(count, minDistance: 8f));

        // ZoneBounds가 없으면 제단 근처에서만 스폰 (폴백)
        float radius = currentMap != null ? currentMap.lambSpawnRadius : 20f;
        if (zoneBounds == null)
        {
            return GeneratePositionsNearAltars(altarPositions, count, radius, minDistSqr);
        }

        // 하이브리드 분산: 절반은 제단 근처, 절반은 ZoneBounds 영역 대역
        int nearAltarCount = count / 2;
        int zoneBoundsCount = count - nearAltarCount;

        // 1) ZoneBounds 영역 분산 (먼저 배치하여 넓은 공간 확보)
        var zonePositions = zoneBounds.GetRandomPointsInZone(zoneBoundsCount, minDistance: 8f);
        result.AddRange(zonePositions);

        // 2) 제단 근처 분산
        var altarNearPositions = GeneratePositionsNearAltars(altarPositions, nearAltarCount, radius, minDistSqr, result);
        result.AddRange(altarNearPositions);

        return result;
    }

    /// <summary>
    /// 제단 위치의 근처에 최소 거리를 유지하며 랜덤 좌표를 생성합니다.
    /// existingPositions가 주어지면 기존 위치들과의 거리도 고려합니다.
    /// </summary>
    private List<Vector3> GeneratePositionsNearAltars(
        List<Vector3> altarPositions, int count, float radius, float minDistSqr,
        List<Vector3> existingPositions = null)
    {
        var result = new List<Vector3>();
        int maxAttempts = count * 15;
        int attempts = 0;

        while (result.Count < count && attempts < maxAttempts)
        {
            attempts++;

            // 제단을 순서대로 돌아가며 선택 (라운드로빈 분산)
            var center = altarPositions[result.Count % altarPositions.Count];

            // 제단 중심 반경 내 랜덤 좌표 생성 (XZ 평면)
            Vector2 randomCircle = Random.insideUnitCircle * radius;
            Vector3 candidate = new Vector3(
                center.x + randomCircle.x,
                center.y,
                center.z + randomCircle.y
            );

            // 이번 배치 내 기존 위치와 최소 거리 확인
            bool tooClose = false;
            foreach (var existing in result)
            {
                if ((candidate - existing).sqrMagnitude < minDistSqr)
                {
                    tooClose = true;
                    break;
                }
            }

            // 이전 배치(ZoneBounds 분산분)와도 최소 거리 확인
            if (!tooClose && existingPositions != null)
            {
                foreach (var existing in existingPositions)
                {
                    if ((candidate - existing).sqrMagnitude < minDistSqr)
                    {
                        tooClose = true;
                        break;
                    }
                }
            }

            if (!tooClose)
                result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// 다음에 스폰할 어린양 유형을 결정합니다. 누적 총수가 소진되면 null을 반환합니다.
    /// 무구한 양이 남아 있으면 일정 확률(약 25%)로 무구한 양을 선택합니다.
    /// </summary>
    private LambController.LambType? PickNextLambType()
    {
        bool normalAvailable = _spawnedNormalCount < currentMap.totalNormalLambs;
        bool innocentAvailable = _spawnedInnocentCount < currentMap.totalInnocentLambs;

        if (!normalAvailable && !innocentAvailable) return null;
        if (!normalAvailable) { _spawnedInnocentCount++; return LambController.LambType.Innocent; }
        if (!innocentAvailable) { _spawnedNormalCount++; return LambController.LambType.Normal; }

        // 남은 비율에 맞춰 확률적으로 분배
        int normalRemaining = currentMap.totalNormalLambs - _spawnedNormalCount;
        int innocentRemaining = currentMap.totalInnocentLambs - _spawnedInnocentCount;
        float innocentChance = (float)innocentRemaining / (normalRemaining + innocentRemaining);

        if (Random.value < innocentChance)
        {
            _spawnedInnocentCount++;
            return LambController.LambType.Innocent;
        }
        else
        {
            _spawnedNormalCount++;
            return LambController.LambType.Normal;
        }
    }

    /// <summary>
    /// 현재 맵에 살아 있는 총 어린양 수를 반환합니다.
    /// </summary>
    private int GetTotalAliveLambs()
    {
        int total = 0;
        foreach (var kvp in _lambsByZone)
        {
            kvp.Value.RemoveAll(obj => obj == null || !obj.IsSpawned);
            total += kvp.Value.Count;
        }
        return total;
    }

    /// <summary>
    /// LambController.OnNetworkDespawn()에서 호출됩니다.
    /// 양이 죽은 후 일정 시간 뒤에 해당 Zone을 리스폰합니다 (누적 총수가 남아 있을 때만).
    /// </summary>
    public void OnLambDespawned(int zoneIndex, LambController.LambType lambType)
    {
        if (!IsServer) return;
        float delay = currentMap != null ? currentMap.lambRespawnDelay : 3f;
        StartCoroutine(RespawnLambCoroutine(zoneIndex, delay));
    }

    private IEnumerator RespawnLambCoroutine(int zoneIndex, float delay)
    {
        yield return new WaitForSeconds(delay);

        if (!IsServer || currentMap == null) yield break;

        // Phase 1에서만 리스폰 — Phase 2 이상이면 리스폰하지 않음.
        if (PhaseManager.Instance != null && !PhaseManager.Instance.IsCurrentPhase(Phase.Phase1))
            yield break;

        // 누적 총수가 남아 있고, 동시 생존 최대치 미만이면 1마리 보충
        int totalAlive = GetTotalAliveLambs();
        if (totalAlive < currentMap.maxAliveLambs)
        {
            FillLambsInZone(zoneIndex, _lambsByZone.ContainsKey(zoneIndex) ? _lambsByZone[zoneIndex].Count + 1 : 1);
        }
    }

    /// <summary>
    /// 모든 Zone의 어린양을 일괄 Despawn합니다.
    /// </summary>
    public void DespawnAllLambs()
    {
        if (!IsServer) return;
        StartCoroutine(DespawnAllLambsCoroutine());
    }

    private IEnumerator DespawnAllLambsCoroutine()
    {
        if (!IsServer) yield break;

        int count = 0;
        foreach (var kvp in _lambsByZone)
        {
            foreach (var obj in kvp.Value)
            {
                if (obj != null && obj.IsSpawned) 
                {
                    obj.Despawn();
                    count++;
                    if (count % 5 == 0) yield return null;
                }
            }
        }
        _lambsByZone.Clear();
        Debug.Log("[SpawnManager] 어린양 분산 Despawn 완료.");
    }

    // ─────────────────────────────────────────────────────────
    // 내부 헬퍼
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// Zone 4 = 모든 Zone에서 유효한 "All" 인덱스입니다.
    /// </summary>
    public const int ZONE_ALL = 4;

    /// <summary>
    /// [서버 전용] 지정된 Zone의 특정 유형 스폰 포인트 중 하나를 랜덤하게 반환하고 사용 처리합니다.
    /// </summary>
    public SpawnPointMarker GetServerRandomPoint(int zoneIndex, SpawnPointMarker.SpawnType spawnType)
    {
        if (!IsServer) return null;
        return GetRandomPoint(zoneIndex, spawnType);
    }

    /// <summary>
    /// [서버 전용] 현재 접속 중인 악인(Criminal) 플레이어들로부터 가장 먼 스폰 포인트를 찾아 반환합니다.
    /// </summary>
    public SpawnPointMarker GetFurthestPointFromCriminals(int zoneIndex, SpawnPointMarker.SpawnType spawnType)
    {
        if (!IsServer) return null;
        if (_pointsByZoneAndType == null) return null;

        var points = new List<SpawnPointMarker>();
        if (_pointsByZoneAndType.TryGetValue(zoneIndex, out var byType))
        {
            if (byType.TryGetValue(spawnType, out var zonePoints))
                points.AddRange(zonePoints);
        }
        if (zoneIndex != ZONE_ALL && _pointsByZoneAndType.TryGetValue(ZONE_ALL, out var allByType))
        {
            if (allByType.TryGetValue(spawnType, out var allPoints))
                points.AddRange(allPoints);
        }

        if (points.Count == 0) return null;

        // 악인 플레이어 위치 수집
        var criminals = PlayerNetworkController.AllPlayers.Where(p => !p.isGreatSinnerNet.Value && p.gameObject != null).ToList();
        
        // 악인이 없으면 기존처럼 랜덤 스폰
        if (criminals.Count == 0)
        {
            return GetRandomPoint(zoneIndex, spawnType);
        }

        SpawnPointMarker furthestMarker = null;
        float maxMinDist = -1f;

        // 각 스폰 포인트에 대해, 가장 가까운 악인과의 거리를 구하고, 그 거리가 최대가 되는 포인트를 찾음
        foreach (var marker in points)
        {
            float minDistToCriminal = float.MaxValue;
            foreach (var criminal in criminals)
            {
                float dist = Vector3.Distance(marker.transform.position, criminal.transform.position);
                if (dist < minDistToCriminal)
                {
                    minDistToCriminal = dist;
                }
            }

            if (minDistToCriminal > maxMinDist)
            {
                maxMinDist = minDistToCriminal;
                furthestMarker = marker;
            }
        }

        if (furthestMarker != null)
        {
            _usedPoints.Add(furthestMarker);
            return furthestMarker;
        }

        return GetRandomPoint(zoneIndex, spawnType);
    }

    /// <summary>
    /// 특정 Zone + Zone 4(All)의 스폰 포인트를 합쳐 미사용 포인트를 랜덤으로 하나 반환합니다.
    /// </summary>
    private SpawnPointMarker GetRandomPoint(int zoneIndex, SpawnPointMarker.SpawnType spawnType)
    {
        if (_pointsByZoneAndType == null) return null;

        // 지정된 Zone의 포인트 수집
        var points = new List<SpawnPointMarker>();

        if (_pointsByZoneAndType.TryGetValue(zoneIndex, out var byType))
        {
            if (byType.TryGetValue(spawnType, out var zonePoints))
                points.AddRange(zonePoints);
        }

        // Zone 4(All)의 포인트도 추가 (zoneIndex가 이미 4가 아닌 경우에만)
        if (zoneIndex != ZONE_ALL && _pointsByZoneAndType.TryGetValue(ZONE_ALL, out var allByType))
        {
            if (allByType.TryGetValue(spawnType, out var allPoints))
                points.AddRange(allPoints);
        }

        if (points.Count == 0) return null;

        // 아직 사용하지 않은 포인트만 필터
        var available = points.Where(p => !_usedPoints.Contains(p)).ToList();

        // 모두 사용했으면 사용 목록 초기화 (재사용 허용)
        if (available.Count == 0)
        {
            _usedPoints.RemoveWhere(p => points.Contains(p));
            available = points;
        }

        var chosen = available[Random.Range(0, available.Count)];
        _usedPoints.Add(chosen);
        return chosen;
    }

    /// <summary>
    /// 모든 Zone을 통틀어 특정 유형의 포인트 중 랜덤 하나를 반환합니다.
    /// </summary>
    private SpawnPointMarker GetRandomPointFromAll(SpawnPointMarker.SpawnType spawnType)
    {
        var all = GetAllPoints(spawnType);
        if (all == null || all.Count == 0) return null;
        return all[Random.Range(0, all.Count)];
    }

    /// <summary>
    /// 모든 Zone에서 특정 유형의 포인트 전체 목록을 반환합니다.
    /// </summary>
    private List<SpawnPointMarker> GetAllPoints(SpawnPointMarker.SpawnType spawnType)
    {
        var result = new List<SpawnPointMarker>();
        foreach (var byType in _pointsByZoneAndType.Values)
        {
            if (byType.TryGetValue(spawnType, out var points))
                result.AddRange(points);
        }
        return result;
    }

    private Vector3 GetGroundPosition(Vector3 position)
    {
        // 1순위: 맵에 구워진 NavMesh(실제 땅) 중 Walkable 영역(Area 1) 정보를 최우선으로 찾아 스냅합니다.
        // 최종적으로 생성 위치의 근처 유효한 NavMesh 표면(Walkable) 좌표를 찾음 (NavMeshAgent 워프 에러 방지용)
        // 반경이 너무 넓으면(15f) 머리 위 다리나 천장으로 좌표가 튀어서 공중에 뜨는 버그가 발생하므로 2.0f로 축소
        if (NavMesh.SamplePosition(position, out NavMeshHit navHit, 2.0f, NavMesh.AllAreas))
        {
            position = navHit.position;
        }

        // 2순위: NavMesh를 찾지 못한 특수 구역의 경우 기존 레이캐스트 방식을 폴백으로 사용
        Vector3 rayStart = new Vector3(position.x, position.y + 5f, position.z);
        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return hit.point;
        }
        
        // 바닥을 찾지 못했으면 원래 좌표 반환
        return position;
    }

    /// <summary>
    /// 찾은 지면(Y) 위치를 기준으로 NavMesh.SamplePosition을 실행하여, NavMeshAgent가 초기화 시 걸리지 않도록
    /// 배경 위 유효한 네비메시 표면 좌표로 스냅(Snap)합니다.
    /// </summary>
    private Vector3 GetNavMeshPosition(Vector3 position)
    {
        Vector3 groundPos = GetGroundPosition(position);

        // 2) NavMeshAgent가 붙을 수 있도록 정확한 NavMesh 표면을 탐색합니다.
        // 다리 밑으로 뚫고 내려가는 버그를 방지하기 위해 AllAreas를 사용합니다.
        if (NavMesh.SamplePosition(groundPos, out NavMeshHit navHit, 50f, NavMesh.AllAreas))
        {
            return navHit.position; // NavMesh 위 표면 지점 반환
        }

        // 못 찾으면 실외나 벽 밖 등으로 떨어지는 상황이지만, 일단 지면 좌표 반환
        return groundPos;
    }



    /// <summary>
    /// 플레이어 오브젝트를 스폰합니다 (클라이언트 소유).
    /// </summary>
    private NetworkObject SpawnPlayerObject(GameObject prefab, Vector3 position, Quaternion rotation, ulong ownerClientId)
    {
        if (prefab == null)
        {
            Debug.LogError("[SpawnManager] 스폰하려는 프리팹이 null입니다.");
            return null;
        }

        // [핵심 해결책] 스폰 좌표를 유효한 NavMesh 표면으로 보정합니다.
        Vector3 navPos = GetNavMeshPosition(position);

        var instance = Instantiate(prefab, navPos, rotation);

        var netObj = instance.GetComponent<NetworkObject>();

        if (netObj == null)
        {
            Debug.LogError("[SpawnManager] 프리팹에 NetworkObject 컴포넌트가 없습니다!");
            Destroy(instance);
            return null;
        }

        bool isBot = ownerClientId >= 9000;

        if (isBot)
        {
            var pnc = instance.GetComponent<PlayerNetworkController>();
            if (pnc != null)
            {
                pnc.isDummy = true;
                pnc.originalBotId = ownerClientId;
            }
            
            // 봇은 연결된 클라이언트가 없으므로 서버 소유로 스폰합니다.
            netObj.SpawnWithOwnership(NetworkManager.ServerClientId);
            
            if (pnc != null && pnc.movement != null)
            {
                pnc.movement.isBot = true; // 이동 동기화 차단
            }

            // [추가] 더미 플래그로 인해 자동 등록이 취소되었으므로 수동으로 고유 봇 ID(9000번대)를 PlayerManager에 등록
            if (PlayerManager.Instance != null && pnc != null)
            {
                PlayerManager.Instance.RegisterPlayer(ownerClientId, PlayerRole.Criminal, pnc.currentCharacterId.Value, netObj);
            }

            // AIManager에 등록하여 AI 행동 스크립트를 부착시킵니다.
            if (AIManager.Instance != null)
            {
                AIManager.Instance.SwitchPlayerToBot(ownerClientId, pnc);
                
                // 더미(샌드백) 모드라면 봇 AI 스크립트 비활성화
                var botAgent = instance.GetComponent<BotPlayerAgent>();
                if (botAgent != null)
                {
                    botAgent.currentState = BotState.Explore;
                    botAgent.enabled = false; // 테스트용 목각인형이 필요하다면 이렇게 꺼둡니다. (원한다면 켜도 됩니다)
                }
            }
        }
        else
        {
            // 실제 접속한 유저는 PlayerObject로 스폰합니다.
            netObj.SpawnAsPlayerObject(ownerClientId);
        }

        return netObj;
    }

    /// <summary>
    /// 기본 스폰 및 NavMesh 보정 후 NetworkObject 반환 (자동 지상 오프셋 안착 지원)
    /// </summary>
    private NetworkObject SpawnServerObject(GameObject prefab, Vector3 position, Quaternion rotation, float yOffset = 0f, System.Action<GameObject> onBeforeSpawn = null, bool autoYOffset = true)
    {
        if (prefab == null) return null;

        Vector3 spawnPos = GetGroundPosition(position);

        // 1) 지면 기준 위치에 임시 생성
        var instance = Instantiate(prefab, spawnPos, rotation);

        // 2) 렌더러와 콜라이더 크기 불일치 자동 보정 (통과 버그 방지)
        var meshFilter = instance.GetComponentInChildren<MeshFilter>();
        var boxCol = instance.GetComponentInChildren<BoxCollider>();
        
        if (meshFilter != null && meshFilter.sharedMesh != null && boxCol != null)
        {
            // 월드 bounds(AABB)는 회전 시 뚱뚱해지므로 순수 Mesh 로컬 크기를 사용합니다.
            Vector3 meshLocalSize = meshFilter.sharedMesh.bounds.size;
            Vector3 rendererScale = meshFilter.transform.lossyScale;
            Vector3 colScale = boxCol.transform.lossyScale;
            
            Vector3 exactLocalSize = new Vector3(
                (meshLocalSize.x * rendererScale.x) / colScale.x,
                (meshLocalSize.y * rendererScale.y) / colScale.y,
                (meshLocalSize.z * rendererScale.z) / colScale.z
            );
            
            if (exactLocalSize.y > boxCol.size.y + 0.1f || exactLocalSize.y < boxCol.size.y - 0.1f)
            {
                boxCol.size = exactLocalSize;
                Vector3 worldCenter = meshFilter.transform.TransformPoint(meshFilter.sharedMesh.bounds.center);
                boxCol.center = boxCol.transform.InverseTransformPoint(worldCenter);
                Debug.Log($"[SpawnManager] {prefab.name}의 BoxCollider 크기를 완벽 보정. (새 크기: {boxCol.size})");
            }
        }

        // 3) 바닥 안착 완벽 보정 (autoYOffset)
        // 생성된 인스턴스의 실제 렌더러/콜라이더 최하단(bounds.min.y)을 찾아, 이 값이 정확히 spawnPos.y가 되도록 끌어올립니다.
        if (autoYOffset)
        {
            float bottomY = spawnPos.y;
            bool foundBounds = false;

            // 1순위: BoxCollider의 최하단을 지면 기준으로 삼는 것이 파티클/이펙트에 영향을 받지 않아 가장 안전합니다.
            if (boxCol != null)
            {
                // 로컬 좌표계 연산 대신 월드 AABB 기준 최하단(min.y)을 사용하여 회전된 객체에서도 완벽한 바닥 위치를 찾습니다.
                bottomY = boxCol.bounds.min.y;
                foundBounds = true;
            }
            else
            {
                // 2순위: 진짜 모델(Mesh/SkinnedMesh) 렌더러만 탐색 (Particle, Line 등 제외)
                Renderer targetRenderer = null;
                foreach (var r in instance.GetComponentsInChildren<Renderer>())
                {
                    if (!(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer))
                    {
                        targetRenderer = r;
                        break;
                    }
                }
                
                if (targetRenderer != null)
                {
                    bottomY = targetRenderer.bounds.min.y;
                    foundBounds = true;
                }
            }

            if (foundBounds)
            {
                // 파묻혀 있다면 양수(+)로 들어올리고, 공중에 떠 있다면 음수(-)로 내려서 무조건 지면에 밀착!
                float requiredLift = spawnPos.y - bottomY;
                
                // 단, 미세한 오차가 아닌 눈에 띄는 차이일 때만 적용
                if (Mathf.Abs(requiredLift) > 0.01f)
                {
                    instance.transform.position += new Vector3(0, requiredLift, 0);
                    Debug.Log($"[SpawnManager] {prefab.name} 바닥 안착 보정: {requiredLift} 만큼 이동됨. (최종 위치: {instance.transform.position})");
                }
            }
        }

        var netObj = instance.GetComponent<NetworkObject>();
        if (netObj == null)
        {
            Debug.LogError("[SpawnManager] 프리팹에 NetworkObject 컴포넌트가 없습니다!");
            Destroy(instance);
            return null;
        }

        // 스폰 전 초기화 콜백 실행 (ex: zoneIndex 주입)
        onBeforeSpawn?.Invoke(instance);

        netObj.Spawn(); // 서버 소유로 스폰
        return netObj;
    }

    // ─────────────────────────────────────────────────────────
    // 아이템 드랍 스폰 API (다원 작업 대기용 스텁)
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// [도메인 명세 §7] 해당 위치에 드랍 아이템(DroppedItem)을 스폰합니다.
    /// (이 메서드의 실제 구현체는 다원 측에서 추가할 예정입니다)
    /// </summary>
    public void SpawnDroppedItem(Vector3 position, ItemId itemId)
    {
        if (!IsServer) return;
        Debug.Log($"[SpawnManager] SpawnDroppedItem 스텁 호출됨 - 위치: {position}, 아이템: {itemId}");
        // TODO: 다원 측 구현 (DroppedItemController 스폰 및 초기화 로직)
    }
}
