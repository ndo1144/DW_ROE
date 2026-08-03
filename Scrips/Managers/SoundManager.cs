using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance { get; private set; }

    [SerializeField] private SoundConfigSO config;

    // SFX용 다중 AudioSource 풀 (양 떼 다중 공격 시 소리가 끊기지 않도록 풀 크기 넉넉하게 확장)
    private const int SFX_POOL_SIZE = 64;
    private AudioSource[] sfxSources;
    private float[] sfxStartTimes;   // 풀 고갈 시 '가장 오래된' 소스를 찾기 위한 시작 시각
    private float[] sfxBaseVolumes;  // 볼륨 옵션 변경 시 재생 중 소스를 즉시 갱신하기 위한 기준 볼륨(entry.volume)
    private SFXType[] sfxPlayingType; // 각 소스가 현재 재생 중인 SFX 종류 (동시 재생 개수 제한용)

    private Dictionary<SFXType, float> lastPlayTimes = new Dictionary<SFXType, float>();

    // 대상(플레이어)별 마지막 피격 음성 재생 시각. 연타 시 음성이 겹치지 않도록 1초 쿨다운에 사용.
    private Dictionary<ulong, float> _lastDamagedVoiceTimes = new Dictionary<ulong, float>();
    private const float DAMAGED_VOICE_COOLDOWN = 1.0f;

    // 대죄인이 관여한 마지막 전투(공격/피격) 시각. Idle 앰비언트를 전투 중에는 재생하지 않기 위해 사용.
    private float _lastGreatSinnerCombatTime = -999f;
    private const float GREAT_SINNER_COMBAT_WINDOW = 5.0f;

    // OnEnable/OnDisable이 여러 번 호출되어도 정확히 1회만 구독/해제되도록 보장하는 가드
    private bool _eventsSubscribed = false;

    [Header("=== 위치 기반 SFX 컬링 거리 ===")]
    [Tooltip("악인 진영 로컬 플레이어 기준 수평(XZ) 컬링 거리(m). 이보다 멀면 위치 기반 SFX를 재생하지 않음.")]
    [SerializeField] private float criminalCullRadiusXZ = 15f;
    [Tooltip("대죄인 로컬 플레이어 기준 수평(XZ) 컬링 거리(m).")]
    [SerializeField] private float greatSinnerCullRadiusXZ = 30f;
    [Tooltip("수직(Y) 컬링 거리(m). 역할 공통.")]
    [SerializeField] private float cullRadiusY = 5f;

    // 무구한 양 반짝임(InnocentLambSparkle)은 "보물 위치 알림" 비콘이라 일반 사운드보다 훨씬 멀리서 들려야 한다.
    // 약 30m 지점에서 또렷이 들리도록 가청 반경을 넉넉히(40m) 잡고, 그만큼 컬링/감쇠 반경도 함께 늘린다.
    private const float BEACON_HORIZ_RADIUS = 40f; // 비콘 수평 가청/컬링 반경(m)
    private const float BEACON_Y_RADIUS = 15f;     // 비콘 수직 컬링 허용치(m) — 지형 고저차로 통째 컬링되는 것 방지

    [Header("=== 동시 재생 제한 ===")]
    [Tooltip("같은 종류의 SFX를 동시에 최대 몇 개까지 재생할지. 양 떼가 한꺼번에 같은 소리를 내며 겹쳐 클리핑(지직거림)되는 것을 방지.")]
    [SerializeField] private int maxConcurrentPerType = 4;

    // 컬링 기준이 되는 로컬 플레이어(리스너) 캐시
    private PlayerNetworkController _cachedLocalPlayer;

    private void Awake()
    {
        // 씬마다 매니저가 배치되는 구조이므로, 항상 최신 씬의 매니저가 Instance를 차지하게 한다.
        // 이전 인스턴스가 아직 살아 있으면 그 GameObject를 즉시 제거하여 중복 이벤트 구독/중복 재생을 막는다.
        // (제거되는 이전 매니저는 OnDisable에서 자신의 이벤트 구독을 스스로 해제한다.)
        if (Instance != null && Instance != this)
        {
            Destroy(Instance.gameObject);
        }
        Instance = this;

        InitializeAudioSources();
    }

    private void InitializeAudioSources()
    {
        // SFX 풀 초기화
        GameObject sfxRoot = new GameObject("SFX_Pool");
        sfxRoot.transform.SetParent(transform);

        sfxSources = new AudioSource[SFX_POOL_SIZE];
        sfxStartTimes = new float[SFX_POOL_SIZE];
        sfxBaseVolumes = new float[SFX_POOL_SIZE];
        sfxPlayingType = new SFXType[SFX_POOL_SIZE];

        for (int i = 0; i < SFX_POOL_SIZE; i++)
        {
            sfxSources[i] = sfxRoot.AddComponent<AudioSource>();
            sfxSources[i].playOnAwake = false;

            // Unity 기본값(minDistance=1, maxDistance=500, Logarithmic)은 15~30m 컬링 반경 안에서
            // 거의 감쇠가 없어, 컬링만 안 됐다 뿐이지 "가까운 소리"와 "먼 소리"가 구분이 안 되고
            // 심지어 시야 밖(안 보이는) 개체의 소리도 거의 풀볼륨으로 들려 "허공에서 들린다"는 인상을 준다.
            // 악인(3D) 리스너 기준으로 자연스럽게 페이드되도록 명시적으로 설정한다.
            // (대죄인 리스너는 IsLocalListenerGreatSinner()에 의해 2D로 재생되므로 이 설정과 무관하다.)
            sfxSources[i].minDistance = 2f;
            sfxSources[i].maxDistance = 15f;
            sfxSources[i].rolloffMode = AudioRolloffMode.Linear;

            // SFX를 믹서 그룹으로 라우팅하여 구역별 리버브(AcousticZoneManager 스냅샷)가 적용되게 한다.
            if (config != null && config.sfxMixerGroup != null)
                sfxSources[i].outputAudioMixerGroup = config.sfxMixerGroup;
        }
    }

    private void Start()
    {
        if (config != null)
        {
            config.Initialize();
        }
    }

    private void OnEnable() => SubscribeEvents();
    private void OnDisable() => UnsubscribeEvents();

    private void OnDestroy()
    {
        // 자신이 현재 Instance일 때만 비운다(새 매니저가 이미 덮어쓴 경우는 건드리지 않음).
        if (Instance == this) Instance = null;
    }

    private float _proximityCheckTimer = 0f;

    private void Update()
    {
        // 1초에 약 2번(0.5초 간격)만 계산하도록 최적화
        _proximityCheckTimer += Time.deltaTime;
        if (_proximityCheckTimer >= 0.5f)
        {
            _proximityCheckTimer = 0f;
            CheckGreatSinnerProximity();
        }
    }

    private void CheckGreatSinnerProximity()
    {
        if (PlayerNetworkController.AllPlayers == null) return;
        if (Unity.Netcode.NetworkManager.Singleton == null || !Unity.Netcode.NetworkManager.Singleton.IsConnectedClient) return;

        PlayerNetworkController localPlayer = null;
        PlayerNetworkController greatSinner = null;

        foreach (var p in PlayerNetworkController.AllPlayers)
        {
            if (p.IsOwner) localPlayer = p;
            if (p.isGreatSinner) greatSinner = p;
        }

        // 로컬 플레이어가 악인(도망자)이고, 대죄인이 맵에 살아있을 때만 동작
        if (localPlayer != null && !localPlayer.isGreatSinner && greatSinner != null)
        {
            float dist = Vector3.Distance(localPlayer.transform.position, greatSinner.transform.position);

            // 30m 이내로 접근하면 사운드 재생 (사운드의 쿨다운은 SoundConfigSO에서 제어됨)
            if (dist <= 30f)
            {
                PlaySFX(SFXType.GreatSinnerProximity);
            }
        }

        // 대죄인 Idle 앰비언트: 전투 중이 아닐 때만 재생(전투 여부는 _lastGreatSinnerCombatTime로 판정).
        // 대죄인 본인 클라이언트에서는 2D로, 다른 클라이언트에서는 대죄인 위치 기준 3D로 들린다(PlaySFX 내부 분기).
        // 재생 빈도 자체는 SoundConfigSO(type 24)의 cooldown 필드가 제어한다.
        if (greatSinner != null && Time.time - _lastGreatSinnerCombatTime >= GREAT_SINNER_COMBAT_WINDOW)
        {
            PlaySFX(SFXType.GreatSinnerIdle, greatSinner.transform.position);
        }
    }

    private void SubscribeEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;

        // 다원 매니저 이벤트 (Phase, AI, Altar 등)
        EventBus.OnPhaseTransition += HandlePhaseTransition;
        EventBus.OnZoneSeal += HandleZoneSeal;
        EventBus.OnZoneTimeUpdate += HandleZoneTimeUpdate;
        // 헌납음(AltarDonate*)은 AltarController의 ClientRpc가 호스트/클라이언트 모두에게 직접 재생/정지하므로
        // 여기서 EventBus로 별도 재생하지 않는다(이중 재생 및 호스트 세션 의존 무음 방지).
        EventBus.OnMatchEndWithStats += HandleMatchEndWithStats;

        EventBus.OnLambKilled += HandleLambKilled;
        EventBus.OnInnocentLambSpotted += HandleInnocentLambSpotted;
        EventBus.OnMonsterKilled += HandleMonsterKilled;
        EventBus.OnDamageDealt += HandleDamageDealt;
    }

    private void UnsubscribeEvents()
    {
        if (!_eventsSubscribed) return;
        _eventsSubscribed = false;

        EventBus.OnPhaseTransition -= HandlePhaseTransition;
        EventBus.OnZoneSeal -= HandleZoneSeal;
        EventBus.OnZoneTimeUpdate -= HandleZoneTimeUpdate;
        EventBus.OnMatchEndWithStats -= HandleMatchEndWithStats;

        EventBus.OnLambKilled -= HandleLambKilled;
        EventBus.OnInnocentLambSpotted -= HandleInnocentLambSpotted;
        EventBus.OnMonsterKilled -= HandleMonsterKilled;
        EventBus.OnDamageDealt -= HandleDamageDealt;
    }

    // 봉인 예정 구역 안에 로컬 플레이어가 머무는 동안 경고음을 반복 재생하기 위한 마지막 재생 시각.
    private float _lastZoneSealWarningTime = -999f;
    private const float ZONE_SEAL_WARNING_INTERVAL = 3.5f;

    // ── EventBus 핸들러 (구독 해제가 가능하도록 람다 대신 명명 메서드로 정의) ──
    private void HandlePhaseTransition(Phase from, Phase to) => PlaySFX(SFXType.PhaseTransition);

    private void HandleZoneSeal(int zoneIdx)
    {
        PlaySFX(SFXType.ZoneSealed);
        _lastZoneSealWarningTime = -999f; // 다음 구역에서 즉시 경고 가능하도록 리셋
    }

    private void HandleZoneTimeUpdate(float remainingTime)
    {
        // 30초 이하로 남았을 때, 봉인 예정 구역 안에 있는 로컬 플레이어에게만 3.5초 간격으로 경고음 재생
        if (remainingTime <= 30f && remainingTime > 0f && IsLocalPlayerInSealingZone())
        {
            if (Time.time - _lastZoneSealWarningTime >= ZONE_SEAL_WARNING_INTERVAL)
            {
                _lastZoneSealWarningTime = Time.time;
                PlaySFX(SFXType.ZoneSealWarning);
            }
        }
    }

    // 현재 봉인 카운트다운이 진행 중인 구역(PhaseManager.CurrentZoneIndex) 내부에 로컬 플레이어가 있는지 확인한다.
    private bool IsLocalPlayerInSealingZone()
    {
        var local = GetLocalPlayer();
        if (local == null || PhaseManager.Instance == null) return false;

        int zoneIdx = PhaseManager.Instance.CurrentZoneIndex;
        foreach (var zone in ZoneBounds.AllZones)
        {
            if (zone.zoneIndex == zoneIdx)
                return zone.Contains(local.transform.position);
        }
        return false;
    }
    private void HandleMatchEndWithStats(int result, MatchStatEntry[] stats) => PlaySFX(SFXType.MatchEnd);
    private void HandleLambKilled(NetworkObject lamb, NetworkObject killer) => PlaySFX(SFXType.LambKill, lamb != null ? lamb.transform.position : (Vector3?)null);
    private void HandleInnocentLambSpotted(Vector3 pos) => PlaySFX(SFXType.InnocentLambKill, pos);
    private void HandleMonsterKilled(NetworkObject monster, NetworkObject killer) => PlaySFX(SFXType.MonsterKill, monster != null ? monster.transform.position : (Vector3?)null);

    private void HandleDamageDealt(NetworkObject attacker, NetworkObject target, int rawDamage)
    {
        // 대죄인이 공격하거나 피격당했다면 전투 중으로 간주(Idle 앰비언트 재생 억제용).
        bool attackerIsGreatSinner = attacker != null && attacker.TryGetComponent<PlayerNetworkController>(out var attackerGsCheck) && attackerGsCheck.isGreatSinner;
        bool targetIsGreatSinner = target != null && target.TryGetComponent<PlayerNetworkController>(out var targetGsCheck) && targetGsCheck.isGreatSinner;
        if (attackerIsGreatSinner || targetIsGreatSinner)
        {
            _lastGreatSinnerCombatTime = Time.time;
        }

        // 1. 피격당한 주체가 플레이어인 경우 피격음 재생 (모든 클라이언트가 듣도록 3D 재생)
        if (target != null && target.TryGetComponent<PlayerNetworkController>(out var targetPnc))
        {
            // 모든 캐릭터의 피격 음성은 연타 시 겹쳐 들리지 않도록 대상별 1초 쿨다운을 둔다.
            if (CanPlayDamagedVoice(target.NetworkObjectId))
            {
                if (targetPnc.isGreatSinner)
                {
                    // 대죄인 피격 히트피드백: HitFeedback.wav 재생
                    PlaySFX(SFXType.GreatSinnerHitFeedback, target.transform.position);
                }
                else
                {
                    // CharacterStatSO에 등록된 고유 피격음 재생 (배열 중 랜덤)
                    // 볼륨 1.7783 = +5dB (20*log10(1.7783) ≈ 5) 부스트
                    if (targetPnc.statSO != null)
                    {
                        var clip = targetPnc.statSO.GetRandomDamagedSound();
                        if (clip != null) PlaySFX(clip, 1.7783f, target.transform.position);
                    }
                }
            }
        }

        // 2. 타격 임팩트음(공격자 본인 피드백, 2D로 재생 → 대상이 멀어도 컬링/거리감쇠 없이 항상 들린다)
        //    - 공격자가 플레이어(악인/대죄인)면 본인에게 재생한다. (피격자는 위의 피격 음성으로 별도 피드백 → 중복 방지)
        //    - 공격자가 몬스터이거나 대죄인인 경우, 그 공격을 맞은 악인 본인에게도 "맞았다"는 확인음을 들려준다.
        PlayerNetworkController atkPncForFeedback = null;
        bool attackerIsMonster = attacker != null && attacker.TryGetComponent<AINetworkController>(out _);
        bool attackerIsGreatSinnerAttacker = attacker != null && attacker.TryGetComponent(out atkPncForFeedback) && atkPncForFeedback.isGreatSinner;

        if (attacker != null && attacker.IsOwner && atkPncForFeedback != null && target != null)
        {
            // 공격 종류를 캐릭터로 구분:
            //  - 대죄인: 전용 히트피드백(HitFeedback.wav)
            //  - 원거리 캐릭터(에반젤린/페레슈테): HitFeedback_UI
            //  - 그 외 근접 악인: MeleeHit_Flesh
            // (스킬 히트도 이 경로로 소리가 남 → 스킬 무음 문제 해결)
            SFXType hitSfx;
            if (attackerIsGreatSinnerAttacker)
            {
                hitSfx = SFXType.GreatSinnerHitFeedback;
            }
            else
            {
                bool isRangedAttacker = !atkPncForFeedback.IsMeleeCharacter(atkPncForFeedback.currentCharacterId.Value);
                hitSfx = isRangedAttacker ? SFXType.HitFeedback_UI : SFXType.MeleeHit_Flesh;
            }
            PlaySFX(hitSfx);
        }

        // 몬스터 또는 대죄인에게 맞은 악인 본인에게도 동일한 히트피드백을 들려준다.
        // (몬스터 공격도 MeleeHit_Flesh가 아니라 HitFeedback 계열 사운드를 그대로 사용한다.)
        if (target != null && target.IsOwner && target.TryGetComponent<PlayerNetworkController>(out var tgtPncForFeedback)
            && !tgtPncForFeedback.isGreatSinner && (attackerIsMonster || attackerIsGreatSinnerAttacker))
        {
            SFXType hitSfx = attackerIsGreatSinnerAttacker ? SFXType.GreatSinnerHitFeedback : SFXType.HitFeedback_UI;
            PlaySFX(hitSfx);
        }
    }

    public SoundConfigSO GetConfig() => config;

    // 대상별 피격 음성 쿨다운(1초) 통과 여부를 판정하고, 통과 시 마지막 재생 시각을 갱신한다.
    private bool CanPlayDamagedVoice(ulong targetId)
    {
        if (_lastDamagedVoiceTimes.TryGetValue(targetId, out float last))
        {
            if (Time.time - last < DAMAGED_VOICE_COOLDOWN) return false;
        }
        _lastDamagedVoiceTimes[targetId] = Time.time;
        return true;
    }

    /// <summary>
    /// 클립을 지정한 Transform(예: 날아가는 투사체)의 위치에서 3D로 재생하되, 대상을 따라 이동합니다.
    /// 대상(투사체)이 도중에 파괴되어도 임시 오디오 소스는 파괴되지 않고 마지막 위치에서 클립을 끝까지 재생한 뒤 스스로 제거됩니다.
    /// (부모로 붙이지 않으므로 Destroy(target) 시에도 소리가 잘리지 않습니다.)
    /// </summary>
    public void PlaySFXFollow(AudioClip clip, Transform target, float volume = 1f)
    {
        if (clip == null) return;

        // 위치 기반 사운드는 로컬 플레이어 기준 컬링 박스 밖이면 재생하지 않는다
        if (target != null && ShouldCullByDistance(target.position)) return;

        GameObject go = new GameObject("SFX_Follow");
        if (target != null) go.transform.position = target.position;

        AudioSource src = go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        if (config != null && config.sfxMixerGroup != null)
            src.outputAudioMixerGroup = config.sfxMixerGroup;

        bool isGreatSinner = IsLocalListenerGreatSinner();
        src.rolloffMode = AudioRolloffMode.Linear;
        // 대죄인은 3인칭 카메라가 멀리 있으므로 가청 거리를 늘려 3D 사운드가 잘 들리게 보정
        src.minDistance = isGreatSinner ? 10f : 2f;
        src.maxDistance = isGreatSinner ? 30f : 15f;
        src.spatialBlend = 1f; // 항상 3D 위치 기반 사용
        src.clip = clip;
        float global = config != null ? config.sfxGain * config.sfxVolume * config.masterVolume : 1f;
        src.volume = Mathf.Clamp01(volume * global);
        src.pitch = 1.0f;
        src.Play();

        StartCoroutine(FollowAndDestroyRoutine(go, src, target, clip.length + 0.5f));
    }

    private System.Collections.IEnumerator FollowAndDestroyRoutine(GameObject go, AudioSource src, Transform target, float maxLife)
    {
        float t = 0f;
        while (go != null && t < maxLife)
        {
            // 대상이 살아있는 동안만 따라가고, 파괴되면(null) 마지막 위치에 남아 끝까지 재생.
            if (target != null) go.transform.position = target.position;
            // 클립 재생이 끝나면 즉시 정리 (첫 프레임 isPlaying 판정 오류 방지로 약간의 유예)
            if (t > 0.1f && (src == null || !src.isPlaying)) break;
            t += Time.deltaTime;
            yield return null;
        }
        if (go != null) Destroy(go);
    }

    /// <summary>
    /// AudioClip을 직접 재생합니다. (CharacterStatSO 등에서 할당된 사운드용)
    /// </summary>
    public int PlaySFX(AudioClip clip, float volume = 1f, Vector3? position = null)
    {
        if (clip == null) return -1;

        if (position.HasValue && ShouldCullByDistance(position.Value)) return -1;

        int idx = GetAvailableSFXIndex();
        if (idx == -1) return -1;

        AudioSource source = sfxSources[idx];
        source.clip = clip;

        sfxBaseVolumes[idx] = volume;
        source.volume = Mathf.Clamp01(volume * config.sfxGain * config.sfxVolume * config.masterVolume);
        source.pitch = 1.0f;
        source.priority = 128;
        source.loop = false; // 풀 소스 재사용 시 이전 루프 상태가 남지 않게 리셋

        if (position.HasValue)
        {
            source.transform.position = position.Value;
            bool isGreatSinner = IsLocalListenerGreatSinner();
            source.spatialBlend = 1f; // 항상 3D 위치 기반 사용
            source.minDistance = isGreatSinner ? 10f : 2f;
            source.maxDistance = isGreatSinner ? 30f : 15f;
        }
        else
        {
            source.spatialBlend = 0f;
        }

        sfxStartTimes[idx] = Time.time;
        sfxPlayingType[idx] = SFXType.None;
        source.Play();
        return idx;
    }

    /// <summary>
    /// 지정된 타입의 SFX를 재생합니다.
    /// position을 전달하면 3D 위치 기반 사운드로 재생됩니다.
    /// 재생에 사용한 풀 소스 인덱스를 반환합니다(실패 시 -1). StopSFX로 도중에 끊을 때 사용합니다.
    /// </summary>
    public int PlaySFX(SFXType type, Vector3? position = null, bool loop = false)
    {
        if (config == null || !config.TryGetSFX(type, out SFXEntry entry)) return -1;
        if (entry.clips == null || entry.clips.Length == 0) return -1;

        // 보물 비콘(무구한 양 반짝임)은 멀리서도 위치를 알려야 하므로 더 넓은 반경(약 30m 이상)까지 들리게 한다.
        bool isBeacon = (type == SFXType.InnocentLambSparkle);

        // 위치 기반 사운드는 로컬 플레이어 기준 컬링 박스 밖이면 재생하지 않는다(동시 보이스 절약).
        if (position.HasValue && ShouldCullByDistance(position.Value,
                isBeacon ? BEACON_HORIZ_RADIUS : 0f,
                isBeacon ? BEACON_Y_RADIUS : 0f)) return -1;

        // 같은 종류 사운드가 이미 상한만큼 재생 중이면 추가 재생하지 않는다(겹쳐 합쳐지며 클리핑되는 것 방지).
        if (maxConcurrentPerType > 0 && CountPlayingOfType(type) >= maxConcurrentPerType) return -1;

        // 쿨다운 체크
        if (entry.cooldown > 0f)
        {
            if (lastPlayTimes.TryGetValue(type, out float lastTime))
            {
                if (Time.time - lastTime < entry.cooldown) return -1;
            }
            lastPlayTimes[type] = Time.time;
        }

        int idx = GetAvailableSFXIndex();
        if (idx == -1) return -1; // 풀이 꽉 차서 기존 사운드를 보호해야 하는 경우 재생 취소

        AudioSource source = sfxSources[idx];

        AudioClip clip = entry.clips[Random.Range(0, entry.clips.Length)];
        source.clip = clip;

        // 기준 볼륨을 기록해 두어 옵션 변경 시 RefreshSFXVolumes()로 즉시 갱신할 수 있게 한다.
        sfxBaseVolumes[idx] = entry.volume;
        // sfxGain으로 작은 소리를 일괄 부스트하되, 최종 음량은 1.0(풀스케일)로 제한.
        source.volume = Mathf.Clamp01(entry.volume * config.sfxGain * config.sfxVolume * config.masterVolume);

        // 자체 피치(basePitch)는 '반음(semitone)' 단위 배율로 적용한다: 2^(basePitch/12).
        // (가산 방식이면 basePitch가 -1 미만일 때 피치가 0 이하로 내려가 0.1로 클램프→무음/초저속이 되던 버그 수정)
        // 0=원음, 음수일수록 낮고 굵게(예: -12 = 한 옥타브 아래). 랜덤 변형과 곱해지고, 안전을 위해 0.1~3으로 클램프.
        float pitchMul = Mathf.Pow(2f, entry.basePitch / 12f);
        source.pitch = Mathf.Clamp((1.0f + Random.Range(-entry.pitchVariation, entry.pitchVariation)) * pitchMul, 0.1f, 3f);

        // 보이스 우선순위: Idle/배경류는 낮게 줘서 보이스 부족 시 먼저 가상화(무음)되게 한다.
        source.priority = GetPriority(type);

        if (position.HasValue)
        {
            source.transform.position = position.Value;
            bool isGreatSinner = IsLocalListenerGreatSinner();
            source.spatialBlend = 1f; // 항상 3D 위치 기반 사용
            if (isBeacon)
            {
                // 비콘은 역할과 무관하게 약 30m 지점에서도 또렷이 들리도록 넓게(가까이서 5m 만렙, 40m에서 소멸) 잡는다.
                source.minDistance = 5f;
                source.maxDistance = BEACON_HORIZ_RADIUS;
            }
            else
            {
                source.minDistance = isGreatSinner ? 10f : 2f;
                source.maxDistance = isGreatSinner ? 30f : 15f;
            }
        }
        else
        {
            source.spatialBlend = 0f; // 2D (UI음 등)
        }

        // 지속형 사운드(예: 8초간 이어지는 헌납음)는 loop=true로 재생하고, StopSFX(type)로 끊는다.
        // 풀 소스는 재사용되므로, 루프가 아닐 때는 반드시 false로 되돌려 이전 루프 상태가 남지 않게 한다.
        source.loop = loop;

        sfxStartTimes[idx] = Time.time;
        sfxPlayingType[idx] = type;
        source.Play();
        return idx;
    }

    /// <summary>
    /// 지정된 타입의 SFX를 재생한 뒤, fadeOutDuration에 걸쳐 볼륨을 0으로 서서히 낮추고 멈춥니다.
    /// 클립 원본이 길어도 짧게 페이드아웃되며 끊기는 효과(예: 엔젤 스킬3 착지음)를 낼 때 사용합니다.
    /// </summary>
    public void PlaySFXWithFadeOut(SFXType type, Vector3? position, float fadeOutDuration)
    {
        int idx = PlaySFX(type, position);
        if (idx == -1) return;

        StartCoroutine(FadeOutSFXRoutine(idx, sfxPlayingType[idx], fadeOutDuration));
    }

    private System.Collections.IEnumerator FadeOutSFXRoutine(int sourceIndex, SFXType expectedType, float fadeOutDuration)
    {
        AudioSource source = sfxSources[sourceIndex];
        if (source == null) yield break;

        float startVolume = source.volume;
        float t = 0f;
        while (t < fadeOutDuration)
        {
            // 소스가 재생 종료되었거나(자연 종료) 다른 소리로 재활용되었다면 건드리지 않고 종료
            if (source == null || !source.isPlaying || sfxPlayingType[sourceIndex] != expectedType) yield break;

            t += Time.deltaTime;
            source.volume = Mathf.Lerp(startVolume, 0f, t / fadeOutDuration);
            yield return null;
        }

        if (source != null && sfxPlayingType[sourceIndex] == expectedType)
        {
            source.Stop();
            sfxPlayingType[sourceIndex] = SFXType.None;
        }
    }

    /// <summary>
    /// PlaySFX가 반환한 소스 인덱스를 받아, 그 소스가 아직 같은 종류(expectedType)를 재생 중이면 즉시 멈춥니다.
    /// 긴 원샷 클립(예: InnocentLambSparkle 비콘)을 짧게 버스트 재생하고 끊어 잔향이 겹치는 것을 막을 때 사용합니다.
    /// 인덱스가 이미 다른 소리로 재활용되었다면(타입 불일치) 멈추지 않습니다(다른 소리 보호).
    /// </summary>
    public void StopSFX(int sourceIndex, SFXType expectedType)
    {
        if (sfxSources == null || sourceIndex < 0 || sourceIndex >= SFX_POOL_SIZE) return;
        if (sfxSources[sourceIndex] != null && sfxSources[sourceIndex].isPlaying
            && sfxPlayingType[sourceIndex] == expectedType)
        {
            sfxSources[sourceIndex].Stop();
            sfxPlayingType[sourceIndex] = SFXType.None;
        }
    }

    /// <summary>
    /// 특정 SFXType으로 재생 중인 모든 소리를 즉시 멈춥니다.
    /// (예: 헌납이 취소되었을 때 루핑되는 헌납 소리를 멈출 때 사용)
    /// </summary>
    public void StopSFX(SFXType type)
    {
        if (sfxSources == null) return;
        for (int i = 0; i < SFX_POOL_SIZE; i++)
        {
            if (sfxSources[i] != null && sfxSources[i].isPlaying && sfxPlayingType[i] == type)
            {
                sfxSources[i].Stop();
                sfxPlayingType[i] = SFXType.None;
            }
        }
    }

    // 현재 해당 종류를 재생 중인 소스 개수를 센다. (동시 재생 상한 판정용)
    private int CountPlayingOfType(SFXType type)
    {
        int count = 0;
        for (int i = 0; i < SFX_POOL_SIZE; i++)
        {
            if (sfxSources[i].isPlaying && sfxPlayingType[i] == type) count++;
        }
        return count;
    }

    private int GetAvailableSFXIndex()
    {
        // 1) 비어 있는(재생이 끝난) 소스를 우선 사용
        for (int i = 0; i < SFX_POOL_SIZE; i++)
        {
            if (!sfxSources[i].isPlaying) return i;
        }

        // 2) 모두 재생 중이면 '가장 먼저 시작된' 소스를 찾는다.
        int oldest = 0;
        for (int i = 1; i < SFX_POOL_SIZE; i++)
        {
            if (sfxStartTimes[i] < sfxStartTimes[oldest]) oldest = i;
        }

        // 3) 만약 가장 오래된 소스조차 0.5초 이내에 재생된 것이라면, 
        // 뺏어오지 않고 그냥 -1을 반환하여 재생을 포기한다. (기존 사운드 끊김/지지직 방지)
        if (Time.time - sfxStartTimes[oldest] < 0.5f)
        {
            return -1;
        }

        return oldest;
    }

    // 로컬 플레이어(리스너)를 캐시. 파괴되면 Unity의 == 오버로드로 null 처리되어 자동 재탐색.
    private PlayerNetworkController GetLocalPlayer()
    {
        if (_cachedLocalPlayer != null) return _cachedLocalPlayer;

        if (PlayerNetworkController.AllPlayers != null)
        {
            foreach (var p in PlayerNetworkController.AllPlayers)
            {
                if (p != null && p.IsOwner)
                {
                    _cachedLocalPlayer = p;
                    return p;
                }
            }
        }
        return null;
    }

    // [진단] 들리는 소리(발소리/히트피드백/BGM)는 전부 2D(spatialBlend=0)라 리스너 거리와 무관하게 재생된다.
    // 안 들리던 소리(공격 스윙/양 피격·사망/오브젝트 파괴)는 전부 3D(spatialBlend=1, position 지정)였다.
    // AudioListener는 카메라에 있는데 대죄인은 3인칭 카메라가 캐릭터에서 멀리 떨어져 있어(실외 기본거리 ×2.8, 1.7배 스케일)
    // 캐릭터 주변 3D 사운드가 거리 감쇠로 거의 안 들렸던 것이 원인이다.
    // → 로컬 리스너가 대죄인이면 풀 사운드를 2D로 재생해(들리는 소리들과 동일 방식) 확실히 들리게 한다. 악인은 3D 유지.
    private bool IsLocalListenerGreatSinner()
    {
        var local = GetLocalPlayer();
        if (local == null) return false;
        
        // 3페이즈에서는 모든 악인이 대죄인화되어 3인칭 뷰를 사용하므로, 
        // 오디오 리스너(카메라)가 캐릭터와 멀어져 3D 사운드가 작게 들리는 것을 방지하기 위해 2D로 처리합니다.
        bool isPhase3 = PhaseManager.Instance != null && PhaseManager.Instance.CurrentPhase == Phase.Phase3;
        return local.isGreatSinner || isPhase3;
    }

    /// <summary>
    /// 위치 기반 SFX가 로컬 플레이어 기준 컬링 박스(역할별 수평 XZ + 공통 수직 Y) 밖이면 true.
    /// 컬링되면 재생을 생략하여 동시 보이스 수를 절약한다.
    /// </summary>
    private bool ShouldCullByDistance(Vector3 soundPos, float minHorizRadius = 0f, float minYRadius = 0f)
    {
        var local = GetLocalPlayer();
        if (local == null) return false; // 로컬 플레이어를 못 찾으면 컬링하지 않음(안전)

        Vector3 listenerPos = local.transform.position;

        // 수직(Y) 컬링. 대죄인은 1.7배 스케일 + 콜라이더 center=0이라 transform.position.y가 발밑에서 약 1.7m 위에 있어,
        // 지면(양/프롭)의 소리와 Y차가 커진다. 씬의 cullRadiusY가 매우 작으면(예: 2) 근처 소리가 통째로 컬링되므로,
        // 대죄인은 스케일을 감안해 최소 6m의 넉넉한 Y 허용치를 보장한다.
        // minYRadius: 특정 사운드(예: 보물 비콘)가 더 넓은 수직 허용치를 요구할 때 하한을 올린다.
        float yRadius = Mathf.Max(local.isGreatSinner ? Mathf.Max(cullRadiusY, 6f) : cullRadiusY, minYRadius);
        if (Mathf.Abs(soundPos.y - listenerPos.y) > yRadius) return true;

        // 수평(XZ)은 역할별 (악인 15m / 대죄인 30m 기본). minHorizRadius로 특정 사운드의 반경 하한을 올릴 수 있다.
        float horizRadius = Mathf.Max(local.isGreatSinner ? greatSinnerCullRadiusXZ : criminalCullRadiusXZ, minHorizRadius);
        float dx = soundPos.x - listenerPos.x;
        float dz = soundPos.z - listenerPos.z;
        if (dx * dx + dz * dz > horizRadius * horizRadius) return true;

        return false;
    }

    // 배경/Idle류는 보이스 부족 시 가장 먼저 가상화되도록 낮은 우선순위(큰 값)를 준다. (0=최고, 256=최저)
    private static int GetPriority(SFXType type)
    {
        switch (type)
        {
            case SFXType.LambIdle:
            case SFXType.GreatSinnerIdle:
            case SFXType.Monster1_Idle:
            case SFXType.Monster2_Idle:
            case SFXType.Monster3_Idle:
            case SFXType.InnocentLambSparkle:
                return 220;
            default:
                return 128; // Unity 기본값
        }
    }

    /// <summary>
    /// 옵션 메뉴 등에서 볼륨 설정을 변경한 직후 호출하면, 현재 재생 중인 SFX 음량도 즉시 갱신됩니다.
    /// (BGM 음량은 BGMStateManager가 매 프레임 자체적으로 반영합니다.)
    /// </summary>
    public void RefreshSFXVolumes()
    {
        if (config == null || sfxSources == null) return;

        float global = config.sfxGain * config.sfxVolume * config.masterVolume;
        for (int i = 0; i < SFX_POOL_SIZE; i++)
        {
            if (sfxSources[i] != null && sfxSources[i].isPlaying)
                sfxSources[i].volume = Mathf.Clamp01(sfxBaseVolumes[i] * global);
        }
    }
}
