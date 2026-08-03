using UnityEngine;
using Unity.Netcode;
using TMPro;

public enum TotemState { Active, Destroyed, NaturalDepleted }

/// <summary>
/// [도메인 명세 §3.8] 토템 자체 인스턴스. NetworkVariable로 내구도 관리.
/// </summary>
public class TotemController : NetworkBehaviour
{
    [Tooltip("이 토템이 속한 구역 인덱스 (0=ZoneA, 1=ZoneB, 2=ZoneC, 3=ZoneD)")]
    public int zoneIndex;

    public NetworkVariable<int> currentDurability = new(0);
    public NetworkVariable<TotemState> state = new(TotemState.Active);

    private TextMeshPro _hpText;

    private void Awake()
    {
        // 렌더러와 콜라이더 불일치 완전 차단 (통과 버그 방지)
        var meshFilter = GetComponentInChildren<MeshFilter>();
        var boxCol = GetComponentInChildren<BoxCollider>();
        
        if (meshFilter != null && meshFilter.sharedMesh != null && boxCol != null)
        {
            // 월드 bounds를 쓰면 회전 시 AABB가 왜곡되어 콜라이더가 이상한 모양으로 커집니다.
            // 따라서 Mesh 자체의 순수 로컬 크기(bounds)를 가져와서 스케일만 곱해줍니다.
            Vector3 meshLocalSize = meshFilter.sharedMesh.bounds.size;
            Vector3 rendererScale = meshFilter.transform.lossyScale;
            Vector3 colScale = boxCol.transform.lossyScale;
            
            // 콜라이더 로컬 좌표계 기준의 실제 크기 계산
            Vector3 exactLocalSize = new Vector3(
                (meshLocalSize.x * rendererScale.x) / colScale.x,
                (meshLocalSize.y * rendererScale.y) / colScale.y,
                (meshLocalSize.z * rendererScale.z) / colScale.z
            );
            
            if (exactLocalSize.y > boxCol.size.y + 0.1f || exactLocalSize.y < boxCol.size.y - 0.1f)
            {
                boxCol.size = exactLocalSize;
                
                // 중심점도 Mesh의 로컬 중심을 기준으로 보정
                Vector3 worldCenter = meshFilter.transform.TransformPoint(meshFilter.sharedMesh.bounds.center);
                boxCol.center = boxCol.transform.InverseTransformPoint(worldCenter);
                
                Debug.Log($"[TotemController] {gameObject.name} 스스로 BoxCollider 크기를 Mesh에 완벽 동기화. (새 크기: {boxCol.size})");
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
        {
            if (TotemManager.Instance != null)
            {
                TotemManager.Instance.RegisterTotem(this);
            }
        }
        
        // 피격 체력 표시를 위한 이벤트 구독
        currentDurability.OnValueChanged += OnDurabilityChanged;
        state.OnValueChanged += OnStateChanged;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            if (TotemManager.Instance != null)
            {
                TotemManager.Instance.UnregisterTotem(this);
            }
        }

        currentDurability.OnValueChanged -= OnDurabilityChanged;
        state.OnValueChanged -= OnStateChanged;

        base.OnNetworkDespawn();
    }

    private void OnDurabilityChanged(int previous, int current)
    {
        if (current < previous && current > 0)
        {
            ShowHPText(current);
            SoundManager.Instance?.PlaySFX(SFXType.TotemHit, transform.position);
        }
        else if (current <= 0)
        {
            if (_hpText != null) _hpText.gameObject.SetActive(false);
        }
    }

    private void OnStateChanged(TotemState previous, TotemState current)
    {
        if (current != TotemState.Active)
        {
            if (current == TotemState.Destroyed && previous == TotemState.Active)
            {
                SoundManager.Instance?.PlaySFX(SFXType.TotemDestroyed, transform.position);
            }

            if (_hpText != null) _hpText.gameObject.SetActive(false);
            
            // 토템 파괴/소멸 시 시각 및 물리적 충돌 판정 완전히 끄기
            var renderers = GetComponentsInChildren<Renderer>();
            foreach(var r in renderers) r.enabled = false;
            
            var colliders = GetComponentsInChildren<Collider>();
            foreach(var c in colliders) c.enabled = false;
        }
        else
        {
            if (_hpText != null) _hpText.gameObject.SetActive(true);

            // 다시 활성화 시 복구 (초기화 등)
            var renderers = GetComponentsInChildren<Renderer>();
            foreach(var r in renderers) r.enabled = true;
            
            var colliders = GetComponentsInChildren<Collider>();
            foreach(var c in colliders) c.enabled = true;
        }
    }

    private void ShowHPText(int current)
    {
        if (_hpText == null)
        {
            GameObject textObj = new GameObject("HPText", typeof(TextMeshPro));
            textObj.transform.SetParent(transform);
            
            // 토템 머리 위로 텍스트 띄움
            float yOffset = 2.5f;
            var boxCol = GetComponentInChildren<BoxCollider>();
            if (boxCol != null) yOffset = boxCol.size.y * transform.localScale.y + 1f;

            textObj.transform.localPosition = new Vector3(0, yOffset, 0);
            
            _hpText = textObj.GetComponent<TextMeshPro>();
            _hpText.alignment = TextAlignmentOptions.Center;
            _hpText.fontSize = 6;
            _hpText.fontStyle = FontStyles.Bold;
            
            // TMPro 기본 폰트 할당 (런타임 생성 시 폰트 누락 방지)
            if (TMP_Settings.defaultFontAsset != null)
                _hpText.font = TMP_Settings.defaultFontAsset;
        }

        _hpText.gameObject.SetActive(true);
        _hpText.text = $"HP: {current}";
        
        if (current <= 2) _hpText.color = Color.red;
        else if (current <= 5) _hpText.color = new Color(1f, 0.5f, 0f); // 주황색
        else _hpText.color = Color.yellow;
    }

    private void Update()
    {
        // 텍스트가 켜져 있으면 항상 카메라를 바라보게 함 (빌보드 효과)
        if (_hpText != null && _hpText.gameObject.activeSelf && Camera.main != null)
        {
            _hpText.transform.rotation = Quaternion.LookRotation(_hpText.transform.position - Camera.main.transform.position);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void TakeDamage_ServerRpc(NetworkObjectReference attackerRef)
    {
        Debug.Log($"[TotemController] TakeDamage_ServerRpc 호출됨! (IsServer: {IsServer}, State: {state.Value})");
        
        if (!IsServer) return;
        if (state.Value != TotemState.Active) 
        {
            Debug.LogWarning($"[TotemController] 토템 타격 무시: 현재 활성 상태가 아님 ({state.Value})");
            return;
        }

        if (attackerRef.TryGet(out NetworkObject attacker))
        {
            bool isGreatSinner = false;

            // PlayerNetworkController에서 직접 대죄인 여부 확인
            if (attacker.TryGetComponent<PlayerNetworkController>(out var pnc))
            {
                isGreatSinner = pnc.isGreatSinner;
            }
            else
            {
                // 만약 플레이어가 아닌 개체가 공격했다면 (예외 상황) Host를 대죄인으로 간주하는 Fallback
                isGreatSinner = attacker.OwnerClientId == 0;
            }

            if (!isGreatSinner)
            {
                Debug.LogWarning($"[TotemController] 토템 공격 무시: 공격자가 대죄인이 아님 (clientId: {attacker?.OwnerClientId})");
                return;
            }

            // [방어 코드] 내구도가 0이면 InitializeForPhase2가 미호출된 상태 → 자동 초기화
            if (currentDurability.Value <= 0)
            {
                int maxDur = TotemManager.Instance != null
                    ? TotemManager.Instance.GetTotemMaxDurabilityForZone(zoneIndex)
                    : 3;
                currentDurability.Value = maxDur;
                Debug.Log($"[TotemController] Zone {zoneIndex} 토템 내구도 자동 초기화: {maxDur}");
            }

            currentDurability.Value--;
            Debug.Log($"[TotemController] Zone {zoneIndex}의 토템 피격! 남은 내구도: {currentDurability.Value}");

            if (currentDurability.Value <= 0)
            {
                state.Value = TotemState.Destroyed;
                Debug.Log($"[TotemController] Zone {zoneIndex}의 토템 파괴됨!");
                
                if (zoneIndex < 0)
                {
                    // 가짜 토템(거짓 인도/교란) 파괴 시: 진행도에 영향을 주지 않고, 파괴자를 2초 기절시킴
                    Debug.Log($"[TotemController] 가짜 토템 파괴됨! 파괴자({attacker.name}) 2초 기절 적용.");
                    if (CombatManager.Instance != null)
                    {
                        CombatManager.Instance.ApplyStatusEffect(null, attacker, StatusEffectType.Stun, 1.0f, 2.0f);
                    }
                }
                else if (TotemManager.Instance != null)
                {
                    // 진짜 토템 파괴 시: 맵 진행도(시간 단축)에 반영
                    TotemManager.Instance.OnTotemDestroyed(this);
                }
            }
        }
    }
}
