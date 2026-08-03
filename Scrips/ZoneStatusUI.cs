using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Phase 2 제한구역 현황 UI.
/// 4개 구역(A~D)의 봉인/활성/대기 상태와 현재 구역 잔여 시간을 표시합니다.
/// HUD 캔버스 하위에 배치되며, Phase 2 진입 시 활성화 / Phase 2 종료 시 비활성화됩니다.
/// </summary>
public class ZoneStatusUI : MonoBehaviour
{
    [Header("Zone 슬롯 (인스펙터에서 등록 또는 자동 생성)")]
    public ZoneSlot[] zoneSlots = new ZoneSlot[4];

    [Header("타이머")]
    public TextMeshProUGUI timerText;

    [Header("제목")]
    public TextMeshProUGUI titleText;

    // 봉인 상태 추적
    private bool[] _sealed = new bool[4];
    private int _currentZoneIndex = -1;
    private float _timeRemaining = 0f;

    // 기믹(석상 파괴)으로 시간이 급감했을 때 상단 텍스트를 붉게 깜빡이는 연출용
    private const float FLASH_DURATION = 0.6f;
    private float _flashTimer = 0f;

    // ─────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────

    private void Awake()
    {
        // 에디터 생성 직후 직렬화가 유실된 경우를 대비한 자동 바인딩
        for (int i = 0; i < 4; i++)
        {
            if (zoneSlots[i] == null || zoneSlots[i].background == null)
            {
                Transform slotT = transform.Find($"Zone_{(char)('A' + i)}");
                if (slotT != null)
                {
                    zoneSlots[i] = new ZoneSlot
                    {
                        background = slotT.GetComponent<Image>(),
                        label = slotT.Find("Label")?.GetComponent<TextMeshProUGUI>()
                    };
                }
            }
        }

        // 슬롯 라벨을 구역 명칭(대성당/납골당/수도원/고문소)으로 갱신
        for (int i = 0; i < 4; i++)
        {
            if (zoneSlots[i] != null && zoneSlots[i].label != null)
                zoneSlots[i].label.text = ZoneBounds.GetDisplayName(i);
        }

        // [폰트 통일] 제목/타이머가 슬롯 라벨과 다른 폰트(빌드에서 한글 글리프가 □ 박스로 깨지는 폰트)를
        // 쓰는 경우를 방지: 정상 렌더되는 슬롯 라벨의 폰트로 제목·타이머를 맞춥니다.
        TMP_FontAsset refFont = null;
        for (int i = 0; i < 4; i++)
        {
            if (zoneSlots[i] != null && zoneSlots[i].label != null && zoneSlots[i].label.font != null)
            {
                refFont = zoneSlots[i].label.font;
                break;
            }
        }
        if (refFont != null)
        {
            if (titleText != null) titleText.font = refFont;
            if (timerText != null) timerText.font = refFont;
        }

        EventBus.OnPhaseTransition += OnPhaseTransition;
        EventBus.OnZoneSeal += OnZoneSeal;
        EventBus.OnZoneTimeUpdate += OnZoneTimeUpdate;
        EventBus.OnZoneTimeReduced += OnZoneTimeReduced;
    }

    private void OnDestroy()
    {
        EventBus.OnPhaseTransition -= OnPhaseTransition;
        EventBus.OnZoneSeal -= OnZoneSeal;
        EventBus.OnZoneTimeUpdate -= OnZoneTimeUpdate;
        EventBus.OnZoneTimeReduced -= OnZoneTimeReduced;
    }

    private void Start()
    {
        // 초기 상태: 전부 대기
        for (int i = 0; i < 4; i++)
        {
            _sealed[i] = false;
            if (zoneSlots[i] != null)
                zoneSlots[i].SetState(ZoneState.Pending);
        }

        // Phase 2가 아니면 숨기기
        if (PhaseManager.Instance == null || PhaseManager.Instance.CurrentPhase != Phase.Phase2)
            gameObject.SetActive(false);
    }

    // ─────────────────────────────────────────────────────────
    // 이벤트 핸들러
    // ─────────────────────────────────────────────────────────

    private void OnPhaseTransition(Phase from, Phase to)
    {
        if (to == Phase.Phase2)
        {
            gameObject.SetActive(true);
            ResetAll();
            SyncWithPhaseManager();
        }
        else
        {
            gameObject.SetActive(false);
        }
    }

    private void OnZoneSeal(int zoneIndex)
    {
        if (zoneIndex < 0 || zoneIndex >= 4) return;
        _sealed[zoneIndex] = true;
        if (zoneSlots[zoneIndex] != null)
            zoneSlots[zoneIndex].SetState(ZoneState.Sealed);

        // PhaseManager에서 다음 구역으로 전환하므로 동기화
        SyncWithPhaseManager();
    }

    private void OnZoneTimeUpdate(float remaining)
    {
        _timeRemaining = remaining;
        UpdateTimerDisplay();
    }

    // 석상 파괴 기믹으로 제한시간이 급감했을 때 호출 → 붉은 플래시 시작
    private void OnZoneTimeReduced()
    {
        _flashTimer = FLASH_DURATION;
    }

