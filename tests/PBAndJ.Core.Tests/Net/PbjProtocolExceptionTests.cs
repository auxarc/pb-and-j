using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjProtocolExceptionTests
    {
        [Fact]
        public void Constructor_WithMessage_SetsMessage()
        {
            var ex = new PbjProtocolException("frame length 9999 exceeds maximum");
            Assert.Equal("frame length 9999 exceeds maximum", ex.Message);
        }
    }
}
