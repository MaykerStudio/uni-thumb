using System;
using System.Reflection;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Narrow dispatch from core to the constraint-gated URP shim
    /// (UniThumb.UrpShim.Editor, defineConstraints HAS_URP). Core keeps zero
    /// Universal references: every call below resolves the bridge type by
    /// name and invokes one named static method, fail-open on any miss.
    /// Under #if HAS_URP the bridge is expected present; #else returns the
    /// absent-package default so HDRP-only and Built-in callers skip
    /// without a forced URP install. All reflection is cached (bridge type
    /// only) and exception-contained; never throws.
    /// </summary>
    internal static class UniThumbUrp
    {
        #region Fields

        private const string k_BridgeTypeName =
            "MaykerStudio.UniThumb.UrpShim.UrpBridge, UniThumb.UrpShim.Editor";
        private static Type s_bridgeType;
        private static bool s_bridgeProbed;
#if HAS_URP
        private static bool s_unavailableLogged;
#endif

        #endregion

        #region Properties

        internal static bool IsAvailable
        {
            get { return BridgeType != null; }
        }

        internal static Type Light2DType
        {
            get { return InvokeType("Light2DType"); }
        }

        internal static Type Light2DLightType
        {
            get { return InvokeType("Light2DLightType"); }
        }

        internal static string Light2DFullName
        {
            get
            {
                object result = Invoke("Light2DFullName", null);
                return result as string;
            }
        }

        private static Type BridgeType
        {
            get
            {
                if (s_bridgeProbed)
                {
                    return s_bridgeType;
                }
                s_bridgeProbed = true;
                try
                {
                    s_bridgeType = Type.GetType(k_BridgeTypeName, false);
                }
                catch (Exception)
                {
                    s_bridgeType = null;
                }
                return s_bridgeType;
            }
        }

        #endregion

        #region Public Methods

        internal static void ResetForTest()
        {
            ResetRendererSwitchLogForTest();
            s_bridgeType = null;
            s_bridgeProbed = false;
#if HAS_URP
            s_unavailableLogged = false;
#endif
        }

        internal static void ResetRendererSwitchLogForTest()
        {
#if HAS_URP
            Invoke("ResetRendererSwitchLogForTest", null);
#endif
        }

        internal static bool EnsureCameraData(Camera cam, bool enablePostProcessing)
        {
#if HAS_URP
            return InvokeBool("EnsureCameraData", new object[] { cam, enablePostProcessing });
#else
            return false;
#endif
        }

        internal static bool EnsureCameraDataFromSource(Camera cam, Camera sourceCam)
        {
#if HAS_URP
            return InvokeBool("EnsureCameraDataFromSource", new object[] { cam, sourceCam });
#else
            return false;
#endif
        }

        internal static bool TrySwitchTo3DRenderer(Camera cam)
        {
#if HAS_URP
            return InvokeBool("TrySwitchTo3DRenderer", new object[] { cam });
#else
            return false;
#endif
        }

        internal static bool PipelineHasOnly2DRenderers()
        {
#if HAS_URP
            return InvokeBool("PipelineHasOnly2DRenderers", null);
#else
            return false;
#endif
        }

        internal static int GetRendererCount()
        {
#if HAS_URP
            object result = Invoke("GetRendererCount", null);
            return result is int count ? count : 0;
#else
            return 0;
#endif
        }

        internal static bool TrySetRendererByIndex(Camera cam, int index)
        {
#if HAS_URP
            if (cam == null)
            {
                return false;
            }
            return InvokeBool("TrySetRendererByIndex", new object[] { cam, index });
#else
            return false;
#endif
        }

        internal static int DetectRendererDimension()
        {
#if HAS_URP
            object result = Invoke("DetectRendererDimension", null);
            return result is int dimension ? dimension : -1;
#else
            return -1;
#endif
        }

        internal static string ActivePipelineName()
        {
#if HAS_URP
            object result = Invoke("ActivePipelineName", null);
            return result as string ?? "none";
#else
            return "none";
#endif
        }

        internal static string ActiveRendererName()
        {
#if HAS_URP
            object result = Invoke("ActiveRendererName", null);
            return result as string ?? "none";
#else
            return "none";
#endif
        }

        internal static bool IsLight2DType(Type type)
        {
#if HAS_URP
            if (type == null)
            {
                return false;
            }
            return type == Light2DType;
#else
            return false;
#endif
        }

        internal static bool IsLight2D(Component component)
        {
#if HAS_URP
            if (component == null)
            {
                return false;
            }
            return InvokeBool("IsLight2D", new object[] { component });
#else
            return false;
#endif
        }

        internal static int GetLightTypeValue(Component component)
        {
#if HAS_URP
            if (component == null)
            {
                return -1;
            }
            object result = Invoke("GetLightTypeValue", new object[] { component });
            return result is int value ? value : -1;
#else
            return -1;
#endif
        }

        internal static bool IsGlobalLight2D(Component component, int globalValue)
        {
#if HAS_URP
            if (component == null)
            {
                return false;
            }
            return InvokeBool("IsGlobalLight2D", new object[] { component, globalValue });
#else
            return false;
#endif
        }

        internal static bool SetupExampleLight2D(
            GameObject go,
            Color color,
            float intensity,
            float outerRadius
        )
        {
#if HAS_URP
            if (go == null)
            {
                return false;
            }
            return InvokeBool(
                "SetupExampleLight2D",
                new object[] { go, color, intensity, outerRadius }
            );
#else
            return false;
#endif
        }

        internal static GameObject CreateTempGlobalLight(
            float intensity,
            int[] sortingLayerIds,
            int globalValue
        )
        {
#if HAS_URP
            object result = Invoke(
                "CreateTempGlobalLight",
                new object[] { intensity, sortingLayerIds, globalValue }
            );
            return result as GameObject;
#else
            return null;
#endif
        }

        internal static Component[] GetLight2DComponents(GameObject root)
        {
#if HAS_URP
            if (root == null)
            {
                return null;
            }
            object result = Invoke("GetLight2DComponents", new object[] { root });
            return result as Component[];
#else
            return null;
#endif
        }

        #endregion

        #region Private Methods

        private static object Invoke(string methodName, object[] args)
        {
            try
            {
                Type bridge = BridgeType;
                if (bridge == null)
                {
                    LogUnavailableOnce();
                    return null;
                }
                MethodInfo method = bridge.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Static
                );
                if (method == null)
                {
                    LogUnavailableOnce();
                    return null;
                }
                return method.Invoke(null, args);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool InvokeBool(string methodName, object[] args)
        {
            object result = Invoke(methodName, args);
            return result is bool value && value;
        }

        private static Type InvokeType(string methodName)
        {
#if HAS_URP
            return Invoke(methodName, null) as Type;
#else
            return null;
#endif
        }

        private static void LogUnavailableOnce()
        {
#if HAS_URP
            if (s_unavailableLogged)
            {
                return;
            }
            s_unavailableLogged = true;
            Debug.LogWarning(
                "[UniThumb] URP shim bridge not found; continuing without URP post-processing and 2D lights."
            );
#endif
        }

        #endregion
    }
}
