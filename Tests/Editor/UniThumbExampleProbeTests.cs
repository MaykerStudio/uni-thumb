using NUnit.Framework;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbExampleProbeTests
    {
        [Test]
        public void DetectRendererDimension_ReturnsDefinedValue()
        {
            ExampleSceneGenerator.RendererDimension dimension =
                ExampleSceneGenerator.DetectRendererDimension();
            Assert.IsTrue(
                dimension == ExampleSceneGenerator.RendererDimension.D2D
                    || dimension == ExampleSceneGenerator.RendererDimension.D3D
            );
        }

        [Test]
        public void DetectRendererDimension_IsStableWithinSession()
        {
            ExampleSceneGenerator.RendererDimension first =
                ExampleSceneGenerator.DetectRendererDimension();
            ExampleSceneGenerator.RendererDimension second =
                ExampleSceneGenerator.DetectRendererDimension();
            Assert.AreEqual(first, second);
        }
    }
}