    // ─────────────────────────────────────────────────────────
    // Update (PhaseManager 직접 폴링 — 보조)
    // ─────────────────────────────────────────────────────────

    private void Update()
    {
        // 플래시 타이머는 페이즈와 무관하게 프레임당 1회만 감소시킨다.
        if (_flashTimer > 0f)
            _flashTimer = Mathf.Max(0f, _flashTimer - Time.deltaTime);

        if (PhaseManager.Instance == null) return;
        if (PhaseManager.Instance.CurrentPhase != Phase.Phase2) return;

        // PhaseManager의 현재 구역이 바뀌었으면 UI 갱신
        if (_currentZoneIndex != PhaseManager.Instance.CurrentZoneIndex)
        {
            SyncWithPhaseManager();
        }

        // 타이머 폴링 (이벤트 누락 대비)
        _timeRemaining = PhaseManager.Instance.ZoneTimeRemaining;
        UpdateTimerDisplay();
    }

    // ─────────────────────────────────────────────────────────
    // 내부 로직
    // ─────────────────────────────────────────────────────────

    private void SyncWithPhaseManager()
    {
        if (PhaseManager.Instance == null) return;

        _currentZoneIndex = PhaseManager.Instance.CurrentZoneIndex;

        for (int i = 0; i < 4; i++)
        {
            if (_sealed[i])
            {
                if (zoneSlots[i] != null)
                    zoneSlots[i].SetState(ZoneState.Sealed);
            }
            else if (i == _currentZoneIndex)
            {
                if (zoneSlots[i] != null)
                    zoneSlots[i].SetState(ZoneState.Active);
            }
            else
            {
                if (zoneSlots[i] != null)
                    zoneSlots[i].SetState(ZoneState.Pending);
            }
        }
    }

    private void UpdateTimerDisplay()
    {
        if (timerText == null) return;

        if (_currentZoneIndex < 0 || _currentZoneIndex >= 4)
        {
            timerText.text = "";
            return;
        }

        int minutes = Mathf.FloorToInt(_timeRemaining / 60f);
        int seconds = Mathf.FloorToInt(_timeRemaining % 60f);
        string zoneName = ZoneBounds.GetDisplayName(_currentZoneIndex);

        int nextZoneIdx = PhaseManager.Instance != null ? PhaseManager.Instance.GetNextZoneIndex() : -1;
        string nextStr = nextZoneIdx != -1 ? $"   <size=12><color=#AAAAAA>Next: {ZoneBounds.GetDisplayName(nextZoneIdx)}</color></size>" : "";

        timerText.text = $"{zoneName}  {minutes:00}:{seconds:00}{nextStr}";

        // 기본 색: 30초 이하일 때 빨간색 경고, 그 외 흰색
        Color baseColor = _timeRemaining <= 30f
            ? new Color(1f, 0.3f, 0.3f, 1f)
            : Color.white;

        // 석상 파괴로 시간이 급감한 직후에는 선명한 빨강에서 기본 색으로 서서히 복귀
        if (_flashTimer > 0f)
        {
            float t = _flashTimer / FLASH_DURATION; // 1(방금 발동) → 0(복귀 완료)
            timerText.color = Color.Lerp(baseColor, Color.red, t);
        }
        else
        {
            timerText.color = baseColor;
        }
    }

    private void ResetAll()
    {
        for (int i = 0; i < 4; i++)
        {
            _sealed[i] = false;
            if (zoneSlots[i] != null)
                zoneSlots[i].SetState(ZoneState.Pending);
        }
        _currentZoneIndex = -1;
    }

    // ─────────────────────────────────────────────────────────
    // 상태 정의
    // ─────────────────────────────────────────────────────────

    public enum ZoneState { Pending, Active, Sealed }

    // ─────────────────────────────────────────────────────────
    // Zone 슬롯 UI (인라인 클래스)
    // ─────────────────────────────────────────────────────────

    [System.Serializable]
    public class ZoneSlot
    {
        public Image background;
        public TextMeshProUGUI label;

        // 상태별 색상
        private static readonly Color COLOR_PENDING = new Color(0.3f, 0.3f, 0.3f, 0.8f);  // 회색 (대기)
        private static readonly Color COLOR_ACTIVE  = new Color(0.9f, 0.5f, 0.1f, 0.9f);  // 주황 (활성)
        private static readonly Color COLOR_SEALED  = new Color(0.8f, 0.15f, 0.15f, 0.9f); // 빨강 (봉인)

        public void SetState(ZoneState state)
        {
            if (background != null)
            {
                background.color = state switch
                {
                    ZoneState.Pending => COLOR_PENDING,
                    ZoneState.Active  => COLOR_ACTIVE,
                    ZoneState.Sealed  => COLOR_SEALED,
                    _ => COLOR_PENDING
                };
            }

            if (label != null)
            {
                label.fontStyle = state == ZoneState.Active
                    ? FontStyles.Bold
                    : FontStyles.Normal;

                label.color = state == ZoneState.Sealed
                    ? new Color(1f, 1f, 1f, 0.4f)  // 반투명
                    : Color.white;
            }
        }
    }
}
