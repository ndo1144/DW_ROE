using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 어린양 프리팹에 부착하는 컴포넌트입니다.
/// 자신이 속한 Zone과 양 타입을 기억하고, 사라질 때 SpawnManager에 리스폰을 요청합니다.
/// </summary>
public class LambController : NetworkBehaviour
{
    /// <summary>
    /// 어린양 종류.
    /// </summary>
    public enum LambType
    {
        Normal,     // 일반 어린양 — 선공, 인식 반경 8m
        Innocent,   // 무구한 어린양 — 도주, 인식 반경 12m, 죄업 5배, 체력 2배
    }

    [Header("양 타입 (SpawnManager가 자동 설정)")]
    public LambType lambType = LambType.Normal;

    /// <summary>
    /// 이 양이 스폰된 Zone 인덱스. SpawnManager가 스폰 시 설정합니다.
    /// </summary>
    [HideInInspector] public int zoneIndex;

    [Header("스탯 (타입에 따라 자동 세팅됨)")]
    public float detectionRadius = 8f;   // 인식 반경
    public float sinMultiplier = 1f;     // 죄업 배율
    public float hpMultiplier = 1f;      // 체력 배율
    public bool isFleeing = false;       // true이면 도주 행동, false이면 선공 행동

    [Header("무구한 양 외형 (sheep_Y)")]
    [Tooltip("무구한 양일 때 교체할 대상 메쉬 오브젝트의 이름입니다. 새 모델의 메쉬 이름(예: Sheep_Mesh)으로 맞춰주세요.")]
    public string bodyMeshName = "body";
    
    [Tooltip("무구한 양일 때 렌더러에 적용할 머티리얼. 비어 있으면 외형을 바꾸지 않음.")]
    public Material innocentBodyMaterial;

    [Header("무구한 양 보물 비콘 (InnocentLambSparkle)")]
    [Tooltip("무구한 양이 Idle 상태일 때 보물처럼 위치를 알리는 반짝임(InnocentLambSparkle) 재생 간격(초). 0 이하이면 비활성화.")]
    public float sparkleInterval = 4f;
    [Tooltip("비콘 한 번을 몇 초만 짧게 재생하고 끊을지(초). 클립이 길어 잔향이 겹치는 것을 막기 위함.")]
    public float sparkleBurstDuration = 1.5f;
    private int _sparkleSourceIdx = -1;

    // 평범한 필드인 lambType은 클라이언트로 동기화되지 않으므로(서버에서만 설정),
    // 클라이언트가 보물 비콘 재생 여부를 판단할 수 있도록 무구한 양 여부를 NetworkVariable로 동기화한다.
    public NetworkVariable<bool> isInnocentSynced = new NetworkVariable<bool>(false);

    private AINetworkController _aiController;
    private Coroutine _sparkleRoutine;

    private void Awake()
    {
        // 양의 히트 범위(콜라이더)를 XYZ 모든 방향으로 2배 확장
        var cols = GetComponentsInChildren<Collider>(true);
        foreach (var col in cols)
        {
            if (col is BoxCollider bc)
            {
                bc.size = new Vector3(bc.size.x * 2f, bc.size.y * 2f, bc.size.z * 2f);
                bc.center = new Vector3(bc.center.x, bc.center.y * 2f, bc.center.z);
                Debug.Log($"[LambController] BoxCollider 히트 범위(XYZ 2배) 확장 완료: {bc.size}");
            }
            else if (col is CapsuleCollider cc)
            {
                cc.height *= 2f;
                cc.radius *= 2f;
                cc.center = new Vector3(cc.center.x, cc.center.y * 2f, cc.center.z);
                Debug.Log($"[LambController] CapsuleCollider 히트 범위(XYZ 2배) 확장 완료: {cc.radius}, {cc.height}");
            }
        }
    }

