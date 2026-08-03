using UnityEngine;

/// <summary>
/// [도메인 명세 §3.12] 씬에 배치되어 스폰 포인트의 종류와 구역 정보를 태깅하는 마커 컴포넌트입니다.
/// SpawnManager.Initialize()가 이 컴포넌트를 읽어 포인트 목록을 채웁니다.
/// </summary>
public class SpawnPointMarker : MonoBehaviour
{
    /// <summary>
    /// 스폰 포인트의 역할 유형입니다.
    /// </summary>
    public enum SpawnType
    {
        LambNormal,         // 일반 어린양
        LambInnocent,       // 무구한 어린양
        Monster,            // 몬스터
        Chest,              // 상자
        PlayerCriminal,     // 악인 플레이어
        PlayerGreatSinner,  // 대죄인 플레이어
        Phase3Spawn,        // Phase 3 전용 스폰
        Altar,              // 제단 (각 구역에 1개, 총 4개)
        Totem,              // 토템 (각 구역에 3개, 총 12개)
    }

    [Header("스폰 포인트 설정")]
    [Tooltip("이 포인트에서 스폰될 대상의 유형입니다.")]
    public SpawnType spawnType = SpawnType.LambNormal;

    [Tooltip("이 포인트가 속한 구역 인덱스입니다. (0=ZoneA, 1=ZoneB, 2=ZoneC, 3=ZoneD, 4=All - 모든 Zone에서 유효)")]
    [SerializeField] public int zoneIndex = 0;

    [Tooltip("무구한 어린양 스폰이 가능한 포인트인지 여부입니다. (LambNormal 타입에서만 적용)")]
    [SerializeField] public bool allowInnocent = false;

    // 에디터에서 스폰 포인트를 시각적으로 확인하기 위한 기즈모
    private void OnDrawGizmos()
    {
        Color gizmoColor = spawnType switch
        {
            SpawnType.LambNormal        => Color.white,
            SpawnType.LambInnocent      => Color.cyan,
            SpawnType.Monster           => Color.red,
            SpawnType.Chest             => Color.yellow,
            SpawnType.PlayerCriminal    => Color.blue,
            SpawnType.PlayerGreatSinner => new Color(0.5f, 0f, 0.5f), // 보라
            SpawnType.Phase3Spawn       => Color.magenta,
            SpawnType.Altar             => new Color(1f, 0.84f, 0f),  // 황금색
            SpawnType.Totem             => new Color(0.6f, 0.6f, 0.6f), // 회색
            _                          => Color.gray,
        };

        Gizmos.color = gizmoColor;

        // 제단과 토템은 큐브로 표시하여 일반 스폰 포인트(구체)와 시각적 구분
        if (spawnType == SpawnType.Altar)
        {
            Gizmos.DrawCube(transform.position + Vector3.up * 1f, new Vector3(2f, 2f, 2f));
            Gizmos.DrawWireCube(transform.position + Vector3.up * 1f, new Vector3(2.2f, 2.2f, 2.2f));
        }
        else if (spawnType == SpawnType.Totem)
        {
            Gizmos.DrawCube(transform.position + Vector3.up * 1.5f, new Vector3(0.8f, 3f, 0.8f));
            Gizmos.DrawWireCube(transform.position + Vector3.up * 1.5f, new Vector3(1f, 3.2f, 1f));
        }
        else
        {
            Gizmos.DrawSphere(transform.position, 0.4f);
        }

        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.8f);
    }
}
