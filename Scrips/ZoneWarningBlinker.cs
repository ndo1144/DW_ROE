using UnityEngine;

public class ZoneWarningBlinker : MonoBehaviour
{
    private Renderer _renderer;
    private Material _mat;
    private Color _baseColor = new Color(1f, 0.4f, 0f, 0f); // 주황색 뼈대 (알파는 Update에서 조절)

    private GameObject _innerCube;

    private void Awake()
    {
        _renderer = GetComponent<Renderer>();
        // URP나 Standard 환경에서 반투명을 지원하는 기본 셰이더(UI용 등)를 사용해 깔끔하게 투명 렌더링
        _mat = new Material(Shader.Find("Sprites/Default"));
        _renderer.material = _mat;

        // 안쪽에서도 보이도록 면이 뒤집힌(Inverted) 내부 큐브를 자식으로 생성합니다.
        _innerCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(_innerCube.GetComponent<Collider>());
        
        _innerCube.transform.SetParent(transform);
        _innerCube.transform.localPosition = Vector3.zero;
        _innerCube.transform.localRotation = Quaternion.identity;
        // 스케일의 X축을 음수로 주면 면(Normal)이 뒤집혀 안쪽에서 보이게 됩니다.
        _innerCube.transform.localScale = new Vector3(-0.99f, 0.99f, 0.99f);
        
        var innerRenderer = _innerCube.GetComponent<Renderer>();
        innerRenderer.material = _mat; // 바깥쪽 큐브와 똑같이 깜빡이도록 동일한 재질 공유
    }

    private void Update()
    {
        // 0.3초 주기로 깜빡이도록 (sin 함수 사용)
        // 시간에 따라 alpha 값이 0.1 ~ 0.4 사이를 오가도록 설정
        float alpha = Mathf.Lerp(0.05f, 0.35f, (Mathf.Sin(Time.time * 6f) + 1f) / 2f);
        _mat.color = new Color(_baseColor.r, _baseColor.g, _baseColor.b, alpha);
    }

    private void OnDestroy()
    {
        if (_mat != null)
        {
            Destroy(_mat);
        }
    }
}