    /// <summary>
    /// SpawnManager에서 타입 지정 후 호출하여 스탯을 자동 세팅합니다.
    /// </summary>
    public void ApplyTypeStats()
    {
        switch (lambType)
        {
            case LambType.Normal:
                detectionRadius = 8f;
                sinMultiplier = 1f;
                hpMultiplier = 1f;
                isFleeing = false;
                break;

            case LambType.Innocent:
                detectionRadius = 12f;
                sinMultiplier = 5f;
                hpMultiplier = 2f;
                isFleeing = true;
                break;
        }

        // 서버 전용: 클라이언트 동기화 + 그동안 죽어있던 스탯 필드들을 실제로 반영
        if (IsServer && IsSpawned)
        {
            isInnocentSynced.Value = (lambType == LambType.Innocent);

            var ai = GetComponent<AINetworkController>();
            if (ai != null)
            {
                ai.ApplyMaxHPMultiplier(hpMultiplier);                  // 체력 배율 (무구한 양 2배)
                if (ai.perception != null)
                    ai.perception.SetPerceptionRadius(detectionRadius); // 인식 반경 (무구한 양 12m)
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        _aiController = GetComponent<AINetworkController>();

        // 무구한 양 외형(body 머티리얼)은 모든 피어에서 적용되어야 한다. 서버는 스폰 직후 ApplyTypeStats에서
        // 값을 쓰므로, 초기값과 이후 변경을 모두 처리한다.
        isInnocentSynced.OnValueChanged += OnInnocentSyncedChanged;
        if (isInnocentSynced.Value)
            ApplyInnocentVisuals();

        // 보물 비콘은 각 클라이언트(및 호스트)에서 로컬로 재생되어야 모든 플레이어가 들을 수 있다.
        // (AI FSM 사운드는 서버 전용이라 일반 클라이언트엔 들리지 않는 것과 달리, 이 비콘은 모든 피어에서 구동된다.)
        if (sparkleInterval > 0f)
            _sparkleRoutine = StartCoroutine(SparkleBeaconRoutine());
    }

    public override void OnNetworkDespawn()
    {
        isInnocentSynced.OnValueChanged -= OnInnocentSyncedChanged;

        if (_sparkleRoutine != null)
        {
            StopCoroutine(_sparkleRoutine);
            _sparkleRoutine = null;
        }

        if (IsServer)
        {
            // 무구한 양 사망 시 LambSpawnSystem에 알림
            if (lambType == LambType.Innocent && LambSpawnSystem.Instance != null)
                LambSpawnSystem.Instance.OnInnocentLambDied();

            // 기존 SpawnManager 리스폰 콜백 (호환용)
            if (SpawnManager.Instance != null)
                SpawnManager.Instance.OnLambDespawned(zoneIndex, lambType);
        }
    }

    private void OnInnocentSyncedChanged(bool previous, bool current)
    {
        if (current) ApplyInnocentVisuals();
    }

    /// <summary>
    /// 무구한 양 외형 적용: 설정한 메쉬(bodyMeshName)의 머티리얼만 교체한다(애니메이션·스케일 보존).
    /// </summary>
    private void ApplyInnocentVisuals()
    {
        if (innocentBodyMaterial == null) return;

        foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            // 인스펙터에서 지정한 이름(기본값: body)과 일치하는 메쉬를 찾음
            if (smr.gameObject.name == bodyMeshName)
            {
                smr.sharedMaterial = innocentBodyMaterial;
                return;
            }
        }
        
        // 만약 지정한 이름을 못 찾았다면, 차선책으로 첫 번째 SkinnedMeshRenderer를 강제 교체
        var firstSmr = GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (firstSmr != null)
        {
            Debug.LogWarning($"[LambController] '{bodyMeshName}' 이름의 메쉬를 못 찾아서, '{firstSmr.name}' 메쉬의 색을 바꿉니다!");
            firstSmr.sharedMaterial = innocentBodyMaterial;
        }
    }

    /// <summary>
    /// 무구한 양이 살아서 Idle(평화로운 배회) 상태일 때, 보물처럼 위치를 알리는
    /// InnocentLambSparkle을 주기적으로 재생한다. 모든 피어에서 로컬로 재생되며,
    /// SoundManager가 로컬 플레이어 기준 거리 컬링을 적용한다.
    /// </summary>
    private IEnumerator SparkleBeaconRoutine()
    {
        var gap = new WaitForSeconds(sparkleInterval);
        var burst = new WaitForSeconds(sparkleBurstDuration);
        while (true)
        {
            yield return gap;

            if (!isInnocentSynced.Value) continue;  // 무구한 양만 비콘 재생
            // Idle(평화로운 배회) 상태에서만 — 도주/사망 중에는 울리지 않음
            if (_aiController != null && _aiController.currentState.Value != AIState.Idle) continue;

            // 짧게 버스트 재생 후 강제로 끊어 긴 클립이 겹쳐 잔향처럼 이어지는 것을 방지한다.
            if (SoundManager.Instance != null)
                _sparkleSourceIdx = SoundManager.Instance.PlaySFX(SFXType.InnocentLambSparkle, transform.position);

            yield return burst;

            SoundManager.Instance?.StopSFX(_sparkleSourceIdx, SFXType.InnocentLambSparkle);
            _sparkleSourceIdx = -1;
        }
    }
}
