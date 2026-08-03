using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

public class SoundConfigAutoAssigner : EditorWindow
{
    private SoundConfigSO config;
    private string sfxFolderPath = "Assets/Audio"; // 기본 폴더 경로

    [MenuItem("Tools/사운드 자동 할당 툴 (SFX Assigner)")]
    public static void ShowWindow()
    {
        GetWindow<SoundConfigAutoAssigner>("SFX 자동 할당 툴");
    }

    private void OnGUI()
    {
        GUILayout.Label("SFX 클립 자동 매핑 도구", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "이 툴은 파일 이름을 분석하여 자동으로 SoundConfigSO에 사운드를 할당해 줍니다.\n" +
            "규칙: 파일 이름이 SFXType의 이름으로 시작해야 합니다.\n" +
            "예시: PlayerFootstep_01.wav, PlayerFootstep_02.wav -> PlayerFootstep에 자동 할당됨", MessageType.Info);

        GUILayout.Space(10);
        
        config = (SoundConfigSO)EditorGUILayout.ObjectField("SoundConfigSO 에셋", config, typeof(SoundConfigSO), false);
        sfxFolderPath = EditorGUILayout.TextField("오디오 폴더 경로", sfxFolderPath);

        GUILayout.Space(20);

        GUI.color = Color.green;
        if (GUILayout.Button("자동 할당 실행", GUILayout.Height(40)))
        {
            if (config == null)
            {
                EditorUtility.DisplayDialog("오류", "SoundConfigSO 에셋을 빈칸에 넣어주세요.", "확인");
                return;
            }
            AutoAssign();
        }
        GUI.color = Color.white;
    }

    private void AutoAssign()
    {
        // 1. 해당 폴더에서 모든 오디오 클립 검색
        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { sfxFolderPath });
        
        // Enum 타입별로 발견된 클립들을 모아둘 딕셔너리
        Dictionary<SFXType, List<AudioClip>> clipDict = new Dictionary<SFXType, List<AudioClip>>();

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;

            string fileName = clip.name;
            SFXType matchedType = SFXType.None;
            int longestMatchLength = 0;

            // 2. 파일 이름과 Enum 이름 매칭 (가장 길게 일치하는 Enum을 찾음 - 예: ItemUse vs ItemUse_Epic)
            foreach(SFXType type in System.Enum.GetValues(typeof(SFXType)))
            {
                if (type == SFXType.None) continue;
                
                string typeName = type.ToString();
                // 파일 이름이 Enum 이름으로 시작하는지 확인 (대소문자 무시)
                if (fileName.StartsWith(typeName, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (typeName.Length > longestMatchLength)
                    {
                        matchedType = type;
                        longestMatchLength = typeName.Length;
                    }
                }
            }

            // 매칭된 타입이 있다면 딕셔너리에 추가
            if (matchedType != SFXType.None)
            {
                if (!clipDict.ContainsKey(matchedType))
                {
                    clipDict[matchedType] = new List<AudioClip>();
                }
                clipDict[matchedType].Add(clip);
            }
        }

        // 3. 찾은 클립들을 SoundConfigSO에 적용
        List<SFXEntry> existingEntries = config.sfxEntries != null ? config.sfxEntries.ToList() : new List<SFXEntry>();

        foreach (var kvp in clipDict)
        {
            SFXType type = kvp.Key;
            List<AudioClip> clips = kvp.Value;

            SFXEntry existing = existingEntries.FirstOrDefault(e => e.type == type);
            if (existing != null)
            {
                // 이미 항목이 있으면 클립 리스트만 덮어씀
                existing.clips = clips.ToArray();
            }
            else
            {
                // 항목이 아예 없으면 새로 만들어서 추가
                SFXEntry newEntry = new SFXEntry();
                newEntry.type = type;
                newEntry.clips = clips.ToArray();
                newEntry.volume = 1.0f;
                newEntry.pitchVariation = 0.1f; // 자연스러운 소리를 위해 기본 피치 변형 0.1 설정
                newEntry.cooldown = 0f;
                existingEntries.Add(newEntry);
            }
        }

        config.sfxEntries = existingEntries.ToArray();
        
        // 변경사항 저장
        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();
        
        EditorUtility.DisplayDialog("할당 완료!", $"총 {clipDict.Count}개의 SFX 항목이 자동으로 연결되었습니다.\n인스펙터를 확인해 보세요.", "확인");
    }
}
