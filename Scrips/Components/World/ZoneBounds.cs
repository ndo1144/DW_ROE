using UnityEngine;

/// <summary>
/// Zone Plane(또는 바닥 오브젝트)에 부착하여 해당 구역의 영역을 정의합니다.
/// SpawnManager가 이 컴포넌트를 읽어 영역 내부의 랜덤 좌표를 생성합니다.
/// </summary>
public class ZoneBounds : MonoBehaviour
{
    [Tooltip("이 Zone의 인덱스입니다. (0=ZoneA, 1=ZoneB, 2=ZoneC, 3=ZoneD)")]
    public int zoneIndex = 0;

    public static readonly System.Collections.Generic.List<ZoneBounds> AllZones = new();

    /// <summary>
    /// UI 등에 표시되는 구역별 명칭입니다. (0=ZoneA, 1=ZoneB, 2=ZoneC, 3=ZoneD)
    /// </summary>
    public static readonly string[] DisplayNames = { "대성당", "납골당", "수도원", "고문소" };

    /// <summary>
    /// 구역 인덱스에 해당하는 표시용 명칭을 반환합니다. (범위 밖이면 "Zone {알파벳}"으로 폴백)
    /// </summary>
    public static string GetDisplayName(int zoneIndex)
    {
        if (zoneIndex >= 0 && zoneIndex < DisplayNames.Length) return DisplayNames[zoneIndex];
        return $"Zone {(char)('A' + zoneIndex)}";
    }

    private void OnEnable() => AllZones.Add(this);
    private void OnDisable() => AllZones.Remove(this);

    public static bool IsPositionInSealedZone(Vector3 position)
    {
        if (PhaseManager.Instance == null) return false;
        foreach (var zone in AllZones)
        {
            if (PhaseManager.Instance.IsZoneSealed(zone.zoneIndex))
            {
                if (zone.Contains(position)) return true;
            }
        }
        return false;
    }

    [Tooltip("스폰 영역을 바닥 면(Plane/Collider) 안쪽으로 얼마나 축소할지 비율입니다. 0.1이면 가장자리 10%를 제외합니다.")]
    [Range(0f, 0.4f)]
    public float edgePadding = 0.1f;

    [Tooltip("스폰 오브젝트가 바닥 위로 얼마나 띄워져야 하는지(Y 오프셋)입니다.")]
    public float spawnHeightOffset = 0.5f;

    [Header("데미지 영역 판정")]
    [Tooltip("콜라이더가 없는 경우, 이 구역이 바닥으로부터 얼마나 높게 데미지 판정을 가질지 결정합니다. (위아래 층이 겹치지 않게 조절하세요)")]
    public float zoneHeight = 15f;

    /// <summary>
    /// 이 Zone 영역 내부의 랜덤 월드 좌표를 반환합니다.
    /// Plane의 Transform 위치/스케일을 기반으로 계산합니다.
    /// </summary>
    public Vector3 GetRandomPointInZone()
    {
        float halfX = 5f * transform.lossyScale.x * (1f - edgePadding);
        float halfZ = 5f * transform.lossyScale.z * (1f - edgePadding);

        // 계단, 다리, Not Walkable 등을 제외하고 'Walkable' 내비메쉬 영역만 스폰을 허용합니다.
        int walkableMask = 1 << UnityEngine.AI.NavMesh.GetAreaFromName("Walkable");

        // 유효한 스폰 위치를 찾기 위해 최대 30번 재시도합니다.
        for (int i = 0; i < 30; i++)
        {
            float randX = Random.Range(-halfX, halfX);
            float randZ = Random.Range(-halfZ, halfZ);

            Vector3 localPoint = new Vector3(randX, 0f, randZ);
            Vector3 worldPoint = transform.position + transform.rotation * localPoint;

            // Y를 바닥에서부터 구역 전체 높이(zoneHeight) 사이의 무작위 값으로 설정
            worldPoint.y = transform.position.y + 1f;

            // 무작위 위치 반경 2m 이내에 Walkable 내비메쉬가 있는지 검사 (다리나 계단, 허공에서는 false가 됨)
            if (UnityEngine.AI.NavMesh.SamplePosition(worldPoint, out UnityEngine.AI.NavMeshHit hit, 5f, walkableMask))
            {
                return hit.position + Vector3.up * spawnHeightOffset;
            }
        }

        // 30번 모두 실패한 경우 (구역 내에 안전한 땅이 너무 좁거나 없는 극단적인 상황)
        // 안전장치로 Transform의 원점을 반환하되, 경고 로그를 남깁니다.
        Debug.LogWarning($"[ZoneBounds] 구역 {zoneIndex}에서 30번의 재시도 끝에 안전한 Walkable 내비메쉬 스폰 위치를 찾지 못했습니다. 기본 위치로 폴백합니다.");
        return transform.position + Vector3.up * spawnHeightOffset;
    }

