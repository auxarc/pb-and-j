using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class TrackThinningTests
    {
        private static int[] Run(int count, int cap)
        {
            var keys = new int[count];
            for (var i = 0; i < count; i++)
            {
                keys[i] = i;
            }

            var thinned = TrackThinning.Thin(keys, cap);
            var result = new int[thinned.Count];
            for (var i = 0; i < thinned.Count; i++)
            {
                result[i] = thinned[i];
            }
            return result;
        }

        [Fact]
        public void Thin_UnderTheCap_ReturnsTheSameList()
        {
            var keys = new[] { 1, 2, 3 };

            Assert.Same(keys, TrackThinning.Thin(keys, 8));
        }

        [Fact]
        public void Thin_AtTheCap_ReturnsTheSameList()
        {
            var keys = new[] { 1, 2, 3 };

            Assert.Same(keys, TrackThinning.Thin(keys, 3));
        }

        // The tail is the half that must survive: the last key is where the
        // snapshot has already corrected everyone to, so a track truncated at
        // the end finishes somewhere the unit no longer is.
        [Fact]
        public void Thin_OverTheCap_KeepsBothEndpoints()
        {
            var thinned = Run(100, 5);

            Assert.Equal(5, thinned.Length);
            Assert.Equal(0, thinned[0]);
            Assert.Equal(99, thinned[4]);
        }

        [Fact]
        public void Thin_OverTheCap_SpreadsTheInteriorAndKeepsItAscending()
        {
            var thinned = Run(100, 6);

            Assert.Equal(new[] { 0, 1, 25, 50, 74, 99 }, thinned);
        }

        // cap 2 leaves no interior at all, which divides by zero if the step is
        // computed before the loop is known to be empty. It is the smallest cap
        // any caller could pass and still satisfy the two-key rule projectile
        // and beam tracks are subject to, so it is not a theoretical input.
        [Fact]
        public void Thin_ToTwo_KeepsOnlyTheEndpoints()
        {
            Assert.Equal(new[] { 0, 49 }, Run(50, 2));
        }
    }
}
