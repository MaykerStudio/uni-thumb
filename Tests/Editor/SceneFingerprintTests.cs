using NUnit.Framework;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class SceneFingerprintTests
    {
        [Test]
        public void ToBytes_RoundTrip_PreservesAllFields()
        {
            var fp = new UniThumbFingerprint.SceneFingerprint(10, 3, 7, 5000, 42);
            byte[] bytes = fp.ToBytes();
            UniThumbFingerprint.SceneFingerprint roundTripped =
                UniThumbFingerprint.SceneFingerprint.FromBytes(bytes);

            Assert.AreEqual(fp.ObjectCount, roundTripped.ObjectCount);
            Assert.AreEqual(fp.LightCount, roundTripped.LightCount);
            Assert.AreEqual(fp.MaterialCount, roundTripped.MaterialCount);
            Assert.AreEqual(fp.TotalVertexCount, roundTripped.TotalVertexCount);
            Assert.AreEqual(fp.BoundsHash, roundTripped.BoundsHash);
        }

        [Test]
        public void FromBytes_Null_ReturnsDefault()
        {
            UniThumbFingerprint.SceneFingerprint result =
                UniThumbFingerprint.SceneFingerprint.FromBytes(null);
            Assert.AreEqual(default(UniThumbFingerprint.SceneFingerprint), result);
        }

        [Test]
        public void FromBytes_TooShort_ReturnsDefault()
        {
            byte[] tooShort = new byte[19];
            UniThumbFingerprint.SceneFingerprint result =
                UniThumbFingerprint.SceneFingerprint.FromBytes(tooShort);
            Assert.AreEqual(default(UniThumbFingerprint.SceneFingerprint), result);
        }

        [Test]
        public void Equals_SameValues_ReturnsTrue()
        {
            var a = new UniThumbFingerprint.SceneFingerprint(1, 2, 3, 4, 5);
            var b = new UniThumbFingerprint.SceneFingerprint(1, 2, 3, 4, 5);
            Assert.IsTrue(a.Equals(b));
        }

        [Test]
        public void GetHashCode_SameValues_SameHash()
        {
            var a = new UniThumbFingerprint.SceneFingerprint(10, 20, 30, 40, 50);
            var b = new UniThumbFingerprint.SceneFingerprint(10, 20, 30, 40, 50);
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }

        [Test]
        public void DefaultStruct_AllZeros()
        {
            var fp = default(UniThumbFingerprint.SceneFingerprint);
            Assert.AreEqual(0, fp.ObjectCount);
            Assert.AreEqual(0, fp.LightCount);
            Assert.AreEqual(0, fp.MaterialCount);
            Assert.AreEqual(0, fp.TotalVertexCount);
            Assert.AreEqual(0, fp.BoundsHash);
        }
    }
}
