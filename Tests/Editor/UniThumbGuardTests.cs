using NUnit.Framework;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbGuardTests
    {
        [SetUp]
        public void SetUp()
        {
            UniThumbGuard.Exit();
        }

        [Test]
        public void TryEnter_WhenFree_ReturnsTrueAndIsGenerating()
        {
            bool result = UniThumbGuard.TryEnter();
            Assert.IsTrue(result);
            Assert.IsTrue(UniThumbGuard.IsGenerating);
        }

        [Test]
        public void TryEnter_WhenBusy_ReturnsFalse()
        {
            UniThumbGuard.TryEnter();
            bool second = UniThumbGuard.TryEnter();
            Assert.IsFalse(second);
            Assert.IsTrue(UniThumbGuard.IsGenerating);
        }

        [Test]
        public void Exit_ResetsGuard()
        {
            UniThumbGuard.TryEnter();
            UniThumbGuard.Exit();
            Assert.IsFalse(UniThumbGuard.IsGenerating);
        }
    }
}
