using UnityEngine;

/// <summary>
/// 각 플레이어/캐릭터 프리팹에 부착되어 애니메이션 이벤트와 연동되는 발소리 컴포넌트
/// </summary>
public class PlayerFootstepAudio : MonoBehaviour
{
    [Header("발소리 클립 (캐릭터 특화)")]
    [Tooltip("일반 걷기 발소리 클립들 (랜덤 재생)")]
    public AudioClip[] walkClips;
    [Tooltip("달리기/스프린트 발소리 클립들 (랜덤 재생)")]
    public AudioClip[] sprintClips;
    
    [Header("오디오 설정")]
    [Range(0f, 1f)] public float walkVolume = 0.7f;
    [Range(0f, 1f)] public float sprintVolume = 1.0f;
    [Range(0.1f, 3f)] public float walkPitch = 1.0f;
    [Range(0.1f, 3f)] public float sprintPitch = 1.2f;
    [Tooltip("피치 변형 정도 (찰진 느낌 부여)")]
    [Range(0f, 0.2f)] public float pitchVariation = 0.1f;

    private AudioSource footstepSource;
    private PlayerMovementController movementController;
    private bool _mixerRouted = false;

    private void Awake()
    {
        footstepSource = gameObject.AddComponent<AudioSource>();
        footstepSource.spatialBlend = 1f; // 3D 사운드
        footstepSource.minDistance = 2f;
        footstepSource.maxDistance = 15f;
        footstepSource.playOnAwake = false;

        movementController = GetComponentInParent<PlayerMovementController>();
    }

    [Header("자동 재생 옵션 (애니메이션 이벤트 미사용 시)")]
    [Tooltip("체크 시 애니메이션 이벤트 대신 내부 타이머로 발소리를 자동 재생합니다.")]
    public bool useAutoTimer = true;
    public float walkStepInterval = 0.5f;
    public float sprintStepInterval = 0.3f;
    private float _stepTimer = 0f;

    private void Update()
    {
        if (!useAutoTimer || movementController == null) return;

        var charCtrl = movementController.GetComponent<CharacterController>();
        if (charCtrl == null) return;
        
        // 확실하게 키보드/조이스틱 입력이 발생하고 있는지 체크 (물리 엔진 오차 무시)
        bool isMoving = false;
        if (movementController.isBot)
        {
            isMoving = movementController.botInput != Vector2.zero;
        }
        else if (InputManager.Instance != null)
        {
            isMoving = InputManager.Instance.MoveInput != Vector2.zero;
        }

        // 땅에 닿아있고 이동 중일 때만 타이머 작동
        if (charCtrl.isGrounded && isMoving)
        {
            _stepTimer -= Time.deltaTime;
            if (_stepTimer <= 0f)
            {
                OnFootstep();
                _stepTimer = movementController.IsSprinting ? sprintStepInterval : walkStepInterval;
            }
        }
        else
        {
            _stepTimer = 0f; // 멈추면 즉시 초기화하여 다음 이동 시 바로 소리가 나도록 함
        }
    }

