using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Editor utility that generates 10 diverse example scenes for testing the
    /// UniThumb. Each scene has unique content covering different
    /// thumbnail scenarios: lighting, color, camera angles, UI, particles, etc.
    /// Accessible via Tools > UniThumb > Generate Example Scenes.
    /// </summary>
    public static class ExampleSceneGenerator
    {
        #region Constants

        private const string k_MenuPath = "Tools/UniThumb/Generate Example Scenes";
        private const string k_ScenesFolder = "Assets/Scenes";
        private const int k_MenuPriority = 1000;
        private const int k_SceneCount = 10;

        #endregion

        #region Public Methods

        [MenuItem(k_MenuPath, false, k_MenuPriority)]
        public static void GenerateExampleScenes()
        {
            if (
                !EditorUtility.DisplayDialog(
                    "Generate Example Scenes",
                    "This will create 10 example scenes in Assets/Scenes/ for thumbnail testing.\n\n"
                        + "Existing example scenes will be overwritten.\n\nContinue?",
                    "Generate",
                    "Cancel"
                )
            )
            {
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Generating Example Scenes", "Preparing...", 0f);

                EnsureScenesFolder();
                string originalScenePath = EditorSceneManager.GetActiveScene().path;

                for (int i = 0; i < k_SceneCount; i++)
                {
                    float progress = (i + 1) / (float)k_SceneCount;
                    string sceneName = GetSceneName(i);
                    EditorUtility.DisplayProgressBar(
                        "Generating Example Scenes",
                        "Creating " + sceneName + "...",
                        progress
                    );

                    string scenePath = k_ScenesFolder + "/" + sceneName + ".unity";
                    CreateExampleScene(i, scenePath);
                }

                RestoreOriginalScene(originalScenePath);
                EditorUtility.DisplayProgressBar("Generating Example Scenes", "Done", 1f);
                Debug.Log(
                    "[UniThumb] Generated "
                        + k_SceneCount
                        + " example scenes in "
                        + k_ScenesFolder
                        + "/"
                );
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[UniThumb] Example scene generation failed.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }

        #endregion

        #region Private Methods - Scene Creation

        private static void CreateExampleScene(int index, string scenePath)
        {
            Scene scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );

            switch (index)
            {
                case 0:
                    CreateBasicLitScene();
                    break;
                case 1:
                    CreateMultiLightScene();
                    break;
                case 2:
                    CreateTopDownScene();
                    break;
                case 3:
                    CreateCloseUpScene();
                    break;
                case 4:
                    CreateDarkScene();
                    break;
                case 5:
                    CreateColorfulScene();
                    break;
                case 6:
                    CreateReflectionScene();
                    break;
                case 7:
                    CreateTerrainScene();
                    break;
                case 8:
                    CreateUIScene();
                    break;
                case 9:
                    CreateParticleScene();
                    break;
            }

            EditorSceneManager.SaveScene(scene, scenePath);
        }

        /// <summary>
        /// Scene 01: Single white cube at origin, directional light, camera at (0,1,-5).
        /// Clean, simple lit scene.
        /// </summary>
        private static void CreateBasicLitScene()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = Vector3.zero;
            SetMaterialColor(cube, Color.white);

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.2f;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            CreateCamera(new Vector3(0f, 1f, -5f), Vector3.zero);
        }

        /// <summary>
        /// Scene 02: Three colored spheres, three colored point lights, dark background.
        /// Tests multi-light color rendering.
        /// </summary>
        private static void CreateMultiLightScene()
        {
            CreateSphereAt(-2f, 0f, 0f, Color.red, "SphereRed");
            CreateSphereAt(0f, 0f, 0f, Color.green, "SphereGreen");
            CreateSphereAt(2f, 0f, 0f, Color.blue, "SphereBlue");

            CreatePointLight(new Vector3(-2f, 2f, 0f), Color.red, "LightRed");
            CreatePointLight(new Vector3(0f, 2f, 0f), Color.green, "LightGreen");
            CreatePointLight(new Vector3(2f, 2f, 0f), Color.blue, "LightBlue");

            CreateCamera(new Vector3(0f, 2f, -5f), Vector3.zero);
        }

        /// <summary>
        /// Scene 03: Top-down camera, 5 randomly placed cubes on a plane.
        /// Tests overhead/birds-eye thumbnail perspective.
        /// </summary>
        private static void CreateTopDownScene()
        {
            GameObject plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.transform.position = Vector3.zero;
            plane.transform.localScale = new Vector3(2f, 1f, 2f);
            SetMaterialColor(plane, new Color(0.3f, 0.5f, 0.3f));

            UnityEngine.Random.State savedState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(42);

            for (int i = 0; i < 5; i++)
            {
                float x = UnityEngine.Random.Range(-3f, 3f);
                float z = UnityEngine.Random.Range(-3f, 3f);
                float scale = UnityEngine.Random.Range(0.3f, 0.8f);
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.position = new Vector3(x, scale * 0.5f, z);
                cube.transform.localScale = Vector3.one * scale;
                cube.transform.rotation = Quaternion.Euler(
                    0f,
                    UnityEngine.Random.Range(0f, 360f),
                    0f
                );
                Color color = Color.HSVToRGB(UnityEngine.Random.Range(0f, 1f), 0.7f, 0.9f);
                SetMaterialColor(cube, color);
            }

            UnityEngine.Random.state = savedState;

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.0f;
            lightGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            Camera cam = CreateCamera(new Vector3(0f, 10f, 0f), Vector3.zero);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>
        /// Scene 04: Large sphere filling most of the view, camera very close.
        /// Tests close-up / detail thumbnail scenarios.
        /// </summary>
        private static void CreateCloseUpScene()
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.position = Vector3.zero;
            sphere.transform.localScale = Vector3.one * 3f;
            SetMaterialColor(sphere, new Color(0.8f, 0.2f, 0.2f));

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.5f;
            lightGO.transform.rotation = Quaternion.Euler(30f, 60f, 0f);

            CreateCamera(new Vector3(0f, 0.5f, -2f), Vector3.zero);
        }

        /// <summary>
        /// Scene 05: Single cube with very dim light. Dark, moody atmosphere.
        /// Tests low-light / dark thumbnail rendering.
        /// </summary>
        private static void CreateDarkScene()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = Vector3.zero;
            SetMaterialColor(cube, new Color(0.15f, 0.15f, 0.15f));

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.6f, 0.6f, 0.7f);
            light.intensity = 0.2f;
            lightGO.transform.rotation = Quaternion.Euler(45f, -20f, 0f);

            CreateCamera(new Vector3(0f, 1f, -5f), Vector3.zero);
        }

        /// <summary>
        /// Scene 06: Four cubes with bright emissive materials, no extra lights.
        /// Tests emission-based color in thumbnails.
        /// </summary>
        private static void CreateColorfulScene()
        {
            Color[] emissiveColors = new Color[]
            {
                Color.red,
                Color.yellow,
                Color.cyan,
                Color.magenta,
            };
            Vector3[] positions = new Vector3[]
            {
                new Vector3(-1.5f, 0f, 0f),
                new Vector3(-0.5f, 0f, 0f),
                new Vector3(0.5f, 0f, 0f),
                new Vector3(1.5f, 0f, 0f),
            };
            string[] names = new string[]
            {
                "EmissiveRed",
                "EmissiveYellow",
                "EmissiveCyan",
                "EmissiveMagenta",
            };

            for (int i = 0; i < 4; i++)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.position = positions[i];
                cube.name = names[i];
                Material mat = new Material(GetLitShader());
                mat.SetColor("_BaseColor", emissiveColors[i] * 0.5f);
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emissiveColors[i] * 2f);
                cube.GetComponent<Renderer>().material = mat;
            }

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 0.3f;
            lightGO.transform.rotation = Quaternion.Euler(30f, -45f, 0f);

            CreateCamera(new Vector3(0f, 2f, -5f), Vector3.zero);
        }

        /// <summary>
        /// Scene 07: Three metallic spheres with high smoothness.
        /// Tests reflection and specular highlights in thumbnails.
        /// </summary>
        private static void CreateReflectionScene()
        {
            for (int i = 0; i < 3; i++)
            {
                float x = (i - 1) * 2.5f;
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.position = new Vector3(x, 0f, 0f);
                sphere.name = "MetalSphere_" + i;

                Material mat = new Material(GetLitShader());
                mat.SetFloat("_Metallic", 0.9f);
                mat.SetFloat("_Smoothness", 0.95f);
                mat.SetColor("_BaseColor", new Color(0.8f, 0.8f, 0.85f));
                sphere.GetComponent<Renderer>().material = mat;
            }

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.position = new Vector3(0f, -1f, 0f);
            floor.transform.localScale = new Vector3(3f, 1f, 3f);
            Material floorMat = new Material(GetLitShader());
            floorMat.SetFloat("_Metallic", 0.3f);
            floorMat.SetFloat("_Smoothness", 0.8f);
            floorMat.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.25f));
            floor.GetComponent<Renderer>().material = floorMat;

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.5f;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            CreateCamera(new Vector3(0f, 1f, -5f), Vector3.zero);
        }

        /// <summary>
        /// Scene 08: Simple terrain with a few hills. Elevated camera looking down.
        /// Tests terrain thumbnail rendering.
        /// </summary>
        private static void CreateTerrainScene()
        {
            GameObject terrainGO = Terrain.CreateTerrainGameObject(new TerrainData());
            Terrain terrain = terrainGO.GetComponent<Terrain>();
            TerrainData terrainData = terrain.terrainData;

            int heightmapRes = 33;
            terrainData.heightmapResolution = heightmapRes;
            terrainData.size = new Vector3(50f, 10f, 50f);

            float[,] heights = new float[heightmapRes, heightmapRes];
            for (int z = 0; z < heightmapRes; z++)
            {
                for (int x = 0; x < heightmapRes; x++)
                {
                    float nx = x / (float)(heightmapRes - 1);
                    float nz = z / (float)(heightmapRes - 1);
                    heights[z, x] =
                        Mathf.PerlinNoise(nx * 3f, nz * 3f) * 0.4f
                        + Mathf.PerlinNoise(nx * 6f + 10f, nz * 6f + 10f) * 0.15f;
                }
            }
            terrainData.SetHeights(0, 0, heights);

            terrainGO.transform.position = new Vector3(-25f, 0f, -25f);

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.95f, 0.85f);
            light.intensity = 1.2f;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            Camera cam = CreateCamera(new Vector3(0f, 25f, -20f), Vector3.zero);
            cam.transform.LookAt(Vector3.zero);
        }

        /// <summary>
        /// Scene 09: Canvas with Button, Text, Image. Screen Space Overlay.
        /// Tests thumbnail rendering with UI elements.
        /// </summary>
        private static void CreateUIScene()
        {
            // EventSystem
            GameObject eventSystemGO = new GameObject("EventSystem");
            eventSystemGO.AddComponent<EventSystem>();
            eventSystemGO.AddComponent<StandaloneInputModule>();

            // Canvas
            GameObject canvasGO = new GameObject("Canvas");
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            // Button
            Button btn = CreateUIElement<Button>(
                canvasGO.transform,
                "TestButton",
                new Vector2(200f, 50f),
                new Vector2(0f, 50f)
            );
            GameObject buttonGO = btn.gameObject;
            Image buttonImage = buttonGO.GetComponent<Image>();
            if (buttonImage != null)
            {
                buttonImage.color = new Color(0.2f, 0.6f, 1f, 1f);
            }
            Button button = buttonGO.GetComponent<Button>();
            if (button != null)
            {
                GameObject childText = CreateUIText(
                    buttonGO.transform,
                    "ButtonLabel",
                    "Click Me",
                    18
                );
                RectTransform textRT = childText.GetComponent<RectTransform>();
                if (textRT != null)
                {
                    textRT.anchorMin = Vector2.zero;
                    textRT.anchorMax = Vector2.one;
                    textRT.offsetMin = Vector2.zero;
                    textRT.offsetMax = Vector2.zero;
                }
            }

            // Text
            CreateUIText(canvasGO.transform, "TitleText", "UI Scene", 24);

            // Image
            Image imageElement = CreateUIElement<Image>(
                canvasGO.transform,
                "TestImage",
                new Vector2(120f, 120f),
                new Vector2(0f, -100f)
            );
            GameObject imageGO = imageElement.gameObject;
            Image img = imageGO.GetComponent<Image>();
            if (img != null)
            {
                Texture2D tex = new Texture2D(4, 4);
                Color[] pixels = new Color[16];
                for (int i = 0; i < 16; i++)
                {
                    pixels[i] = Color.HSVToRGB((i % 4) / 4f, 0.8f, 0.9f);
                }
                tex.SetPixels(pixels);
                tex.Apply();
                img.sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
                img.type = Image.Type.Simple;
                img.preserveAspect = true;
            }
        }

        /// <summary>
        /// Scene 10: Particle system with colorful particles.
        /// Tests thumbnail rendering with dynamic visual effects.
        /// </summary>
        private static void CreateParticleScene()
        {
            GameObject particleGO = new GameObject("ColorfulParticles");
            particleGO.transform.position = new Vector3(0f, 1f, 0f);

            ParticleSystem ps = particleGO.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.startLifetime = 3f;
            main.startSpeed = 2f;
            main.startSize = 0.3f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                Color.HSVToRGB(0f, 0.8f, 1f),
                Color.HSVToRGB(0.8f, 0.8f, 1f)
            );
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.loop = true;
            main.playOnAwake = true;
            main.maxParticles = 200;

            var emission = ps.emission;
            emission.rateOverTime = 40f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 25f;
            shape.radius = 0.1f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[]
                {
                    new GradientColorKey(Color.red, 0f),
                    new GradientColorKey(Color.yellow, 0.33f),
                    new GradientColorKey(Color.green, 0.66f),
                    new GradientColorKey(Color.blue, 1f),
                },
                new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.8f),
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

            // Ensure default particle material is assigned
            Renderer renderer = particleGO.GetComponent<ParticleSystemRenderer>();
            if (renderer != null && renderer.sharedMaterial == null)
            {
                Shader particleShader = Shader.Find("Particles/Universal Render Pipeline/Unlit");
                if (particleShader == null)
                {
                    particleShader = Shader.Find("Particles/Standard Unlit");
                }
                if (particleShader != null)
                {
                    Material particleMat = new Material(particleShader);
                    particleMat.SetColor("_BaseColor", Color.white);
                    renderer.material = particleMat;
                }
            }

            GameObject lightGO = new GameObject("Directional Light");
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 0.8f;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            CreateCamera(new Vector3(0f, 2f, -5f), Vector3.zero);
        }

        #endregion

        #region Private Methods - Helpers

        private static void EnsureScenesFolder()
        {
            if (!AssetDatabase.IsValidFolder(k_ScenesFolder))
            {
                string parent = Path.GetDirectoryName(k_ScenesFolder);
                string folderName = Path.GetFileName(k_ScenesFolder);
                AssetDatabase.CreateFolder(parent, folderName);
            }
        }

        private static string GetSceneName(int index)
        {
            string[] names = new string[]
            {
                "BasicLitScene",
                "MultiLightScene",
                "TopDownScene",
                "CloseUpScene",
                "DarkScene",
                "ColorfulScene",
                "ReflectionScene",
                "TerrainScene",
                "UIScene",
                "ParticleScene",
            };
            return names[index];
        }

        private static Camera CreateCamera(Vector3 position, Vector3 lookAt)
        {
            GameObject camGO = new GameObject("Main Camera");
            camGO.tag = "MainCamera";
            Camera cam = camGO.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.1f, 0.1f, 0.15f, 1f);
            camGO.transform.position = position;
            if (lookAt != position)
            {
                camGO.transform.LookAt(lookAt);
            }
            return cam;
        }

        private static void CreateSphereAt(float x, float y, float z, Color color, string name)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.position = new Vector3(x, y, z);
            sphere.name = name;
            SetMaterialColor(sphere, color);
        }

        private static void CreatePointLight(Vector3 position, Color color, string name)
        {
            GameObject lightGO = new GameObject(name);
            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = 2.0f;
            light.range = 10f;
            lightGO.transform.position = position;
        }

        private static Shader GetLitShader()
        {
            Shader urpShader = Shader.Find("Universal Render Pipeline/Lit");
            if (urpShader != null)
            {
                return urpShader;
            }
            Debug.LogWarning(
                "[UniThumb] URP Lit shader not found. Falling back to Standard shader."
            );
            return Shader.Find("Standard");
        }

        private static void SetMaterialColor(GameObject go, Color color)
        {
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }
            Material mat = new Material(GetLitShader());
            mat.SetColor("_BaseColor", color);
            renderer.material = mat;
        }

        private static T CreateUIElement<T>(
            Transform parent,
            string name,
            Vector2 sizeDelta,
            Vector2 anchoredPosition
        )
            where T : Component
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = anchoredPosition;
            go.AddComponent<T>();
            return go.GetComponent<T>();
        }

        private static GameObject CreateUIText(
            Transform parent,
            string name,
            string text,
            int fontSize
        )
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(300f, 40f);
            rt.anchoredPosition = new Vector2(0f, 150f);
            Text uiText = go.AddComponent<Text>();
            uiText.text = text;
            uiText.fontSize = fontSize;
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.color = Color.white;
            Font font = Font.CreateDynamicFontFromOSFont("Arial", fontSize);
            if (font != null)
            {
                uiText.font = font;
            }
            return go;
        }

        private static void RestoreOriginalScene(string originalPath)
        {
            try
            {
                if (string.IsNullOrEmpty(originalPath))
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.OpenScene(originalPath, OpenSceneMode.Single);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[UniThumb] Could not restore previous scene: " + exception.Message
                );
            }
        }

        #endregion
    }
}