    /// <summary>
    /// 서로 최소 거리(minDistance)를 유지하는 랜덤 좌표를 count개 반환합니다.
    /// </summary>
    public Vector3[] GetRandomPointsInZone(int count, float minDistance = 5f, int maxAttempts = 100)
    {
        var points = new System.Collections.Generic.List<Vector3>();

        for (int i = 0; i < count; i++)
        {
            bool placed = false;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                Vector3 candidate = GetRandomPointInZone();
                bool tooClose = false;

                foreach (var existing in points)
                {
                    if (Vector3.Distance(candidate, existing) < minDistance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    points.Add(candidate);
                    placed = true;
                    break;
                }
            }

            // 최소 거리를 만족하는 위치를 찾지 못하면 그냥 아무 데나 배치
            if (!placed)
            {
                points.Add(GetRandomPointInZone());
            }
        }

        return points.ToArray();
    }

    /// <summary>
    /// 지정된 위치가 이 Zone 영역 내부에 포함되는지 여부를 판단합니다.
    /// </summary>
    public bool Contains(Vector3 position)
    {
        // 1. BoxCollider가 있다면 회전, 스케일, Center 오프셋을 모두 고려하여 완벽하게 판정합니다.
        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
        {
            // 월드 좌표를 BoxCollider의 로컬 좌표계로 변환 후 Center 오프셋 적용
            Vector3 localP = transform.InverseTransformPoint(position) - box.center;
            // 로컬 기준에서 Box 사이즈의 절반 이내에 들어오면 포함된 것 (Y축 즉, 높이도 판정)
            return Mathf.Abs(localP.x) <= box.size.x * 0.5f &&
                   Mathf.Abs(localP.y) <= box.size.y * 0.5f &&
                   Mathf.Abs(localP.z) <= box.size.z * 0.5f;
        }

        // 2. 콜라이더가 없다면 기즈모(OnDrawGizmos)와 정확히 일치하는 평면(10x10) 수학 판정 폴백
        // InverseTransformPoint는 이미 스케일을 역산하여 로컬 좌표로 변환하므로, 
        // 로컬 기준 크기인 5f (10x10 Plane 기준) 와 직접 비교해야 합니다.
        Vector3 localPos = transform.InverseTransformPoint(position);
        
        // 평면 수학 판정에서는 X, Z 축 범위 확인 (5f) 및 Y축 범위 (지하 방지를 위해 0 ~ zoneHeight)를 확인합니다.
        // 언덕 등 지형 굴곡을 감안해 아래로 약간(-2f) 여유를 둡니다.
        // 단, Y축 높이는 스케일의 영향을 받지 않는 절대 높이(월드 높이)를 의도하는 경우가 많으므로 로컬 스케일을 다시 곱해 계산합니다.
        float worldYOffset = localPos.y * transform.lossyScale.y;
        
        return Mathf.Abs(localPos.x) <= 5f && 
               Mathf.Abs(localPos.z) <= 5f && 
               worldYOffset >= -2f && 
               worldYOffset <= zoneHeight;
    }

    // 에디터에서 Zone 영역을 시각적으로 확인
    private void OnDrawGizmos()
    {
        float halfX = 5f * Mathf.Abs(transform.lossyScale.x) * (1f - edgePadding);
        float halfZ = 5f * Mathf.Abs(transform.lossyScale.z) * (1f - edgePadding);

        Gizmos.color = new Color(0f, 1f, 0f, 0.15f);
        // 바닥에서부터 zoneHeight만큼의 상자를 그리도록 중심점과 크기 조절
        Vector3 center = transform.position + transform.up * (zoneHeight * 0.5f);
        Vector3 size = new Vector3(halfX * 2f, zoneHeight, halfZ * 2f);
        
        // 회전값 적용하여 그리기
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(center, transform.rotation, Vector3.one);
        
        Gizmos.DrawCube(Vector3.zero, size);

        Gizmos.color = new Color(0f, 1f, 0f, 0.5f);
        Gizmos.DrawWireCube(Vector3.zero, size);
        
        Gizmos.matrix = oldMatrix;
    }
}
