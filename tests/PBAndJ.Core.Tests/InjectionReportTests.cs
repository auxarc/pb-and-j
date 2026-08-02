using System;
using PBAndJ.Core;
using Xunit;

namespace PBAndJ.Core.Tests
{
    public class InjectionReportTests
    {
        [Fact]
        public void Compose_ValidInjection_ReportsSuccessWithDetails()
        {
            var result = InjectionReport.Compose("unit_a", true, 321, 5f, 3.5f);
            Assert.Equal("[pb-and-j] injected move for unit_a: action #321 @5.00s +3.50s | valid=True", result);
        }

        [Fact]
        public void Compose_InvalidInjection_ReportsFailure()
        {
            var result = InjectionReport.Compose("unit_a", false, -1, 0f, 0f);
            Assert.Equal("[pb-and-j] injection REJECTED for unit_a (valid=False) — action was not accepted by the game", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Compose_MissingUnitName_Throws(string? unit)
        {
            var ex = Assert.Throws<ArgumentException>(() => InjectionReport.Compose(unit!, true, 1, 0f, 1f));
            Assert.Equal("unitName", ex.ParamName);
        }

        [Fact]
        public void Compose_UsesInvariantCulture()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Contains("@1.50s +2.25s", InjectionReport.Compose("u", true, 5, 1.5f, 2.25f));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }
    }
}
