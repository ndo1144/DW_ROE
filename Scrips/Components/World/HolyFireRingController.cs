using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

[RequireComponent(typeof(LineRenderer))]
public class HolyFireRingController : NetworkBehaviour
{
    public static HolyFireRingController Instance { get; private set; }

    public NetworkVariable<float> currentRadius = new NetworkVariable<float>(50f);
    public NetworkVariable<Vector3> centerPoint = new NetworkVariable<Vector3>(Vector3.zero);
    public NetworkVariable<bool> isZoneSealFire = new NetworkVariable<bool>(false);
    public NetworkVariable<Vector3> zoneExtents = new NetworkVariable<Vector3>(Vector3.zero); // 2페이즈 제한구역 크기

    [Header("VFX")]
    [Tooltip("성화 파티클 시스템")]
    public ParticleSystem fireParticleSystem;
    
    [SerializeField] private HolyFireConfigSO config;

    private float erosionMultiplier = 1.0f;
    private float erosionTimer = 0f;
    private Dictionary<ulong, float> _lastDamageTime = new Dictionary<ulong, float>();

    private void Awake()
    {
        // 최신 인스턴스로 갱신 (3페이즈용은 마지막에 덮어씌워짐)
        Instance = this;
    }

    private void FixedUpdate()
    {
        if (!IsServer || config == null) return;

        if (isZoneSealFire.Value) return; // 2페이즈 성화는 침식 로직 제외

        // 3페이즈: 시간이 지남에 따라 플레이어를 향해 조여오는 링
        erosionTimer += Time.fixedDeltaTime;
        if (erosionTimer >= 10.0f)
        {
            currentRadius.Value -= config.ErosionAmountPer10s * erosionMultiplier;
            if (currentRadius.Value < 5f) currentRadius.Value = 5f; // 최소 반경 유지
            erosionTimer = 0f;
            Client_UpdateVisualsClientRpc(currentRadius.Value);
        }

        // 3페이즈: 링에 닿거나 바깥으로 넘어가면 데미지 입음
        if (PlayerNetworkController.AllPlayers != null)
        {
            foreach (var player in PlayerNetworkController.AllPlayers)
            {
                if (player != null && player.gameObject.activeInHierarchy)
                {
                    // 수직 위치를 무시하고 2D 평면상에서 거리 계산
                    Vector2 playerPosXZ = new Vector2(player.transform.position.x, player.transform.position.z);
                    Vector2 centerPosXZ = new Vector2(centerPoint.Value.x, centerPoint.Value.z);
                    float dist = Vector2.Distance(playerPosXZ, centerPosXZ);

                    // 링 두께(약 1.5m)에 닿거나 바깥으로 나가면 데미지
                    if (dist >= currentRadius.Value - 1.5f)
                    {
                        ulong clientId = player.OwnerClientId;
                        if (!_lastDamageTime.ContainsKey(clientId))
                            _lastDamageTime[clientId] = 0f;

                        if (Time.time - _lastDamageTime[clientId] >= config.p3DamageInterval)
                        {
                            // 사타니즘(Satanism) 등으로 인한 성화 무효화 상태 체크
                            if (CombatManager.Instance != null && CombatManager.Instance.HasStatusEffect(player.NetworkObject, StatusEffectType.HolyFireImmune))
                            {
                                continue;
                            }

                            _lastDamageTime[clientId] = Time.time;

                            if (CombatManager.Instance != null)
                            {
                                CombatManager.Instance.ApplyDamage(null, player.NetworkObject, config.p3DamageAmount);
                            }

                            // 성화 도트 데미지음을 피해를 입은 본인에게만 재생
                            var burnRpcParams = new ClientRpcParams
                            {
                                Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } }
                            };
                            Client_PlayHolyFireBurnClientRpc(burnRpcParams);
                        }
                    }
                }
            }
        }
    }

    [ClientRpc]
    private void Client_UpdateVisualsClientRpc(float newRadius)
    {
        UpdateVisuals();
        // 성화 침식(반경 축소)이 일어날 때마다 전체에게 경고성 2D 사운드 재생
        SoundManager.Instance?.PlaySFX(SFXType.HolyFireShrink);
    }

    // 성화 외곽 도트 데미지음 (피해 입은 본인에게만 2D 재생)
    [ClientRpc]
    private void Client_PlayHolyFireBurnClientRpc(ClientRpcParams rpcParams = default)
    {
        SoundManager.Instance?.PlaySFX(SFXType.HolyFireBurn);
    }

    public override void OnNetworkSpawn()
    {
        if (IsClient)
        {
            UpdateVisuals();
            currentRadius.OnValueChanged += (oldVal, newVal) => UpdateVisuals();
            isZoneSealFire.OnValueChanged += (oldVal, newVal) => UpdateVisuals();
        }

        if (IsServer && isZoneSealFire.Value)
        {
            // 서버에서 직접 존의 크기를 RPC 파라미터로 넘겨줍니다.
            // (클라이언트에서 NetworkVariable 동기화 지연으로 인해 크기가 0으로 잡혀서 파티클이 중앙에만 작게 뭉치던 치명적 버그 해결!)
            SpawnZoneFirePatchesClientRpc(zoneExtents.Value);
        }
    }

    [ClientRpc]
    private void SpawnZoneFirePatchesClientRpc(Vector3 serverExtents)
    {
        if (fireParticleSystem == null) return;

        // 원본 파티클(3페이즈용) 비활성화
        fireParticleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var line = GetComponent<LineRenderer>();
        if (line != null) line.enabled = false;

        // 서버에서 확실하게 받아온 크기(serverExtents)를 사용합니다. 
        float xExtent = serverExtents.x > 0 ? serverExtents.x : 15f;
        float yExtent = serverExtents.y > 0 ? serverExtents.y : 10f; 
        float zExtent = serverExtents.z > 0 ? serverExtents.z : 15f;

        Vector3 centerPos = transform.position;
        float zoneArea = (xExtent * 2f) * (zExtent * 2f);

        // 바닥을 완벽하게 가리기 위해 패치 개수와 파티클 밀도를 황금비율로 맞춥니다.
        float patchSize = 12f; 
        
        int patchCount = Mathf.Clamp((int)(zoneArea / 60f), 15, 120);
        int spawnedCount = 0;

        // 🚨 바닥을 찾을 확률이 더 엄격해졌으므로 시도 횟수를 15배로 크게 늘립니다.
        for (int i = 0; i < patchCount * 15; i++)
        {
            if (spawnedCount >= patchCount) break;

            // Zone D처럼 1층과 0층(복층)이 모두 있는 경우를 위해 Y축 전체를 탐색합니다.
            // 존의 바닥부터 zoneHeight까지 탐색하여 겹치는 층을 모두 찾아냅니다.
            float targetY = centerPos.y + Random.Range(-2f, yExtent);

            Vector3 randomPos = new Vector3(
                centerPos.x + Random.Range(-xExtent, xExtent),
                targetY,
                centerPos.z + Random.Range(-zExtent, zExtent)
            );

            // 반경을 2.5m로 유지하여 존 바깥으로 새어나가는 것은 막되,
            // Y축(위아래)으로는 무작위 높이에서 NavMesh를 찾으므로 0층과 1층 모두에 정상적으로 스폰됩니다.
            if (UnityEngine.AI.NavMesh.SamplePosition(randomPos, out UnityEngine.AI.NavMeshHit hit, 2.5f, UnityEngine.AI.NavMesh.AllAreas))
            {
                // 🚨 존 C 토나 D 침범 버그 수정: NavMesh.SamplePosition은 요청 반경(2.5m) 안의
                // 가장 가까운 날 위치로 snap하므로, 존 경계 근처에서는 인접존 NavMesh로 넘어갈 수 있습니다.
                // snap된 후 실제 위치가 원래 존 범위 안에 있는지 반드시 검증합니다!
                bool withinX = hit.position.x >= centerPos.x - xExtent && hit.position.x <= centerPos.x + xExtent;
                bool withinZ = hit.position.z >= centerPos.z - zExtent && hit.position.z <= centerPos.z + zExtent;
                if (!withinX || !withinZ) continue; // 존 범위 밖이면 스폰 거부

                SpawnSinglePatch(hit.position + Vector3.up * 0.05f, patchSize);
                spawnedCount++;
            }
        }
    }

    private void SpawnSinglePatch(Vector3 pos, float patchSize)
    {
        // 30m짜리 초거대 패치를 스폰하되, 네모난 박스들이 똑같은 방향으로 정렬되지 않도록 무작위 Y축 회전을 줍니다!
        var patchObj = Instantiate(fireParticleSystem.gameObject, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
        patchObj.transform.localScale = Vector3.one;
        
        var cloneController = patchObj.GetComponent<HolyFireRingController>();
        if (cloneController != null) Destroy(cloneController);
        var cloneLine = patchObj.GetComponent<LineRenderer>();
        if (cloneLine != null) Destroy(cloneLine);

        var psArray = patchObj.GetComponentsInChildren<ParticleSystem>();
        
        foreach (var ps in psArray)
        {
            // 🚨 치명적 버그 수정: 최상위 객체(patchObj) 자체의 transform을 건드리면 위치가 (0,0,0)으로 초기화되어 
            // 수십 개의 불꽃이 맵 중앙 한 곳에 전부 겹쳐서 '다른 곳에 하나만 스폰된 것처럼' 보이는 현상이 발생했습니다!
            if (ps.gameObject != patchObj)
            {
                ps.transform.SetParent(patchObj.transform);
                ps.transform.localScale = Vector3.one;
                ps.transform.localPosition = Vector3.zero;
                // 기존 프리팹의 로컬 회전값(localRotation)은 건드리지 않아야 불꽃이 눕지 않습니다!
            }

            string psName = ps.gameObject.name.ToLower();
            
            if (psName.Contains("light") || psName.Contains("distortion") || psName.Contains("ring") || psName.Contains("outline") || psName.Contains("ashes"))
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.gameObject.SetActive(false);
                continue;
            }

            var shape = ps.shape;
            var main = ps.main;
            var emission = ps.emission;

            // 🚨 "바닥이 안 보이게 덮어달라"는 요청 반영: RGB 밝기는 억제하여 눈부심(Bloom)은 막되, 
            // 알파(투명도)를 3배 이상 올려서 바닥이 투명하게 비치지 않고 꽉 찬 불바다처럼 보이게 만듭니다!
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.6f, 0.55f, 0.4f, 0.3f), new Color(0.6f, 0.5f, 0.3f, 0.3f));
            var col = ps.colorOverLifetime;
            if (col.enabled)
            {
                Gradient platGrad = new Gradient();
                platGrad.SetKeys(
                    new GradientColorKey[] { 
                        new GradientColorKey(new Color(0.7f, 0.7f, 0.7f), 0.0f), 
                        new GradientColorKey(new Color(0.7f, 0.6f, 0.3f), 0.3f), 
                        new GradientColorKey(new Color(0.5f, 0.4f, 0.1f), 1.0f)  
                    },
                    // 투명도를 0.35 수준으로 대폭 올려서 바닥 텍스처를 완전히 가리도록 함
                    new GradientAlphaKey[] { 
                        new GradientAlphaKey(0.0f, 0.0f), 
                        new GradientAlphaKey(0.35f, 0.2f), 
                        new GradientAlphaKey(0.25f, 0.7f), 
                        new GradientAlphaKey(0.0f, 1.0f) 
                    }
                );
                col.color = new ParticleSystem.MinMaxGradient(platGrad);
            }

            // Box 이미터: 바닥 전체를 균일하게 덮고 파티클이 +Y 방향으로 자연스럽게 방출됩니다.
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.position = Vector3.zero;
            // Z축 방향으로 발사되는 기본 속성을 위로 솟구치게(-90도 회전) 만듭니다!
            shape.rotation = new Vector3(-90f, 0f, 0f); 
            shape.scale = new Vector3(patchSize, 0.05f, patchSize);

            // 수명을 늘려 불길이 더 높게 치솟도록 합니다. (목표: 2~3m 높이)
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.2f);

            var fol = ps.forceOverLifetime;
            var vol = ps.velocityOverLifetime;

            if (vol.enabled) vol.enabled = false;
            main.gravityModifier = 0f;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();

            // 'glow'나 'base' 같은 메인 불꽃들이 눕는 현상 방지! 오직 'quad'만 눕힙니다.
            if (psName.Contains("quad"))
            {
                fol.enabled = false; // 장판이 위로 떠오르지 않게 고정
                main.startSize3D = false; 
                main.startSize = new ParticleSystem.MinMaxCurve(patchSize * 0.8f, patchSize * 1.2f);
                
                main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0f); // 완벽 고정
                main.startRotation3D = false;
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
                
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(2f, 4f); 
                main.maxParticles = 10;
            }
            else
            {
                // 위로 치솟는 불꽃 설정 (속도로만 부드럽게 상승)
                fol.enabled = false; 

                // Phase 3 장벽처럼 가로 폭(X, Z)을 넉넉하게 주어 얄팍해 보이지 않게 합니다.
                main.startSize3D = true;
                main.startSizeX = new ParticleSystem.MinMaxCurve(3.0f, 5.0f);
                main.startSizeY = new ParticleSystem.MinMaxCurve(3.5f, 6.0f); // 수직 높이
                main.startSizeZ = new ParticleSystem.MinMaxCurve(3.0f, 5.0f);

                main.startRotation3D = false;
                // 불꽃이 꼿꼿하게 서 있지 않고, 좌우로 20~30도 정도 기울어져 더 자연스럽게 타오르도록 설정합니다.
                main.startRotation = new ParticleSystem.MinMaxCurve(-30f * Mathf.Deg2Rad, 30f * Mathf.Deg2Rad);
                
                main.startDelay = new ParticleSystem.MinMaxCurve(0.0f, 3.0f);
                
                // 일정한 속도로 서서히 위로 솟아오름 (실제 불처럼 강력한 상승 속도)
                main.startSpeed = new ParticleSystem.MinMaxCurve(2.0f, 4.0f);
                
                // 바닥을 빽빽하게 채우기 위해 스폰량을 대폭 늘립니다.
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(200f, 300f); 
                main.maxParticles = 1000;

                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                AnimationCurve fireCurve = new AnimationCurve();
                fireCurve.AddKey(0.0f, 0.1f);
                fireCurve.AddKey(0.15f, 1.0f);
                fireCurve.AddKey(0.6f, 0.6f);
                fireCurve.AddKey(1.0f, 0.0f);
                sol.size = new ParticleSystem.MinMaxCurve(1.0f, fireCurve);
            }

            if (!ps.isPlaying) ps.Play(false);
        }
    }

    private void UpdateVisuals()
    {
        if (isZoneSealFire.Value) return; // 2페이즈 장판은 ClientRpc로 1회성 생성 완료

        // 3페이즈 링 라인 렌더러(푸른색 선) 비활성화 요청 반영
        var line = GetComponent<LineRenderer>();
        if (line != null)
        {
            line.enabled = false;
        }

        if (fireParticleSystem != null)
        {
            ParticleSystem[] allParticles = GetComponentsInChildren<ParticleSystem>();
            foreach (var ps in allParticles)
            {
                string psName = ps.gameObject.name.ToLower();
                if (psName.Contains("light") || psName.Contains("distortion") || psName.Contains("ashes"))
                    continue;

                var shape = ps.shape;
                var main = ps.main;
                var emission = ps.emission;

                // 머티리얼이 흰색 캔버스가 되었으므로, 꼼수 없이 가장 화려하고 입체적인 푸른색(Cyan -> Deep Blue) 그라데이션을 적용합니다!
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.5f, 0.8f, 1f), new Color(0f, 0.4f, 1f));
                var col = ps.colorOverLifetime;
                if (col.enabled)
                {
                    Gradient blueGrad = new Gradient();
                    blueGrad.SetKeys(
                        new GradientColorKey[] { 
                            new GradientColorKey(new Color(0.7f, 0.9f, 1f), 0.0f), // 중심부 청백색 코어
                            new GradientColorKey(new Color(0f, 0.4f, 1f), 0.2f),   // 타오르는 진한 푸른색
                            new GradientColorKey(new Color(0f, 0.1f, 0.8f), 1.0f)  // 어두운 푸른색으로 소멸
                        },
                        new GradientAlphaKey[] { 
                            new GradientAlphaKey(0f, 0.0f), 
                            new GradientAlphaKey(1f, 0.1f), 
                            new GradientAlphaKey(1f, 0.7f), 
                            new GradientAlphaKey(0f, 1.0f) 
                        }
                    );
                    col.color = new ParticleSystem.MinMaxGradient(blueGrad);
                }

                if (!shape.enabled) shape.enabled = true;
                if (!emission.enabled) emission.enabled = true;

                // 3페이즈 링 모양 (자연스러운 경계선 형성)
                shape.shapeType = ParticleSystemShapeType.Circle;
                // Z축 발사 파티클을 위로 솟구치게(-90도) 강제합니다.
                shape.rotation = new Vector3(-90f, 0f, 0f); 
                
                // 0.0(완벽한 1D 선)으로 하면 종이장처럼 작위적으로 보일 수 있으므로, 0.05(5%)의 미세한 두께감을 주어 
                // 불꽃들이 앞뒤로 지그재그로 서며 입체적인 장벽을 형성하도록 합니다.
                shape.radiusThickness = 0.05f; 
                shape.radius = currentRadius.Value; // 실시간 축소 반영
                
                // 파티클이 일정한 간격으로 생성되는 현상을 막고 완전히 무작위로 흩뿌려지도록 강제합니다.
                shape.arcMode = ParticleSystemShapeMultiModeValue.Random;

                main.startLifetime = new ParticleSystem.MinMaxCurve(2.0f, 3.5f);

                if (psName.Contains("quad"))
                {
                    // 바닥 장판
                    main.startSize3D = true;
                    main.startSizeX = new ParticleSystem.MinMaxCurve(10.0f, 15.0f);
                    main.startSizeY = new ParticleSystem.MinMaxCurve(10.0f, 15.0f);
                    main.startSizeZ = new ParticleSystem.MinMaxCurve(10.0f, 15.0f);
                    
                    main.startSpeed = new ParticleSystem.MinMaxCurve(0.001f, 0.01f);
                    
                    main.startRotation3D = false;
                    main.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);

                    main.maxParticles = 500;
                    emission.rateOverTime = new ParticleSystem.MinMaxCurve(50f, 80f);
                }
                else
                {
                    // 플레이어를 향해 조여오는 위협적인 거대 불기둥
                    main.startSize3D = true;
                    // 스폰은 얇은 선에서 되지만, 파티클 자체의 크기를 넓게(6~10m) 설정하여 
                    // 선을 중심으로 두꺼운 불의 장벽이 형성되도록 만듭니다.
                    main.startSizeX = new ParticleSystem.MinMaxCurve(6.0f, 10.0f);
                    
                    // 🚨 3페이즈는 조여오는 압박감을 주기 위해 2페이즈(허리높이)보다 훨씬 높은 거대한 장벽(4~7m)으로 만듭니다!
                    main.startSizeY = new ParticleSystem.MinMaxCurve(4.0f, 7.0f); 
                    main.startSizeZ = new ParticleSystem.MinMaxCurve(6.0f, 10.0f);

                    main.startRotation3D = false; 
                    
                    // 불기둥을 좌우로 20~30도 비틀어서 작위적인 느낌을 없애고 훨씬 자연스러운 화염을 연출합니다.
                    main.startRotation = new ParticleSystem.MinMaxCurve(-30f * Mathf.Deg2Rad, 30f * Mathf.Deg2Rad);
                    
                    // 애니메이션 칼군무 방지를 위해 생성 타이밍을 무작위로 지연시킵니다.
                    main.startDelay = new ParticleSystem.MinMaxCurve(0.0f, 0.5f);

                    // 불꽃이 위로 더 빠르게 솟구치게 하여 생동감을 줍니다.
                    main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3.0f);

                    // 원 둘레를 빈틈없이 꽉 채우기 위해 어마어마한 양의 파티클 스폰 허용
                    main.maxParticles = 2000;
                    // 원의 둘레(반지름)에 비례해서 스폰량을 유동적으로 조절
                    float dynamicRate = Mathf.Clamp(currentRadius.Value * 8f, 200f, 1000f);
                    emission.rateOverTime = new ParticleSystem.MinMaxCurve(dynamicRate * 0.8f, dynamicRate * 1.2f);
                }

                // 🚨 기존에 있던 ps.Stop(Clear) 로직을 삭제! 
                // 반경이 줄어들 때마다 불꽃이 팍팍 꺼지지 않고, 자연스럽게 안쪽으로 스며들며 조여오도록 만듭니다.
                if (!ps.isPlaying) ps.Play(false);
            }
        }
    }

    public void AccelerateErosion(float multiplier)
    {
        if (!IsServer) return;
        erosionMultiplier *= multiplier;
    }
}
