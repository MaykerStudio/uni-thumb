using System;
using System.Collections.Generic;
using UnityEngine;

namespace MaykerStudio.UniThumb
{
    /// <summary>
    /// Lightweight scene fingerprint for detecting meaningful scene changes before
    /// re-capturing thumbnails. Captures five metrics (object count, light count,
    /// material count, total vertex count, bounds hash) into a 20-byte struct.
    /// Large scenes (>10k objects) skip fingerprinting to avoid editor stalls.
    /// </summary>
    public static class UniThumbFingerprint
    {
        #region Constants

        private const string k_LogPrefix = "[UniThumb] ";

        /// <summary>
        /// Scenes with more GameObjects than this threshold skip fingerprint
        /// computation entirely to avoid editor stalls on heavy scenes.
        /// </summary>
        private const int k_MaxFingerprintObjects = 10000;

        #endregion

        #region Public Methods

        /// <summary>
        /// Computes a scene fingerprint from the current scene's active objects.
        /// Returns an all-zeros fingerprint when the scene exceeds the object
        /// count threshold or contains no relevant components.
        /// </summary>
        public static SceneFingerprint Compute()
        {
#if UNITY_6000_0_OR_NEWER
            GameObject[] objects = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
#else
            GameObject[] objects = UnityEngine.Object.FindObjectsOfType<GameObject>();
#endif
            if (objects.Length > k_MaxFingerprintObjects)
            {
                Debug.LogWarning(
                    k_LogPrefix
                        + "Scene too large for fingerprint computation ("
                        + objects.Length
                        + " objects). Skipping."
                );
                return default;
            }

            int objectCount = objects.Length;

#if UNITY_6000_0_OR_NEWER
            Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
            MeshFilter[] meshFilters = UnityEngine.Object.FindObjectsByType<MeshFilter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None
            );
#else
            Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
            MeshFilter[] meshFilters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();
#endif

            int lightCount = lights.Length;

            int totalVertexCount = 0;
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter mf = meshFilters[i];
                if (mf == null)
                {
                    continue;
                }
                Mesh mesh = mf.sharedMesh;
                if (mesh != null)
                {
                    totalVertexCount += mesh.vertexCount;
                }
            }

            int boundsHash = 0;
            var uniqueMaterials = new HashSet<Material>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                {
                    continue;
                }

                Bounds b = r.bounds;
                Vector3Int bMin = Vector3Int.FloorToInt(b.min);
                Vector3Int bMax = Vector3Int.CeilToInt(b.max);
                boundsHash ^= bMin.GetHashCode();
                boundsHash ^= bMax.GetHashCode();

                Material[] mats = r.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] != null)
                    {
                        uniqueMaterials.Add(mats[m]);
                    }
                }
            }

            return new SceneFingerprint(
                objectCount,
                lightCount,
                uniqueMaterials.Count,
                totalVertexCount,
                boundsHash
            );
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Immutable snapshot of key scene metrics. Five ints = 20 bytes.
        /// Two fingerprints are equal when all five fields match.
        /// </summary>
        public readonly struct SceneFingerprint : IEquatable<SceneFingerprint>
        {
            public readonly int ObjectCount;
            public readonly int LightCount;
            public readonly int MaterialCount;
            public readonly int TotalVertexCount;
            public readonly int BoundsHash;

            public SceneFingerprint(
                int objectCount,
                int lightCount,
                int materialCount,
                int totalVertexCount,
                int boundsHash
            )
            {
                ObjectCount = objectCount;
                LightCount = lightCount;
                MaterialCount = materialCount;
                TotalVertexCount = totalVertexCount;
                BoundsHash = boundsHash;
            }

            public bool Equals(SceneFingerprint other)
            {
                return ObjectCount == other.ObjectCount
                    && LightCount == other.LightCount
                    && MaterialCount == other.MaterialCount
                    && TotalVertexCount == other.TotalVertexCount
                    && BoundsHash == other.BoundsHash;
            }

            public override bool Equals(object obj)
            {
                return obj is SceneFingerprint other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + ObjectCount;
                    hash = hash * 31 + LightCount;
                    hash = hash * 31 + MaterialCount;
                    hash = hash * 31 + TotalVertexCount;
                    hash = hash * 31 + BoundsHash;
                    return hash;
                }
            }

            public static bool operator ==(SceneFingerprint left, SceneFingerprint right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(SceneFingerprint left, SceneFingerprint right)
            {
                return !left.Equals(right);
            }

            /// <summary>
            /// Serializes the fingerprint to a 20-byte array (5 ints, little-endian).
            /// </summary>
            public byte[] ToBytes()
            {
                byte[] data = new byte[20];
                Buffer.BlockCopy(BitConverter.GetBytes(ObjectCount), 0, data, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(LightCount), 0, data, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(MaterialCount), 0, data, 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(TotalVertexCount), 0, data, 12, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(BoundsHash), 0, data, 16, 4);
                return data;
            }

            /// <summary>
            /// Deserializes a 20-byte array into a SceneFingerprint.
            /// Returns default (all-zeros) when the data is null or too short.
            /// </summary>
            public static SceneFingerprint FromBytes(byte[] data)
            {
                if (data == null || data.Length < 20)
                {
                    return default;
                }
                return new SceneFingerprint(
                    BitConverter.ToInt32(data, 0),
                    BitConverter.ToInt32(data, 4),
                    BitConverter.ToInt32(data, 8),
                    BitConverter.ToInt32(data, 12),
                    BitConverter.ToInt32(data, 16)
                );
            }
        }

        #endregion
    }
}
