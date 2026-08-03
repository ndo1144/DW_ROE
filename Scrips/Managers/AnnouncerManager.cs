using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Unity.Netcode;

public class AnnouncerManager : MonoBehaviour
{
    public static AnnouncerManager Instance { get; private set; }

    [Header("UI Reference")]
    [SerializeField, Tooltip("인스펙터에서 씬의 TextMeshPro 객체를 끌어다 놓으세요.")]
    private TMP_Text announcerText;

    [Header("Settings")]
    [SerializeField, Tooltip("메시지가 화면에 머무는 시간 (초)")]
    private float displayDuration = 2.0f;
    [SerializeField, Tooltip("서서히 나타나고 사라지는 데 걸리는 시간 (초)")]
    private float fadeDuration = 1.0f;

    private Queue<string> messageQueue = new Queue<string>();
    private bool isDisplaying = false;

    private void Awake()
    {
        // 싱글톤 세팅
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        if (announcerText != null)
        {
            // 시작 시 텍스트를 투명하게 만들고 비활성화
            Color c = announcerText.color;
            c.a = 0f;
            announcerText.color = c;
            announcerText.gameObject.SetActive(false);
        }
    }

    private void OnEnable()
    {
        // PlayerManager.OnPlayerDeath += HandlePlayerDeath; // (수정 필요) 해당 이벤트는 현재 프로젝트 구조상 없습니다.
        EventBus.OnPhaseTransition += HandlePhaseTransition;
        EventBus.OnLambKilled += HandleLambKilled;
    }

    private void OnDisable()
    {
        // PlayerManager.OnPlayerDeath -= HandlePlayerDeath;
        EventBus.OnPhaseTransition -= HandlePhaseTransition;
        EventBus.OnLambKilled -= HandleLambKilled;
    }

    /// <summary>
    /// 외부에서 알림 메시지를 보낼 때 호출하는 퍼블릭 메서드입니다.
    /// 예: AnnouncerManager.Instance.Announce("<color=red>대죄인</color>이 깨어났습니다!");
    /// </summary>
    public void Announce(string message)
    {
        messageQueue.Enqueue(message);
        
        // 현재 출력 중인 메시지가 없다면 코루틴 시작
        if (!isDisplaying && announcerText != null)
        {
            StartCoroutine(ProcessQueue());
        }
    }

    private IEnumerator ProcessQueue()
    {
        isDisplaying = true;
        announcerText.gameObject.SetActive(true);

        while (messageQueue.Count > 0)
        {
            string nextMessage = messageQueue.Dequeue();
            announcerText.text = nextMessage;

            // 아나운서 종류(상황)와 무관하게, 멘트가 화면에 등장할 때마다 공통 알림음을 재생한다.
            SoundManager.Instance?.PlaySFX(SFXType.AnnouncerAppear);

            // 1. 페이드 인 (서서히 나타남)
            yield return StartCoroutine(FadeText(0f, 1f, fadeDuration));

            // 2. 화면에 머무는 시간 대기
            yield return new WaitForSeconds(displayDuration);

            // 3. 페이드 아웃 (서서히 사라짐)
            yield return StartCoroutine(FadeText(1f, 0f, fadeDuration));
        }

        announcerText.gameObject.SetActive(false);
        isDisplaying = false;
    }

    private IEnumerator FadeText(float startAlpha, float endAlpha, float duration)
    {
        float elapsed = 0f;
        Color color = announcerText.color;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            color.a = Mathf.Lerp(startAlpha, endAlpha, elapsed / duration);
            announcerText.color = color;
            yield return null;
        }

        color.a = endAlpha;
        announcerText.color = color;
    }

    // ==========================================
    // 이벤트 처리 예시 (Contract 문서 기준)
    // ==========================================
    
    private void HandlePlayerDeath(PlayerSession victim, ulong killerId)
    {
        // 롤 감성: 아군이 당했습니다 느낌의 문구와 색상
        Announce($"<color=#FF4D4D>{victim.characterId}</color>님이 처치되었습니다.");
    }

    // 페이즈별로 문구를 구분한다. 로컬 플레이어의 역할(악인/대죄인)에 따라 다른 멘트를 출력한다.
    private void HandlePhaseTransition(Phase oldPhase, Phase newPhase)
    {
        bool isGreatSinner = IsLocalPlayerGreatSinner();

        switch (newPhase)
        {
            case Phase.Phase1:
                // 매치 시작(1페이즈 진입) 시점에 어린양 카운터를 초기화한다.
                // 이전에는 초기화 로직이 없어 재대결/재시작 시 이전 매치의 카운트가 그대로 남아
                // 사냥당한 어린양 수 멘트가 실제 상황과 어긋나 보이는 문제가 있었다.
                remainingLambs = totalLambs;

                Announce(isGreatSinner
                    ? "<color=#FFD700>악인들과 어린양을 모두 사냥하십시오...</color>"
                    : "<color=#FFD700>대죄인을 피해 어린양을 사냥하십시오...</color>");
                break;
            case Phase.Phase2:
                Announce(isGreatSinner
                    ? "<color=#FFD700> 천상의 업화를 피해 악인들을 모두 사냥하십시오...</color>"
                    : "<color=#FFD700> 천상의 업화를 피해 대죄인을 처치하십시오...</color>");
                break;
            case Phase.Phase3:
                Announce(isGreatSinner
                    ? "<color=#FFD700> 당신은 실패하였지만, 이야기의 끝은 지켜볼 수 있습니다...</color>"
                    : "<color=#FFD700> 최후의 1인이 되는 자, 모든 죄를 사하노라...</color>");
                break;
            // PreMatch/PostMatch는 아나운서 멘트 대상이 아니므로 무시한다.
        }
    }

    // 로컬 플레이어(본인)가 대죄인 역할인지 확인한다.
    private bool IsLocalPlayerGreatSinner()
    {
        if (PlayerNetworkController.AllPlayers == null) return false;

        foreach (var p in PlayerNetworkController.AllPlayers)
        {
            if (p != null && p.IsOwner)
            {
                return p.isGreatSinner;
            }
        }

        return false;
    }

    [Header("Lamb Settings")]
    [SerializeField, Tooltip("초기 시작 시 전체 어린양 마리 수")]
    private int totalLambs = 99;
    [SerializeField, Tooltip("현재 남은 어린양 마리 수 (매치 시작 시 totalLambs와 동일하게 초기화 필요)")]
    private int remainingLambs = 99;

    private void HandleLambKilled(NetworkObject lamb, NetworkObject killer)
    {
        remainingLambs--;
        int sacrificedLambs = totalLambs - remainingLambs;

        if (remainingLambs == 69)
        {
            Announce($"<color=#FFD700>{sacrificedLambs}마리 분의 죄악이 고해졌습니다.</color>");
        }
        else if (remainingLambs == 49)
        {
            Announce($"<color=#FF4500>{sacrificedLambs}마리 분의 죄악이 고해졌습니다.</color>");
        }
        else if (remainingLambs == 29)
        {
            Announce($"<color=#FF0000>{sacrificedLambs}마리 분의 죄악이 고해졌습니다.</color>");
        }
    }
    
}
