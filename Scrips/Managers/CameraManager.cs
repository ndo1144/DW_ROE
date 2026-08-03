using UnityEngine;
using Cinemachine;

public class CameraManager : MonoBehaviour {
    public static CameraManager Instance { get; private set; }

    [Header("카메라 세팅 (인스펙터에서 할당)")]
    public CinemachineVirtualCamera fpsCamera;
    public CinemachineVirtualCamera tpsCamera;

    [Header("회전 설정")]
    public float mouseSensitivity = 2f;
    public float minPitch = -80f;
    public float maxPitch = 80f;

    [Header("카메라별 안개(Fog) 설정")]
    public bool enableDynamicFog = true;
    [Tooltip("1인칭(악인) 시점의 안개 농도 (기본: 0.05)")]
    public float fpsFogDensity = 0.05f;
    [Tooltip("1인칭(악인) 시점의 안개 색상")]
    public Color fpsFogColor = new Color(0.2f, 0.2f, 0.2f, 1f);
    
    [Tooltip("3인칭(대죄인) 시점의 안개 농도 (기본: 0.01)")]
    public float tpsFogDensity = 0.01f;
    [Tooltip("3인칭(대죄인) 시점의 안개 색상")]
    public Color tpsFogColor = new Color(0.1f, 0.1f, 0.1f, 1f);

    [Header("천장 감지 설정 (3인칭 점프 버그 방지)")]
    public float ceilingCheckDistance = 1.5f;
    public LayerMask ceilingLayerMask = ~0; // 인스펙터에서 Obstacle이나 Environment만 선택 가능

    private Transform playerTransform;
    private Transform playerLookRoot;

    private float currentPitch = 0f;
    private bool isFPS = true;

    // 실내/실외 뷰 상태 관리 (기본값: 실내)
    public bool isIndoorView = true;

    private bool _cursorLocked = false;
    private float defaultTpsDistance = -1f;
    private float _baseTpsAnchorY = 0.5f; // 대죄인 앵커의 기준 Y 높이

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Update() {
        if (playerTransform == null || playerLookRoot == null || InputManager.Instance == null) return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            _cursorLocked = false;
        }
        else if (!_cursorLocked && Input.GetMouseButtonDown(0))
        {
            _cursorLocked = true;
        }

