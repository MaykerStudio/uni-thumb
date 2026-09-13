using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Editor utility that generates example prefabs for testing UniThumb.
    /// Covers three categories: simple models, particles, and UI.
    /// Accessible via Tools > UniThumb > Generate Example Prefabs.
    /// </summary>
    public static class ExamplePrefabGenerator
    {
        #region Constants

        private const string k_MenuPath = "Tools/UniThumb/Generate Example Prefabs";
        private const string k_PrefabsFolder = "Assets/UniThumb/Examples/Prefabs";
        private const string k_MaterialsFolder = "Assets/UniThumb/Examples/Prefabs/Materials";
        private const int k_MenuPriority = 1001;

        #endregion

        #region Public Methods

        [MenuItem(k_MenuPath, false, k_MenuPriority)]
        public static void GenerateExamplePrefabs()
        {
            if (
                !EditorUtility.DisplayDialog(
                    "Generate Example Prefabs",
                    "This will create example prefabs in Assets/UniThumb/Examples/Prefabs/ for thumbnail testing.\n\n"
                        + "Existing example prefabs will be overwritten.\n\nContinue?",
                    "Generate",
                    "Cancel"
                )
            )
            {
                return;
            }

            if (!UniThumbGuard.TryEnter())
            {
                Debug.LogWarning("[UniThumb] Example prefab generation already in progress.");
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Generating Example Prefabs", "Preparing...", 0f);

                EnsurePrefabsFolder();
                DeleteExistingPrefabs();

                string[] prefabNames = GetPrefabNames();
                for (int i = 0; i < prefabNames.Length; i++)
                {
                    float progress = (i + 1) / (float)prefabNames.Length;
                    EditorUtility.DisplayProgressBar(
                        "Generating Example Prefabs",
                        "Creating " + prefabNames[i] + "...",
                        progress
                    );

                    string prefabPath = k_PrefabsFolder + "/" + prefabNames[i] + ".prefab";
                    CreateExamplePrefab(i, prefabPath);
                }

                EditorUtility.DisplayProgressBar("Generating Example Prefabs", "Done", 1f);
                Shader litShader = ResolveLitShader();
                string litShaderName = litShader != null ? litShader.name : "<missing>";
                Debug.Log(
                    "[UniThumb] Generated "
                        + prefabNames.Length
                        + " example prefabs in "
                        + k_PrefabsFolder
                        + "/: "
                        + string.Join(", ", prefabNames)
                        + ". Model lit shader: "
                        + litShaderName
                );
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[UniThumb] Example prefab generation failed.");
            }
            finally
            {
                UniThumbGuard.Exit();
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }

        #endregion

        #region Private Methods - Prefab Creation

        private static void CreateExamplePrefab(int index, string prefabPath)
        {
            GameObject root = null;
            try
            {
                switch (index)
                {
                    case 0:
                        root = CreateModelCubePrefab();
                        break;
                    case 1:
                        root = CreateModelCylinderTowerPrefab();
                        break;
                    case 2:
                        root = CreateParticleBurstPrefab();
                        break;
                    case 3:
                        root = CreateParticleFountainPrefab();
                        break;
                    case 4:
                        root = CreateUIButtonPanelPrefab();
                        break;
                    case 5:
                        root = CreateUIHealthBarPrefab();
                        break;
                    default:
                        Debug.LogWarning("[UniThumb] Unknown prefab index: " + index);
                        return;
                }

                if (root == null)
                {
                    Debug.LogWarning("[UniThumb] Failed to build prefab root for " + prefabPath);
                    return;
                }

                AssetDatabase.SaveAssets();
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[UniThumb] Failed to save prefab " + prefabPath + ": " + exception.Message
                );
            }
            finally
            {
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        /// <summary>
        /// Model 01: Orange cube crate on a small base plate.
        /// </summary>
        private static GameObject CreateModelCubePrefab()
        {
            GameObject root = new GameObject("ModelCube");

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Crate";
            cube.transform.SetParent(root.transform, false);
            cube.transform.position = new Vector3(0f, 0.5f, 0f);
            SetRendererColor(cube, new Color(0.15f, 0.7f, 0.7f), 0.1f, 0.6f, "ModelCube_Crate");

            GameObject trim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            trim.name = "Trim";
            trim.transform.SetParent(root.transform, false);
            trim.transform.position = new Vector3(0f, 0.06f, 0f);
            trim.transform.localScale = new Vector3(1.4f, 0.12f, 1.4f);
            SetRendererColor(trim, new Color(0.25f, 0.22f, 0.2f), 0.6f, 0.4f, "ModelCube_Trim");

            return root;
        }

        /// <summary>
        /// Model 02: Cylinder pillar with a sphere cap.
        /// </summary>
        private static GameObject CreateModelCylinderTowerPrefab()
        {
            GameObject root = new GameObject("ModelCylinderTower");

            GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pillar.name = "Pillar";
            pillar.transform.SetParent(root.transform, false);
            pillar.transform.position = new Vector3(0f, 1f, 0f);
            pillar.transform.localScale = new Vector3(1f, 1f, 1f);
            SetRendererColor(
                pillar,
                new Color(0.9f, 0.45f, 0.1f),
                0.2f,
                0.5f,
                "ModelCylinderTower_Pillar"
            );

            GameObject cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            cap.name = "Cap";
            cap.transform.SetParent(root.transform, false);
            cap.transform.position = new Vector3(0f, 2.3f, 0f);
            cap.transform.localScale = Vector3.one * 0.7f;
            SetRendererColor(
                cap,
                new Color(0.95f, 0.8f, 0.2f),
                0.8f,
                0.7f,
                "ModelCylinderTower_Cap"
            );

            GameObject plinth = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            plinth.name = "Plinth";
            plinth.transform.SetParent(root.transform, false);
            plinth.transform.position = new Vector3(0f, 0.1f, 0f);
            plinth.transform.localScale = new Vector3(1.6f, 0.2f, 1.6f);
            SetRendererColor(
                plinth,
                new Color(0.3f, 0.3f, 0.32f),
                0.4f,
                0.3f,
                "ModelCylinderTower_Plinth"
            );

            return root;
        }

        /// <summary>
        /// Particle 01: One-shot burst / fireworks style.
        /// </summary>
        private static GameObject CreateParticleBurstPrefab()
        {
            GameObject root = new GameObject("ParticleBurstFireworks");
            ParticleSystem ps = root.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.duration = 1.5f;
            main.startLifetime = 1.5f;
            main.startSpeed = 6f;
            main.startSize = 0.25f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                Color.HSVToRGB(0f, 0.8f, 1f),
                Color.HSVToRGB(0.9f, 0.8f, 1f)
            );
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 300;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 120) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.2f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.yellow, 0f),
                    new GradientColorKey(Color.magenta, 0.5f),
                    new GradientColorKey(Color.blue, 1f),
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.6f),
                    new GradientAlphaKey(0f, 1f),
                }
            );
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                AnimationCurve.Linear(0f, 1f, 1f, 0f)
            );

            AssignParticleMaterial(root, "ParticleBurstFireworks");
            return root;
        }

        /// <summary>
        /// Particle 02: Continuous smoke / fountain style.
        /// </summary>
        private static GameObject CreateParticleFountainPrefab()
        {
            GameObject root = new GameObject("ParticleSmokeFountain");
            ParticleSystem ps = root.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.startLifetime = 2.5f;
            main.startSpeed = 3f;
            main.startSize = 0.5f;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.7f, 0.7f, 0.7f, 0.8f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 250;
            main.gravityModifier = -0.2f;

            var emission = ps.emission;
            emission.rateOverTime = 30f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 15f;
            shape.radius = 0.3f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(new Color(0.9f, 0.9f, 0.9f), 0f),
                    new GradientColorKey(new Color(0.4f, 0.4f, 0.45f), 1f),
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(0.7f, 0f),
                    new GradientAlphaKey(0f, 1f),
                }
            );
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
                1f,
                AnimationCurve.Linear(0f, 0.4f, 1f, 1.5f)
            );

            AssignParticleMaterial(root, "ParticleSmokeFountain");
            return root;
        }

        /// <summary>
        /// UI 01: Canvas with a titled button panel.
        /// </summary>
        private static GameObject CreateUIButtonPanelPrefab()
        {
            GameObject root = CreateCanvasRoot("UIButtonPanel");

            GameObject panel = CreateUIPanel(
                root.transform,
                "Panel",
                new Vector2(400f, 250f),
                Vector2.zero,
                new Color(0.12f, 0.12f, 0.16f, 0.95f)
            );

            CreateUIText(panel.transform, "Title", "Example Panel", 22, new Vector2(0f, 70f));

            GameObject buttonGO = CreateUIButton(
                panel.transform,
                "ActionButton",
                "Click Me",
                new Vector2(200f, 50f),
                new Vector2(0f, -30f)
            );
            Image buttonImage = buttonGO.GetComponent<Image>();
            if (buttonImage != null)
            {
                buttonImage.color = new Color(0.2f, 0.6f, 1f, 1f);
            }

            return root;
        }

        /// <summary>
        /// UI 02: Canvas with a health-bar / slider HUD element.
        /// </summary>
        private static GameObject CreateUIHealthBarPrefab()
        {
            GameObject root = CreateCanvasRoot("UIHealthBarHUD");

            GameObject hud = CreateUIPanel(
                root.transform,
                "HudPanel",
                new Vector2(360f, 120f),
                new Vector2(0f, 120f),
                new Color(0.08f, 0.08f, 0.1f, 0.9f)
            );

            CreateUIText(hud.transform, "Label", "HP", 20, new Vector2(-120f, 0f));

            GameObject sliderGO = new GameObject("HealthSlider");
            sliderGO.transform.SetParent(hud.transform, false);
            RectTransform sliderRT = sliderGO.AddComponent<RectTransform>();
            sliderRT.sizeDelta = new Vector2(220f, 24f);
            sliderRT.anchoredPosition = new Vector2(40f, 0f);

            Slider slider = sliderGO.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 100f;
            slider.value = 72f;

            GameObject backgroundGO = new GameObject("Background");
            backgroundGO.transform.SetParent(sliderGO.transform, false);
            RectTransform backgroundRT = backgroundGO.AddComponent<RectTransform>();
            backgroundRT.anchorMin = Vector2.zero;
            backgroundRT.anchorMax = Vector2.one;
            backgroundRT.offsetMin = Vector2.zero;
            backgroundRT.offsetMax = Vector2.zero;
            Image backgroundImage = backgroundGO.AddComponent<Image>();
            backgroundImage.color = new Color(0.25f, 0.1f, 0.1f, 1f);
            slider.targetGraphic = backgroundImage;

            GameObject fillGO = new GameObject("Fill");
            fillGO.transform.SetParent(sliderGO.transform, false);
            RectTransform fillRT = fillGO.AddComponent<RectTransform>();
            fillRT.anchorMin = new Vector2(0f, 0f);
            fillRT.anchorMax = new Vector2(0.72f, 1f);
            fillRT.offsetMin = Vector2.zero;
            fillRT.offsetMax = Vector2.zero;
            Image fillImage = fillGO.AddComponent<Image>();
            fillImage.color = new Color(0.2f, 0.8f, 0.25f, 1f);
            slider.fillRect = fillRT;

            return root;
        }

        #endregion

        #region Private Methods - Helpers

        private static string[] GetPrefabNames()
        {
            return new string[]
            {
                "ModelCube",
                "ModelCylinderTower",
                "ParticleBurstFireworks",
                "ParticleSmokeFountain",
                "UIButtonPanel",
                "UIHealthBarHUD",
            };
        }

        private static void EnsurePrefabsFolder()
        {
            EnsureFolder(k_PrefabsFolder);
            EnsureFolder(k_MaterialsFolder);
        }

        private static void EnsureFolder(string folderPath)
        {
            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static void DeleteExistingPrefabs()
        {
            if (!AssetDatabase.IsValidFolder(k_PrefabsFolder))
            {
                return;
            }

            string[] names = GetPrefabNames();
            for (int i = 0; i < names.Length; i++)
            {
                string path = k_PrefabsFolder + "/" + names[i] + ".prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
            }
        }

        private static Shader ResolveLitShader()
        {
            RenderPipelineAsset rp = GraphicsSettings.currentRenderPipeline;
            string rpName = string.Empty;
            if (rp != null)
            {
                Type rpType = rp.GetType();
                if (rpType != null)
                {
                    rpName = rpType.FullName;
                }
            }

            if (!string.IsNullOrEmpty(rpName) && rpName.Contains("HighDefinition"))
            {
                Shader hdrpLit = Shader.Find("HDRP/Lit");
                if (hdrpLit != null)
                {
                    return hdrpLit;
                }
            }
            else if (string.IsNullOrEmpty(rpName) || rpName.Contains("Universal") || rp == null)
            {
                Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
                if (urpLit != null)
                {
                    return urpLit;
                }
            }

            Shader fallback = Shader.Find("Universal Render Pipeline/Lit");
            if (fallback != null)
            {
                return fallback;
            }

            fallback = Shader.Find("HDRP/Lit");
            if (fallback != null)
            {
                return fallback;
            }

            fallback = Shader.Find("Standard");
            if (fallback != null)
            {
                return fallback;
            }

            return Shader.Find("Unlit/Color");
        }

        private static Material CreateLitMaterial(
            Color color,
            float metallic,
            float smoothness,
            string materialName
        )
        {
            Shader shader = ResolveLitShader();
            if (shader == null)
            {
                return null;
            }

            Material mat = new Material(shader);
            SetMaterialMainColor(mat, color);
            if (mat.HasProperty("_Metallic"))
            {
                mat.SetFloat("_Metallic", metallic);
            }
            if (mat.HasProperty("_Smoothness"))
            {
                mat.SetFloat("_Smoothness", smoothness);
            }
            else if (mat.HasProperty("_Glossiness"))
            {
                mat.SetFloat("_Glossiness", smoothness);
            }
            return SaveMaterialAsset(mat, materialName);
        }

        private static Material SaveMaterialAsset(Material mat, string materialName)
        {
            if (mat == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(materialName))
            {
                materialName = "Material";
            }

            EnsureFolder(k_MaterialsFolder);
            string assetPath = k_MaterialsFolder + "/" + materialName + ".mat";
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                AssetDatabase.DeleteAsset(assetPath);
            }

            AssetDatabase.CreateAsset(mat, assetPath);
            AssetDatabase.SaveAssets();
            return mat;
        }

        private static void SetMaterialMainColor(Material mat, Color color)
        {
            if (mat == null)
            {
                return;
            }

            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
            }
            else if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", color);
            }
        }

        private static void SetRendererColor(GameObject go, Color color)
        {
            string materialName = go != null ? go.name : "Material";
            SetRendererColor(go, color, 0f, 0.5f, materialName);
        }

        private static void SetRendererColor(
            GameObject go,
            Color color,
            float metallic,
            float smoothness,
            string materialName
        )
        {
            if (go == null)
            {
                return;
            }

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            Material mat = CreateLitMaterial(color, metallic, smoothness, materialName);
            if (mat == null)
            {
                return;
            }
            renderer.sharedMaterial = mat;
        }

        private static void AssignParticleMaterial(GameObject go, string materialName)
        {
            if (go == null)
            {
                return;
            }

            Renderer renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
            {
                return;
            }

            Shader particleShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (particleShader == null)
            {
                particleShader = Shader.Find("Universal Render Pipeline/Particles/Lit");
            }
            if (particleShader == null)
            {
                particleShader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            if (particleShader == null)
            {
                particleShader = Shader.Find("HDRP/Particles/Unlit");
            }
            if (particleShader == null)
            {
                particleShader = Shader.Find("HDRP/Unlit");
            }
            if (particleShader == null)
            {
                particleShader = Shader.Find("Particles/Standard Unlit");
            }
            if (particleShader != null)
            {
                Material particleMat = new Material(particleShader);
                SetMaterialMainColor(particleMat, Color.white);
                particleMat = SaveMaterialAsset(particleMat, materialName);
                if (particleMat == null)
                {
                    return;
                }
                renderer.sharedMaterial = particleMat;
            }
        }

        private static GameObject CreateCanvasRoot(string name)
        {
            GameObject root = new GameObject(name);
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            root.AddComponent<CanvasScaler>();
            root.AddComponent<GraphicRaycaster>();
            return root;
        }

        internal static Font ResolveLegacyFont(int fontSize)
        {
            Font builtin = GetBuiltinFont("LegacyRuntime.ttf");
            if (builtin != null)
            {
                return builtin;
            }

            builtin = GetBuiltinFont("Arial.ttf");
            if (builtin != null)
            {
                return builtin;
            }

            Font osFont = Font.CreateDynamicFontFromOSFont("Arial", fontSize);
            if (osFont != null)
            {
                return osFont;
            }

            return null;
        }

        private static Font GetBuiltinFont(string resourceName)
        {
            try
            {
                return Resources.GetBuiltinResource<Font>(resourceName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static GameObject CreateUIPanel(
            Transform parent,
            string name,
            Vector2 sizeDelta,
            Vector2 anchoredPosition,
            Color color
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = anchoredPosition;
            Image image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        private static GameObject CreateUIButton(
            Transform parent,
            string name,
            string label,
            Vector2 sizeDelta,
            Vector2 anchoredPosition
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = anchoredPosition;
            go.AddComponent<Image>();
            Button button = go.AddComponent<Button>();

            GameObject textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            RectTransform textRT = textGO.AddComponent<RectTransform>();
            textRT.anchorMin = Vector2.zero;
            textRT.anchorMax = Vector2.one;
            textRT.offsetMin = Vector2.zero;
            textRT.offsetMax = Vector2.zero;
            Text uiText = textGO.AddComponent<Text>();
            uiText.text = label;
            uiText.fontSize = 18;
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.color = Color.white;
            Font font = ResolveLegacyFont(18);
            if (font != null)
            {
                uiText.font = font;
            }

            if (button != null)
            {
                button.targetGraphic = go.GetComponent<Image>();
            }

            return go;
        }

        private static GameObject CreateUIText(
            Transform parent,
            string name,
            string text,
            int fontSize,
            Vector2 anchoredPosition
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(300f, 40f);
            rt.anchoredPosition = anchoredPosition;
            Text uiText = go.AddComponent<Text>();
            uiText.text = text;
            uiText.fontSize = fontSize;
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.color = Color.white;
            Font font = ResolveLegacyFont(fontSize);
            if (font != null)
            {
                uiText.font = font;
            }
            return go;
        }

        #endregion
    }
}