    /// <summary>
    /// 애니메이터의 걷기/달리기 애니메이션의 발이 땅에 닿는 프레임에 
    /// "OnFootstep" 이라는 이름으로 Animation Event를 추가하세요.
    /// </summary>
    public void OnFootstep()
    {
        bool isSprinting = false;
        bool isGreatSinner = false;
        
        if (movementController != null)
        {
            isSprinting = movementController.IsSprinting;
            if (movementController.TryGetComponent<PlayerNetworkController>(out var pnc))
            {
                isGreatSinner = pnc.isGreatSinner;
            }
        }

        // 인스펙터에 할당된 클립을 우선 사용하되, 없으면 역할에 맞춰 매번 동적으로 가져옵니다.
        AudioClip[] dynamicWalkClips = walkClips;
        AudioClip[] dynamicSprintClips = sprintClips;

        if (dynamicWalkClips == null || dynamicWalkClips.Length == 0 || dynamicSprintClips == null || dynamicSprintClips.Length == 0)
        {
            if (SoundManager.Instance != null && SoundManager.Instance.GetConfig() != null)
            {
                SFXType typeWalk = isGreatSinner ? SFXType.GreatSinnerFootstep : SFXType.PlayerFootstep;
                SFXType typeSprint = isGreatSinner ? SFXType.GreatSinnerFootstep : SFXType.PlayerSprintFootstep;

                if (dynamicWalkClips == null || dynamicWalkClips.Length == 0)
                {
                    if (SoundManager.Instance.GetConfig().TryGetSFX(typeWalk, out SFXEntry entryWalk) && entryWalk.clips != null)
                        dynamicWalkClips = entryWalk.clips;
                }
                
                if (dynamicSprintClips == null || dynamicSprintClips.Length == 0)
                {
                    if (SoundManager.Instance.GetConfig().TryGetSFX(typeSprint, out SFXEntry entrySprint) && entrySprint.clips != null)
                        dynamicSprintClips = entrySprint.clips;
                }
            }
        }

        AudioClip[] currentClips = (isSprinting && dynamicSprintClips != null && dynamicSprintClips.Length > 0) ? dynamicSprintClips : dynamicWalkClips;
        
        if (currentClips != null && currentClips.Length > 0)
        {
            AudioClip clip = currentClips[Random.Range(0, currentClips.Length)];
            
            // 피치 랜더마이즈로 기계적인 반복음 완화
            float basePitch = isSprinting ? sprintPitch : walkPitch;
            footstepSource.pitch = basePitch + Random.Range(-pitchVariation, pitchVariation);
            
            // 글로벌 마스터/SFX 볼륨을 가져와 적용
            float globalVol = 1.0f;
            if (SoundManager.Instance != null && SoundManager.Instance.GetConfig() != null)
            {
                var config = SoundManager.Instance.GetConfig();
                globalVol = config.sfxVolume * config.masterVolume;

                // 발소리를 SFX 믹서 그룹으로 라우팅하여 구역별 리버브가 걸리게 한다. (최초 1회)
                if (!_mixerRouted && config.sfxMixerGroup != null)
                {
                    footstepSource.outputAudioMixerGroup = config.sfxMixerGroup;
                    _mixerRouted = true;
                }
            }
            else
            {
                Debug.LogWarning("[Footstep Debug] SoundManager 또는 Config가 없습니다!");
            }

            // 대죄인 발소리 볼륨/거리 보정 및 자신의 발소리 2D 처리
            float roleVolumeMultiplier = 1.0f;
            if (movementController != null && movementController.TryGetComponent<PlayerNetworkController>(out var pnc))
            {
                // [핵심] 내 발소리는 무조건 2D로 들리게 설정! (거리 감쇠 무시)
                if (pnc.IsOwner)
                {
                    footstepSource.spatialBlend = 0f;
                    
                    // 2D 사운드는 거리 감쇠가 없어서 매우 크게 들리므로 기본 볼륨을 대폭 깎아줍니다.
                    roleVolumeMultiplier = 0.4f; 
                }
                else
                {
                    footstepSource.spatialBlend = 1f; // 남의 발소리는 3D 처리
                }

                if (pnc.isGreatSinner)
                {
                    if (pnc.IsOwner)
                    {
                        // 대죄인 본인 귀에 들리는 볼륨
                        footstepSource.maxDistance = 15f; 
                    }
                    else
                    {
                        // 악인들 귀에는 공포감을 위해 아주 크게 (1.5배) 들리고, 
                        // 소리가 들리는 최대 거리도 2배(30m)로 증가
                        roleVolumeMultiplier = 1.5f;
                        footstepSource.maxDistance = 30f;
                    }
                }
                else
                {
                    // 일반 악인들의 발소리 거리는 원상복구
                    footstepSource.maxDistance = 15f;
                }
            }

            // 너무 크다는 피드백 반영: 강제 증폭 제거
            float finalVolume = (isSprinting ? sprintVolume : walkVolume) * globalVol * roleVolumeMultiplier;
            Debug.Log($"[Footstep Debug] 발소리 재생! Clip: {clip.name}, Volume: {finalVolume}, SpatialBlend: {footstepSource.spatialBlend}, isSprinting: {isSprinting}");
            footstepSource.PlayOneShot(clip, finalVolume);
        }
        else
        {
            Debug.LogWarning($"[Footstep Debug] 재생할 클립이 없습니다! walkClips 여부: {walkClips != null}, sprintClips 여부: {sprintClips != null}");
        }
    }
}
