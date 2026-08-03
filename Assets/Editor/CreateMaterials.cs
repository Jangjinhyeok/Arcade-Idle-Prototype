using UnityEditor;
using UnityEngine;

namespace PrisonLife
{
    /// <summary>
    /// §6.5 표 기준으로 Assets/Materials/Mat_*.mat 9개를 일괄 생성한다. ADR-002 §5.
    /// </summary>
    /// <remarks>
    /// 단발성 도구이지만 폐기하지 않고 보존 — §6.5 표 갱신 시 재실행으로 9개 일괄 동기화.
    /// 동일 경로에 .mat가 이미 있으면 shader/색상/Metallic/Smoothness/keyword를 덮어쓴다.
    /// Standard Shader + Specular Highlights/Reflections off 일괄 적용.
    /// </remarks>
    public static class CreateMaterials
    {
        private const string MaterialsFolder = "Assets/Materials";
        private const string StandardShaderName = "Standard";

        private struct MatSpec
        {
            public string Name;
            public string Hex;
            public float Metallic;
            public float Smoothness;
        }

        private static readonly MatSpec[] _specs =
        {
            new MatSpec { Name = "Mat_Blue",      Hex = "#2B5B84", Metallic = 0f,   Smoothness = 0.1f },
            new MatSpec { Name = "Mat_Orange",    Hex = "#D97E36", Metallic = 0f,   Smoothness = 0.1f },
            new MatSpec { Name = "Mat_DarkGrey",  Hex = "#4A4A4A", Metallic = 0f,   Smoothness = 0.2f },
            new MatSpec { Name = "Mat_Silver",    Hex = "#A0A0A0", Metallic = 0.4f, Smoothness = 0.5f },
            new MatSpec { Name = "Mat_Green",     Hex = "#52994C", Metallic = 0f,   Smoothness = 0f   },
            new MatSpec { Name = "Mat_Sand",      Hex = "#DBC8A0", Metallic = 0f,   Smoothness = 0f   },
            new MatSpec { Name = "Mat_LightGrey", Hex = "#CED1D4", Metallic = 0f,   Smoothness = 0.1f },
            new MatSpec { Name = "Mat_Wood",      Hex = "#8F6343", Metallic = 0f,   Smoothness = 0.1f },
            new MatSpec { Name = "Mat_Yellow",    Hex = "#E0AE36", Metallic = 0.2f, Smoothness = 0.4f },
        };

        [MenuItem("Tools/Create Materials")]
        public static void Run()
        {
            if (!AssetDatabase.IsValidFolder(MaterialsFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Materials");
            }

            var shader = Shader.Find(StandardShaderName);
            if (shader == null)
            {
                Debug.LogError($"[CreateMaterials] Shader '{StandardShaderName}' not found. Built-in RP가 아닌지 확인.");
                return;
            }

            int created = 0;
            int updated = 0;

            foreach (var spec in _specs)
            {
                string path = $"{MaterialsFolder}/{spec.Name}.mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                bool isNew = material == null;

                if (isNew)
                {
                    material = new Material(shader);
                    AssetDatabase.CreateAsset(material, path);
                    created++;
                }
                else
                {
                    material.shader = shader;
                    updated++;
                }

                if (!ColorUtility.TryParseHtmlString(spec.Hex, out var color))
                {
                    Debug.LogError($"[CreateMaterials] hex parse 실패: {spec.Name} {spec.Hex}");
                    continue;
                }

                material.SetColor("_Color", color);
                material.SetFloat("_Metallic", spec.Metallic);
                material.SetFloat("_Glossiness", spec.Smoothness);

                // §6.5: Specular Highlights / Reflections 모두 off — Standard Shader 토글 표준 패턴.
                material.SetFloat("_SpecularHighlights", 0f);
                material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                material.SetFloat("_GlossyReflections", 0f);
                material.EnableKeyword("_GLOSSYREFLECTIONS_OFF");

                EditorUtility.SetDirty(material);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[CreateMaterials] 완료. created={created} updated={updated} total={_specs.Length}");
        }
    }
}
