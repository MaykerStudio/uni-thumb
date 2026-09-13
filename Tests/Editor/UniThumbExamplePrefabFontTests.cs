using NUnit.Framework;
using UnityEngine;

namespace MaykerStudio.UniThumb.Tests
{
    [TestFixture]
    public class UniThumbExamplePrefabFontTests
    {
        [Test]
        public void ResolveLegacyFont_ReturnsNonNullFont()
        {
            Font font = ExamplePrefabGenerator.ResolveLegacyFont(18);
            Assert.IsNotNull(font);
        }

        [Test]
        public void ResolveLegacyFont_PrefersBuiltinFont()
        {
            Font builtin = ResolveBuiltinFont("LegacyRuntime.ttf");
            if (builtin == null)
            {
                builtin = ResolveBuiltinFont("Arial.ttf");
            }

            if (builtin == null)
            {
                Assert.Ignore("No builtin legacy font available in this project.");
            }

            Font resolved = ExamplePrefabGenerator.ResolveLegacyFont(18);
            Assert.AreSame(builtin, resolved);
        }

        private static Font ResolveBuiltinFont(string resourceName)
        {
            try
            {
                return Resources.GetBuiltinResource<Font>(resourceName);
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
