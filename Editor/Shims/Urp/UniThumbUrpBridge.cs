using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MaykerStudio.UniThumb.UrpShim
{
    /// <summary>
    /// Constraint-gated URP access for UniThumb. This assembly compiles only
    /// when HAS_URP is defined (defineConstraints), so the Universal using
    /// above always resolves here. Core (UniThumb.Editor) keeps zero
    /// Universal references and reaches this bridge only through the
    /// UniThumbUrp narrow reflection dispatch under #if HAS_URP, with #else
    /// fail-open fallbacks. URP14 (2022.3) and URP17 (6000.x) share the
    /// warning-free renderer count below; no URP17-only API is used.
    /// Renderer selection uses SetRenderer (present since URP12); the
    /// rendererIndex property removed in URP17 is never referenced, and 2D
    /// types resolve via the 2D.Runtime assembly reference. GetRenderer is
    /// never probed out of range: URP14 logs a missing-renderer fallback
    /// warning per out-of-range call, so every sweep stays within
    /// GetRendererCount.
    /// </summary>
    public static class UrpBridge
    {
        #region Fields

        private static bool s_rendererSwitchLogged;

        private static FieldInfo s_rendererDataListField;

        private static bool s_rendererDataListProbed;

        #endregion

        #region Public Methods

        public static bool EnsureCameraData(Camera cam, bool enablePostProcessing)
        {
            try
            {
                if (cam == null)
                {
                    return false;
                }
                UniversalAdditionalCameraData uacd =
                    cam.GetComponent<UniversalAdditionalCameraData>();
                if (uacd == null)
                {
                    uacd = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                }
                if (uacd == null)
                {
                    return false;
                }
                if (enablePostProcessing)
                {
                    uacd.renderPostProcessing = true;
                    uacd.volumeLayerMask = new LayerMask { value = -1 };
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool EnsureCameraDataFromSource(Camera cam, Camera sourceCam)
        {
            try
            {
                if (cam == null || sourceCam == null)
                {
                    return false;
                }
                UniversalAdditionalCameraData component =
                    cam.GetComponent<UniversalAdditionalCameraData>();
                if (component == null)
                {
                    component = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
                }
                if (component == null)
                {
                    return false;
                }
                UniversalAdditionalCameraData sourceUacd =
                    sourceCam.GetComponent<UniversalAdditionalCameraData>();
                if (sourceUacd != null)
                {
                    // rendererIndex was removed in URP17: resolve the source
                    // renderer back to its pipeline index (SetRenderer exists
                    // since URP12, so this compiles on URP14 and URP17).
                    // Validated against the render-time renderer count: on
                    // 2D-only single-renderer pipelines a stale source index
                    // (e.g. 1) would otherwise arm a per-render URP fallback
                    // warning on the temp camera. Out-of-range indexes keep
                    // the default renderer silently.
                    int sourceIndex = FindRendererIndex(sourceUacd.scriptableRenderer);
                    if (sourceIndex >= 0)
                    {
                        TrySetRendererValidated(component, sourceIndex);
                    }
                    component.renderPostProcessing = sourceUacd.renderPostProcessing;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TrySwitchTo3DRenderer(Camera cam)
        {
            try
            {
                if (cam == null)
                {
                    return false;
                }
                UniversalAdditionalCameraData uacd =
                    cam.GetComponent<UniversalAdditionalCameraData>();
                if (uacd == null)
                {
                    return false;
                }
                UniversalRenderPipelineAsset pipelineAsset =
                    GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipelineAsset == null)
                {
                    return false;
                }
                int count = GetRendererCount();
                for (int i = 0; i < count; i++)
                {
                    ScriptableRenderer renderer = pipelineAsset.GetRenderer(i);
                    if (renderer == null)
                    {
                        break;
                    }
                    if (IsRenderer2D(renderer))
                    {
                        continue;
                    }
                    if (!TrySetRendererValidated(uacd, i))
                    {
                        continue;
                    }
                    LogRendererSwitchOnce(renderer.GetType().Name, i);
                    return true;
                }
                if (PipelineHasOnly2DRenderers())
                {
                    Debug.LogWarning(
                        "[UniThumb] Capture needs a 3D URP renderer (e.g. ForwardRenderer), "
                            + "but only 2D renderers were found in the pipeline. "
                            + "Standard Light components will have no effect."
                    );
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Render-time renderer count of the active URP pipeline asset.
        /// Warning-free: URP14 logs a missing-renderer fallback warning on
        /// every out-of-range GetRenderer call, so the count comes from the
        /// pipeline's renderer data list (reflection, SerializedObject
        /// fallback) and GetRenderer is only ever called within range.
        /// Returns 0 when no Universal pipeline asset is active.
        /// </summary>
        public static int GetRendererCount()
        {
            try
            {
                UniversalRenderPipelineAsset pipelineAsset =
                    GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipelineAsset == null)
                {
                    return 0;
                }
                int dataCount = GetRendererDataCount(pipelineAsset);
                if (dataCount >= 0)
                {
                    return dataCount;
                }
                return pipelineAsset.scriptableRenderer != null ? 1 : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Dispatch-friendly validated renderer switch (Camera + index, so
        /// core reaches it without Universal references). No-op returning
        /// false when the index is out of range; never emits the URP
        /// missing-renderer fallback warning.
        /// </summary>
        public static bool TrySetRendererByIndex(Camera cam, int index)
        {
            try
            {
                if (cam == null)
                {
                    return false;
                }
                UniversalAdditionalCameraData uacd =
                    cam.GetComponent<UniversalAdditionalCameraData>();
                if (uacd == null)
                {
                    return false;
                }
                return TrySetRendererValidated(uacd, index);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void ResetRendererSwitchLogForTest()
        {
            s_rendererSwitchLogged = false;
        }

        public static bool PipelineHasOnly2DRenderers()
        {
            try
            {
                UniversalRenderPipelineAsset pipelineAsset =
                    GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipelineAsset == null)
                {
                    return false;
                }
                ScriptableRenderer defaultRenderer = pipelineAsset.scriptableRenderer;
                if (defaultRenderer == null)
                {
                    return false;
                }
                bool any = false;
                int count = GetRendererCount();
                for (int i = 0; i < count; i++)
                {
                    ScriptableRenderer renderer = pipelineAsset.GetRenderer(i);
                    if (renderer == null)
                    {
                        break;
                    }
                    any = true;
                    if (!IsRenderer2D(renderer))
                    {
                        return false;
                    }
                }
                return any;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Renderer dimension of the active pipeline: 1 is 2D, 0 is 3D,
        /// -1 when no Universal pipeline asset is active (fail-open 3D).
        /// </summary>
        public static int DetectRendererDimension()
        {
            try
            {
                UniversalRenderPipelineAsset pipelineAsset =
                    GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipelineAsset == null)
                {
                    RenderPipelineAsset fallback = GraphicsSettings.defaultRenderPipeline;
                    pipelineAsset = fallback as UniversalRenderPipelineAsset;
                }
                if (pipelineAsset == null)
                {
                    return -1;
                }
                ScriptableRenderer activeRenderer = pipelineAsset.scriptableRenderer;
                return IsRenderer2D(activeRenderer) ? 1 : 0;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static string ActivePipelineName()
        {
            try
            {
                UniversalRenderPipelineAsset pipelineAsset =
                    GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipelineAsset == null)
                {
                    return "none";
                }
                return pipelineAsset.GetType().Name;
            }
            catch (Exception)
            {
                return "none";
            }
        }

        public static string ActiveRendererName()
        {
            try
            {
                UniversalRenderPipelineAsset pipelineAsset =
                    GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipelineAsset == null)
                {
                    return "none";
                }
                ScriptableRenderer activeRenderer = pipelineAsset.scriptableRenderer;
                if (activeRenderer == null)
                {
                    return "none";
                }
                return activeRenderer.GetType().Name;
            }
            catch (Exception)
            {
                return "none";
            }
        }

        public static Type Light2DType()
        {
            return typeof(Light2D);
        }

        public static Type Light2DLightType()
        {
            return typeof(Light2D.LightType);
        }

        public static string Light2DFullName()
        {
            return typeof(Light2D).FullName;
        }

        public static bool IsLight2DType(Type type)
        {
            return type == typeof(Light2D);
        }

        public static bool IsLight2D(Component component)
        {
            return component != null && component.GetType() == typeof(Light2D);
        }

        public static int GetLightTypeValue(Component component)
        {
            try
            {
                Light2D light2D = component as Light2D;
                if (light2D == null)
                {
                    return -1;
                }
                return (int)light2D.lightType;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static bool IsGlobalLight2D(Component component, int globalValue)
        {
            return IsLight2D(component) && GetLightTypeValue(component) == globalValue;
        }

        public static Component AddLight2D(GameObject go)
        {
            try
            {
                if (go == null)
                {
                    return null;
                }
                return go.AddComponent<Light2D>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool SetupExampleLight2D(
            GameObject go,
            Color color,
            float intensity,
            float outerRadius
        )
        {
            try
            {
                if (go == null)
                {
                    return false;
                }
                Light2D light2D = go.AddComponent<Light2D>();
                if (light2D == null)
                {
                    return false;
                }
                light2D.intensity = intensity;
                light2D.color = color;
                light2D.pointLightOuterRadius = outerRadius;
                light2D.pointLightInnerRadius = outerRadius * 0.5f;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static GameObject CreateTempGlobalLight(
            float intensity,
            int[] sortingLayerIds,
            int globalValue
        )
        {
            GameObject go = new GameObject("__UniThumbTempLight2D");
            try
            {
                go.hideFlags = HideFlags.HideInHierarchy;
                Light2D light2D = go.AddComponent<Light2D>();
                if (light2D == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return null;
                }
                light2D.intensity = intensity;
                light2D.lightType = (Light2D.LightType)globalValue;
                if (sortingLayerIds != null && sortingLayerIds.Length > 0)
                {
                    SerializedObject so = new SerializedObject(light2D);
                    SerializedProperty layersProp = so.FindProperty("m_ApplyToSortingLayers");
                    if (layersProp != null && layersProp.isArray)
                    {
                        layersProp.ClearArray();
                        for (int i = 0; i < sortingLayerIds.Length; i++)
                        {
                            layersProp.InsertArrayElementAtIndex(i);
                            layersProp.GetArrayElementAtIndex(i).intValue = sortingLayerIds[i];
                        }
                        so.ApplyModifiedProperties();
                    }
                }
                return go;
            }
            catch (Exception)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
                return null;
            }
        }

        public static Component[] GetLight2DComponents(GameObject root)
        {
            try
            {
                if (root == null)
                {
                    return null;
                }
                Light2D[] found = root.GetComponentsInChildren<Light2D>(true);
                Component[] result = new Component[found.Length];
                for (int i = 0; i < found.Length; i++)
                {
                    result[i] = found[i];
                }
                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Once-per-session info log for the 3D renderer switch. The switch
        /// itself runs on every capture/preview (SetRenderer above), but the
        /// log fires only once so prefab-mode previews do not spam the
        /// console. Genuine failures (Renderer2D-only pipeline) still warn
        /// per call above.
        /// </summary>
        private static void LogRendererSwitchOnce(string rendererName, int index)
        {
            if (s_rendererSwitchLogged)
            {
                return;
            }
            s_rendererSwitchLogged = true;
            Debug.Log(
                "[UniThumb] Light3D: switched camera renderer to '"
                    + rendererName
                    + "' (index "
                    + index
                    + ")."
            );
        }

        /// <summary>
        /// True when the renderer is a URP 2D renderer. Name-based: the
        /// Renderer2D class is public in URP14 but internal in URP17, so a
        /// direct "is" check cannot compile on both. The namespace guard
        /// keeps the match exact on either version.
        /// </summary>
        private static bool IsRenderer2D(ScriptableRenderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }
            System.Type type = renderer.GetType();
            return type.Name == "Renderer2D" && type.Namespace == "UnityEngine.Rendering.Universal";
        }

        /// <summary>
        /// Renderer data entry count behind the active pipeline asset,
        /// read without calling GetRenderer (which warns per out-of-range
        /// call on URP14). Reflection first (allocation-free array length),
        /// SerializedObject fallback, -1 when unreadable.
        /// </summary>
        private static int GetRendererDataCount(UniversalRenderPipelineAsset pipelineAsset)
        {
            try
            {
                if (pipelineAsset == null)
                {
                    return -1;
                }
                if (!s_rendererDataListProbed)
                {
                    s_rendererDataListProbed = true;
                    s_rendererDataListField = typeof(UniversalRenderPipelineAsset).GetField(
                        "m_RendererDataList",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );
                }
                if (s_rendererDataListField != null)
                {
                    Array list = s_rendererDataListField.GetValue(pipelineAsset) as Array;
                    if (list != null)
                    {
                        return list.Length;
                    }
                }
                SerializedObject so = new SerializedObject(pipelineAsset);
                SerializedProperty listProp = so.FindProperty("m_RendererDataList");
                if (listProp != null && listProp.isArray)
                {
                    return listProp.arraySize;
                }
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Validated SetRenderer: applies the index only when it addresses
        /// a real render-time renderer (0 &lt;= index &lt; GetRendererCount).
        /// Out-of-range indexes are skipped silently so the camera keeps
        /// the pipeline default (-1) instead of arming the URP
        /// missing-renderer fallback warning on every render.
        /// </summary>
        private static bool TrySetRendererValidated(UniversalAdditionalCameraData uacd, int index)
        {
            try
            {
                if (uacd == null)
                {
                    return false;
                }
                int count = GetRendererCount();
                if (index < 0 || index >= count)
                {
                    return false;
                }
                uacd.SetRenderer(index);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Index of the given renderer in the active URP pipeline asset, or
        /// -1 when unresolvable (fail-open: callers keep the default
        /// renderer). Reference compare avoids the removed rendererIndex
        /// property; GetRenderer/SetRenderer exist since URP12.
        /// </summary>
        private static int FindRendererIndex(ScriptableRenderer renderer)
        {
            try
            {
                if (renderer == null)
                {
                    return -1;
                }
                UniversalRenderPipelineAsset pipelineAsset =
                    GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (pipelineAsset == null)
                {
                    return -1;
                }
                int count = GetRendererCount();
                for (int i = 0; i < count; i++)
                {
                    ScriptableRenderer candidate = pipelineAsset.GetRenderer(i);
                    if (candidate == null)
                    {
                        break;
                    }
                    if (candidate == renderer)
                    {
                        return i;
                    }
                }
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        #endregion
    }
}
