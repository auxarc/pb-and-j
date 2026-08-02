using System;
using PBAndJ.Core;
using Xunit;

namespace PBAndJ.Core.Tests
{
    public class LoadBannerTests
    {
        // --- Compose: the line logged from ModLink.OnLoadStart ---

        [Fact]
        public void Compose_WithValidIdAndVersion_FormatsBanner()
        {
            var result = LoadBanner.Compose("pb-and-j", "0.1.0");
            Assert.Equal("[pb-and-j] v0.1.0 — core loaded", result);
        }

        [Fact]
        public void Compose_TrimsWhitespaceFromInputs()
        {
            var result = LoadBanner.Compose("  pb-and-j ", " 0.1.0  ");
            Assert.Equal("[pb-and-j] v0.1.0 — core loaded", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Compose_WithMissingModId_Throws(string? modId)
        {
            var ex = Assert.Throws<ArgumentException>(() => LoadBanner.Compose(modId!, "0.1.0"));
            Assert.Equal("modId", ex.ParamName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Compose_WithMissingVersion_Throws(string? version)
        {
            var ex = Assert.Throws<ArgumentException>(() => LoadBanner.Compose("pb-and-j", version!));
            Assert.Equal("version", ex.ParamName);
        }

        // --- PatchFired: the line logged from the Harmony postfix ---

        [Fact]
        public void PatchFired_WithValidTarget_FormatsMessage()
        {
            var result = LoadBanner.PatchFired("Heartbeat.Start");
            Assert.Equal("[pb-and-j] patch fired: Heartbeat.Start", result);
        }

        [Fact]
        public void PatchFired_TrimsWhitespaceFromTarget()
        {
            var result = LoadBanner.PatchFired("  Heartbeat.Start ");
            Assert.Equal("[pb-and-j] patch fired: Heartbeat.Start", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void PatchFired_WithMissingTarget_Throws(string? target)
        {
            var ex = Assert.Throws<ArgumentException>(() => LoadBanner.PatchFired(target!));
            Assert.Equal("target", ex.ParamName);
        }
    }
}
