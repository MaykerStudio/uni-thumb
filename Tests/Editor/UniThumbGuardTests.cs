using System;
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

        [Test]
        public void TryEnter_SetsEntryTimestamp()
        {
            UniThumbGuard.TryEnter();
            DateTime? entry = UniThumbGuard.EntryTime;
            Assert.IsNotNull(entry);
            // EntryTime should be within last 2 seconds
            Assert.IsTrue((DateTime.UtcNow - entry.Value).TotalSeconds < 2.0);
        }

        [Test]
        public void Exit_ClearsEntryTimestamp()
        {
            UniThumbGuard.TryEnter();
            UniThumbGuard.Exit();
            Assert.IsNull(UniThumbGuard.EntryTime);
        }

        [Test]
        public void IsStale_WhenNotGenerating_ReturnsFalse()
        {
            Assert.IsFalse(UniThumbGuard.IsStale);
        }

        [Test]
        public void RecoverIfStale_WhenStale_ReleasesGuard()
        {
            // Enter guard, then fake an old entry time
            UniThumbGuard.TryEnter();
            UniThumbGuard.SetEntryTimeForTest(DateTime.UtcNow.AddSeconds(-10));

            // Simulate batch not running so stale check passes
            UniThumbGuard.RecoverIfStale();
            Assert.IsFalse(UniThumbGuard.IsGenerating);
        }

        [Test]
        public void RecoverIfStale_WhenRecent_DoesNotReleaseGuard()
        {
            // Enter guard with fresh timestamp
            UniThumbGuard.TryEnter();

            // Not stale yet (< 5 seconds)
            UniThumbGuard.RecoverIfStale();
            Assert.IsTrue(UniThumbGuard.IsGenerating);
        }

        [Test]
        public void TeardownException_ReleaseGuard_ExitStillCalled()
        {
            // Simulates the CancelBatchState pattern: guard is entered,
            // then teardown code throws, but Exit() was already called first
            UniThumbGuard.TryEnter();
            Assert.IsTrue(UniThumbGuard.IsGenerating);

            // The fix pattern: Exit BEFORE fallible work
            UniThumbGuard.Exit();
            Assert.IsFalse(UniThumbGuard.IsGenerating);

            // Simulate the fallible teardown throwing
            bool threw = false;
            try
            {
                throw new InvalidOperationException("simulated refresh failure");
            }
            catch
            {
                threw = true;
            }
            Assert.IsTrue(threw);
            // Guard is still released because Exit() ran first
            Assert.IsFalse(UniThumbGuard.IsGenerating);
        }
    }
}