        if (_cursorLocked)
        {
            // Confined를 사용하면 에디터 환경에서 Game 뷰 밖으로 마우스가 나가는 것을 방지할 수 있습니다.
            // 하지만 FPS 방식의 화면 회전을 위해서는 Locked가 필수적입니다.
            if (Cursor.lockState != CursorLockMode.Locked)
                Cursor.lockState = CursorLockMode.Locked;
            if (Cursor.visible)
                Cursor.visible = false;
        }
        else
        {
            if (Cursor.lockState != CursorLockMode.None)
                Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible)
                Cursor.visible = true;
        }

        if (Input.GetKeyDown(KeyCode.V)) {
            ToggleView();
        }

        // [테스트용] B 키를 누르면 실내/실외 뷰를 토글합니다.
        if (Input.GetKeyDown(KeyCode.B)) {
            SetEnvironmentView(!isIndoorView);
            Debug.Log($"[CameraManager] 환경 뷰 변경됨: {(isIndoorView ? "실내 (뒤통수 샷)" : "실외 (이볼브 샷)")}");
        }

        if (PlayerNetworkController.LocalInstance != null && PlayerNetworkController.LocalInstance.isSpectating)
        {
            // 관전 모드: 타겟 캐릭터 자체를 회전시키지 않고, 카메라의 피치(상하)만 돌리거나, 
            // 시네머신이 LookAt을 유지하되 타겟의 회전을 따라가게 둡니다.
            // 관전 중 마우스 입력으로 피치(상하) 각도는 조절할 수 있도록 합니다.
            if (_cursorLocked)
            {
                Vector2 lookInput = InputManager.Instance.LookInput;
                currentPitch -= lookInput.y * mouseSensitivity;
                currentPitch = Mathf.Clamp(currentPitch, minPitch, maxPitch);
                if (playerLookRoot != null)
                {
                    playerLookRoot.localEulerAngles = new Vector3(currentPitch, 0f, 0f);
                }
            }
            return;
        }

        if (_cursorLocked)
        {
            Vector2 lookInput = InputManager.Instance.LookInput;
            playerTransform.Rotate(Vector3.up * lookInput.x * mouseSensitivity);

            currentPitch -= lookInput.y * mouseSensitivity;
            // 대죄인(3인칭) 모드 상하 제한
            float currentMaxPitch = (!isFPS) ? 35f : maxPitch;
            float currentMinPitch = (!isFPS) ? -20f : minPitch;
            currentPitch = Mathf.Clamp(currentPitch, currentMinPitch, currentMaxPitch);
            playerLookRoot.localEulerAngles = new Vector3(currentPitch, 0f, 0f);
        }
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && _cursorLocked)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    public void SetLocalPlayerTarget(Transform player, Transform lookRoot, bool isGreatSinner = false, CharacterStatSO cameraStatSO = null) {
        playerTransform = player;
        playerLookRoot = lookRoot;

        if (fpsCamera != null) {
            fpsCamera.Follow = lookRoot;
            fpsCamera.LookAt = null;
        }

        if (tpsCamera != null) {
            Transform tpsAnchor = lookRoot.Find("TPS_Anchor");
            if (tpsAnchor == null) {
                tpsAnchor = new GameObject("TPS_Anchor").transform;
                tpsAnchor.SetParent(lookRoot, false);
            }

            tpsCamera.Follow = tpsAnchor;
            tpsCamera.LookAt = tpsAnchor;

            // --- 3인칭 카메라 벽/바닥 뚫기 방지 (CinemachineCollider) ---
            var collider = tpsCamera.GetComponent<CinemachineCollider>();
            if (collider == null)
            {
                collider = tpsCamera.gameObject.AddComponent<CinemachineCollider>();
            }
            collider.m_Strategy = CinemachineCollider.ResolutionStrategy.PullCameraForward;
            // 카메라가 장애물을 벗어났을 때 딜레이(Damping) 없이 즉각적으로 원래 거리로 복구되도록 설정
            collider.m_Damping = 0f; 
            collider.m_DampingWhenOccluded = 0f; 
            collider.m_CameraRadius = 0.1f; 
            collider.m_IgnoreTag = "Player";
            collider.m_MinimumDistanceFromTarget = 0.5f; 

            // 플레이어 모델(날개, 무기 등)이 카메라와 충돌하여 1인칭으로 강제 줌인되는 현상을 막기 위해 
            // Player, Ignore Raycast, TransparentFX 레이어를 충돌 검사에서 제외합니다.
            int playerLayer = LayerMask.NameToLayer("Player");
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            int transparentFXLayer = LayerMask.NameToLayer("TransparentFX");
            
            // 위 레이어들이 존재하는 경우에만 비트 연산으로 마스크에서 제외
            LayerMask excludeMask = 0;
            if (playerLayer != -1) excludeMask |= (1 << playerLayer);
            if (ignoreRaycastLayer != -1) excludeMask |= (1 << ignoreRaycastLayer);
            if (transparentFXLayer != -1) excludeMask |= (1 << transparentFXLayer);
            
            collider.m_CollideAgainst = ~excludeMask; 
            // ----------------------------------------------------------------------

            // 멀미 방지: 카메라 이동/회전 지연(Damping) 제거
            var framingTransposer = tpsCamera.GetCinemachineComponent<CinemachineFramingTransposer>();
            if (framingTransposer != null) {
                if (defaultTpsDistance < 0) defaultTpsDistance = framingTransposer.m_CameraDistance;
                framingTransposer.m_TrackedObjectOffset = Vector3.zero; 
                // 즉각적인 반응을 위해 댐핑을 0으로 제거
                framingTransposer.m_XDamping = 0f;
                framingTransposer.m_YDamping = 0f;
                framingTransposer.m_ZDamping = 0f;
            } else {
                var thirdPersonFollow = tpsCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
                if (thirdPersonFollow != null) {
                    if (defaultTpsDistance < 0) defaultTpsDistance = thirdPersonFollow.CameraDistance;
                    Vector3 baseOffset = thirdPersonFollow.ShoulderOffset;
                    thirdPersonFollow.ShoulderOffset = new Vector3(0f, baseOffset.y, baseOffset.z);
                    // 즉각적인 반응을 위해 댐핑 제거
                    thirdPersonFollow.Damping = Vector3.zero;
                }
            }

            // 회전 시 딜레이 제거 (Aim Composer 댐핑 제거)
            var composer = tpsCamera.GetCinemachineComponent<CinemachineComposer>();
            if (composer != null)
            {
                composer.m_HorizontalDamping = 0f;
                composer.m_VerticalDamping = 0f;
            }
        }

        isFPS = !isGreatSinner;
        if (fpsCamera != null && tpsCamera != null) {
            fpsCamera.Priority = isFPS ? 30 : 10;
            tpsCamera.Priority = isFPS ? 10 : 30;
            
            // 처음 스폰 시 카메라가 천천히 이동(블렌딩)하지 않고 즉시 대상에 붙도록 강제 설정
            fpsCamera.PreviousStateIsValid = false;
            tpsCamera.PreviousStateIsValid = false;
        }

        // ─── 캐릭터별 카메라 오프셋 적용 ───
        // CharacterStatSO에 설정된 눈 높이 / 앵커 오프셋을 lookRoot에 반영합니다.
        if (cameraStatSO != null)
        {
            // 1인칭 눈 오프셋: fpsCamera가 Follow하는 lookRoot 자체의 로컬 위치를 이동시킵니다.
            // lookRoot는 플레이어 프리팹의 자식 오브젝트이므로 localPosition 조작이 안전합니다.
            if (isFPS)
            {
                // SO 값이 (0,0,0)인 경우 캐릭터 발밑(바닥)으로 카메라가 쳐박히는 현상 방지 (기본 눈높이 0.6 적용)
                if (cameraStatSO.fpsEyeOffset == Vector3.zero)
                    lookRoot.localPosition = new Vector3(0f, 0.6f, 0f);
                else
                    lookRoot.localPosition = cameraStatSO.fpsEyeOffset;
            }

            // 3인칭 앵커 오프셋: TPS_Anchor의 기본 위치에 캐릭터 전용 오프셋을 더합니다.
            if (!isFPS)
            {
                Transform tpsAnchor = lookRoot.Find("TPS_Anchor");
                if (tpsAnchor != null)
                {
                    // SetEnvironmentView에서 설정한 _baseTpsAnchorY에 캐릭터 오프셋 Y를 추가
                    _baseTpsAnchorY += cameraStatSO.tpsAnchorOffset.y;
                    tpsAnchor.localPosition = new Vector3(
                        cameraStatSO.tpsAnchorOffset.x,
                        _baseTpsAnchorY,
                        cameraStatSO.tpsAnchorOffset.z
                    );
                }
            }
        }

        // 실내/실외 뷰에 맞게 앵커와 거리 세팅
        SetEnvironmentView(isIndoorView);
        
        UpdateFog(); // 카메라 역할에 맞춰 안개 갱신
        
        _cursorLocked = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    /// <summary>
    /// 카메라가 장애물에 끼이거나 버그가 생겼을 때 완전히 껐다 켜서 고치는 리셋 함수
    /// </summary>
    public void ResetCameraState()
    {
        StartCoroutine(ResetCameraCoroutine());
    }

    private System.Collections.IEnumerator ResetCameraCoroutine()
    {
        // 시네머신 충돌체가 몸 안에 갇히는 현상을 해결하기 위해 카메라 우선순위를 리셋합니다.
        if (tpsCamera != null) tpsCamera.Priority = 1;
        if (fpsCamera != null) fpsCamera.Priority = 1;

        yield return null; // 한 프레임 대기

        if (tpsCamera != null)
        {
            tpsCamera.Priority = isFPS ? 10 : 30;
            tpsCamera.PreviousStateIsValid = false;
        }
        if (fpsCamera != null)
        {
            fpsCamera.Priority = isFPS ? 30 : 10;
            fpsCamera.PreviousStateIsValid = false;
        }
    }

    /// <summary>
    /// 실내(Indoor)와 실외(Outdoor) 환경에 따라 3인칭(대죄인) 카메라의 위치와 거리를 조정합니다.
    /// </summary>
    public void SetEnvironmentView(bool isIndoor)
    {
        isIndoorView = isIndoor;
        if (playerLookRoot == null || tpsCamera == null) return;

        Transform tpsAnchor = playerLookRoot.Find("TPS_Anchor");
        if (tpsAnchor != null)
        {
            bool isGreatSinnerMode = !isFPS; // isFPS가 false면 대죄인(3인칭) 모드

            if (isGreatSinnerMode)
            {
                // [근본적 해결] 물리적인 타겟(앵커)은 무조건 캐릭터 정중앙(X=0)에 두어 좁은 골목 벽을 절대 파고들지 않게 합니다.
                // 유저가 가장 완벽하다고 느꼈던 이전의 거리감과 높이(0.6f)로 100% 원복합니다.
                _baseTpsAnchorY = isIndoor ? 0.5f : 0.8f;
                tpsAnchor.localPosition = new Vector3(0f, _baseTpsAnchorY, 0f);
            }
            else
            {
                // 일반 악인 관전/3인칭용 (기본값)
                tpsAnchor.localPosition = new Vector3(0f, 0f, 0f);
            }

            // 거리 및 뷰 오프셋 설정
            var framingTransposer = tpsCamera.GetCinemachineComponent<CinemachineFramingTransposer>();
            if (framingTransposer != null && defaultTpsDistance > 0)
            {
                // 정면을 볼 때 캐릭터가 잘리기 않도록 거리를 살짝 더 뒤로 뺍니다. (실내 1.8배, 실외 2.8배)
                // 기본 세팅된 거리가 너무 짧을 경우 이빨 뷰가 되는 것을 막기 위해 최소 거리를 보장합니다.
                float calculatedDist = isIndoor ? defaultTpsDistance * 1.8f : defaultTpsDistance * 2.8f;
                float targetDist = isGreatSinnerMode ? (isIndoor ? Mathf.Max(calculatedDist, 4.5f) : Mathf.Max(calculatedDist, 6.5f)) : defaultTpsDistance;
                framingTransposer.m_CameraDistance = targetDist;
                
                // 화면상에서만 캐릭터를 왼쪽으로 밀어내어 시야를 여는 가상 오프셋 적용
                float viewOffsetX = isGreatSinnerMode ? (isIndoor ? 1.0f : 1.5f) : 0f;
                framingTransposer.m_TrackedObjectOffset = new Vector3(viewOffsetX, -0.3f, 0f); // Y값을 살짝 내려 몬스터가 화면 위로 올라오게 함
            }
            else
            {
                var thirdPersonFollow = tpsCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
                if (thirdPersonFollow != null && defaultTpsDistance > 0)
                {
                    float calculatedDist = isIndoor ? defaultTpsDistance * 1.8f : defaultTpsDistance * 2.8f;
                    float targetDist = isGreatSinnerMode ? (isIndoor ? Mathf.Max(calculatedDist, 4.5f) : Mathf.Max(calculatedDist, 6.5f)) : defaultTpsDistance;
                    thirdPersonFollow.CameraDistance = targetDist;
                    
                    float viewOffsetX = isGreatSinnerMode ? (isIndoor ? 1.0f : 1.5f) : 0f;
                    Vector3 baseOffset = thirdPersonFollow.ShoulderOffset;
                    thirdPersonFollow.ShoulderOffset = new Vector3(viewOffsetX, baseOffset.y - 0.3f, baseOffset.z);
                }
            }

            // 실내뷰일 때 캐릭터 반투명화 처리 (시야 확보)
            SetCharacterAlpha(isGreatSinnerMode && isIndoor);
        }
    }

    /// <summary>
    /// 캐릭터 모델의 머티리얼 알파값을 조정하여 반투명/불투명 상태를 전환합니다.
    /// </summary>
    private void SetCharacterAlpha(bool isTransparent)
    {
        if (playerTransform == null) return;
        
        Renderer[] renderers = playerTransform.GetComponentsInChildren<Renderer>();
        float targetAlpha = isTransparent ? 0.35f : 1.0f; // 실내뷰 반투명도 설정 (0.35 = 35% 불투명)

        foreach (var r in renderers)
        {
            ApplyMaterialAlpha(r, targetAlpha);
        }
    }

    private System.Collections.Generic.Dictionary<Renderer, Material[]> _originalMaterials = new();

    /// <summary>
    /// 단일 렌더러의 투명도를 처리하는 공통 메서드 (Standard & URP 지원)
    /// </summary>
    private void ApplyMaterialAlpha(Renderer r, float targetAlpha)
    {
        if (r == null) return;
        bool isTransparent = targetAlpha < 1.0f;

        // 원본 머티리얼 백업 저장
        if (!_originalMaterials.ContainsKey(r))
        {
            _originalMaterials[r] = r.sharedMaterials;
        }

        if (isTransparent)
        {
            Material[] transMats = new Material[r.sharedMaterials.Length];
            for (int i = 0; i < r.sharedMaterials.Length; i++)
            {
                Material orig = _originalMaterials[r][i];
                // URP Lit 셰이더로 완전히 새로운 투명 머티리얼 생성 (런타임 배리언트 누락 방지)
                Material tMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                
                // 원본 텍스처 및 색상 복사
                if (orig.HasProperty("_BaseMap")) tMat.SetTexture("_BaseMap", orig.GetTexture("_BaseMap"));
                if (orig.HasProperty("_MainTex")) tMat.SetTexture("_BaseMap", orig.GetTexture("_MainTex")); // Standard 호환
                
                Color baseColor = Color.white;
                if (orig.HasProperty("_BaseColor")) baseColor = orig.GetColor("_BaseColor");
                else if (orig.HasProperty("_Color")) baseColor = orig.GetColor("_Color");
                
                baseColor.a = targetAlpha;
                tMat.SetColor("_BaseColor", baseColor);
                
                // 완벽한 URP 투명도 설정
                tMat.SetFloat("_Surface", 1); // Transparent
                tMat.SetFloat("_Blend", 0); // Alpha
                tMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                tMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                tMat.SetInt("_ZWrite", 0);
                tMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                tMat.EnableKeyword("_ALPHABLEND_ON");
                tMat.renderQueue = 3000;
                
                transMats[i] = tMat;
            }
            r.materials = transMats;
        }
        else
        {
            // 원래 불투명 머티리얼로 완벽 복구
            if (_originalMaterials.TryGetValue(r, out Material[] origMats))
            {
                r.materials = origMats;
            }
        }
    }

    private void ToggleView() {
        isFPS = !isFPS;
        if (fpsCamera != null && tpsCamera != null) {
            fpsCamera.Priority = isFPS ? 20 : 10;
            tpsCamera.Priority = isFPS ? 10 : 20;
        }
        
        SetEnvironmentView(isIndoorView); // 카메라 거리와 위치를 현재 실내/외 모드에 맞게 적용
        UpdateFog(); // 카메라 역할에 맞춰 안개 갱신

        if (PlayerNetworkController.LocalInstance != null) {
            PlayerNetworkController.LocalInstance.RequestRoleChangeServerRpc(!isFPS);
        }
    }

    public void SetSpectatorTarget(Transform target, Transform lookRoot)
    {
        playerTransform = target;
        playerLookRoot = lookRoot;

        if (tpsCamera != null) {
            Transform tpsAnchor = lookRoot.Find("TPS_Anchor");
            if (tpsAnchor == null) {
                tpsAnchor = new GameObject("TPS_Anchor").transform;
                tpsAnchor.SetParent(lookRoot, false);
                // 강제로 앵커를 머리 위쪽으로 살짝 올림
                tpsAnchor.localPosition = new Vector3(0f, 0.5f, 0f);
            }

            tpsCamera.Follow = tpsAnchor;
            tpsCamera.LookAt = tpsAnchor;
            
            // 시네머신 컴포넌트를 직접 조작하여 거리를 3인칭(관전용 4미터)으로 강제 고정
            var framingTransposer = tpsCamera.GetCinemachineComponent<CinemachineFramingTransposer>();
            if (framingTransposer != null) {
                framingTransposer.m_CameraDistance = 4.0f; // 무조건 4미터 확보
                framingTransposer.m_TrackedObjectOffset = new Vector3(1.0f, -0.3f, 0f); // 살짝 오른쪽 어깨 너머
            } else {
                var thirdPersonFollow = tpsCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
                if (thirdPersonFollow != null) {
                    thirdPersonFollow.CameraDistance = 4.0f;
                    thirdPersonFollow.ShoulderOffset = new Vector3(1.0f, -0.3f, 0f);
                }
            }
            
            // 콜라이더가 관전 대상 캐릭터 자체에 부딪혀서 카메라가 앞으로 당겨지는 현상(1인칭화)을 방지
            var collider = tpsCamera.GetComponent<CinemachineCollider>();
            if (collider != null)
            {
                collider.m_IgnoreTag = "Player"; // Player 태그는 투시함
            }

            tpsCamera.PreviousStateIsValid = false;
        }

        isFPS = false;
        if (fpsCamera != null && tpsCamera != null) {
            fpsCamera.Priority = 10;
            tpsCamera.Priority = 30; // TPS 활성화
        }
        UpdateFog(); // 관전 모드 진입 시 안개 갱신
    }

    private Vector3 _lastTargetPos;
    private float _currentAnchorY = 0f;

    private void LateUpdate()
    {
        if (playerTransform != null)
        {
            if (Vector3.Distance(playerTransform.position, _lastTargetPos) > 10f)
            {
                if (fpsCamera != null) fpsCamera.PreviousStateIsValid = false;
                if (tpsCamera != null) tpsCamera.PreviousStateIsValid = false;
            }
            _lastTargetPos = playerTransform.position;

            // --- 천장 감지 기반 대죄인(3인칭) 카메라 앵커 동적 하향 (부드러운 보간 적용) ---
            if (!isFPS && playerLookRoot != null)
            {
                Transform tpsAnchor = playerLookRoot.Find("TPS_Anchor");
                if (tpsAnchor != null && _baseTpsAnchorY > 0f)
                {
                    Vector3 rayOrigin = playerLookRoot.position;
                    float targetY = _baseTpsAnchorY;

                    // 플레이어의 팔이나 날개가 위로 올라올 때 천장으로 오인되는 것을 방지
                    int playerLayer = LayerMask.NameToLayer("Player");
                    LayerMask safeCeilingMask = ceilingLayerMask;
                    if (playerLayer != -1) {
                        safeCeilingMask &= ~(1 << playerLayer);
                    }

                    // 플레이어 머리 위로 레이캐스트 발사 (거리를 2.0m로 넉넉하게 잡음)
                    if (Physics.Raycast(rayOrigin, Vector3.up, out RaycastHit hit, 2.0f, safeCeilingMask, QueryTriggerInteraction.Ignore))
                    {
                        // 천장에 부딪히면 충돌 지점에서 안전거리를 0.5f 이상 넉넉하게 확보하여 카메라가 천장 텍스처에 파고들지 않게 함
                        float safeDistance = hit.distance - 0.6f;
                        
                        // 타겟 Y값을 기본값보다 낮추되, 바닥 밑으로 너무 꺼지지 않게 하한선(-0.2f) 지정
                        targetY = Mathf.Clamp(safeDistance, -0.2f, _baseTpsAnchorY);
                    }

                    // 카메라가 천장에 닿을 때 덜컹거리는(끼인 듯한) 느낌을 없애기 위해 부드럽게(Lerp) 높이를 조절
                    _currentAnchorY = Mathf.Lerp(_currentAnchorY, targetY, Time.deltaTime * 15f);
                    tpsAnchor.localPosition = new Vector3(0f, _currentAnchorY, 0f);
                }
            }
        }
    }

    /// <summary>
    /// 현재 1인칭/3인칭 상태에 따라 RenderSettings의 안개(Fog) 농도와 색상을 즉시 변경합니다.
    /// </summary>
    private void UpdateFog()
    {
        if (!enableDynamicFog) return;

        // 안개 기능을 강제로 켭니다 (자연스러운 지수 안개 방식)
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;

        if (isFPS)
        {
            // 악인 (1인칭) 안개 적용
            RenderSettings.fogDensity = fpsFogDensity;
            RenderSettings.fogColor = fpsFogColor;
        }
        else
        {
            // 대죄인/관전 (3인칭) 안개 적용
            RenderSettings.fogDensity = tpsFogDensity;
            RenderSettings.fogColor = tpsFogColor;
        }
    }
}
