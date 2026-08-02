using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjMailboxTests
    {
        private static PbjMailbox Box(int capacity = 16) => new PbjMailbox(capacity);

        private static PbjInboundEvent Evt(int peerId = 1) => new PeerConnectedEvent(peerId, "127.0.0.1:1");

        [Fact]
        public void DrainAll_WhenEmpty_ReturnsEmptyList()
        {
            Assert.Empty(Box().DrainAll());
        }

        [Fact]
        public void DrainAll_ReturnsEventsInPostOrder()
        {
            var box = Box();
            box.Post(Evt(1));
            box.Post(Evt(2));
            box.Post(Evt(3));

            var drained = box.DrainAll();
            Assert.Equal(3, drained.Count);
            Assert.Equal(1, ((PeerConnectedEvent)drained[0]).PeerId);
            Assert.Equal(2, ((PeerConnectedEvent)drained[1]).PeerId);
            Assert.Equal(3, ((PeerConnectedEvent)drained[2]).PeerId);
        }

        [Fact]
        public void DrainAll_LeavesMailboxEmpty()
        {
            var box = Box();
            box.Post(Evt());
            box.DrainAll();
            Assert.Empty(box.DrainAll());
            Assert.Equal(0, box.Count);
        }

        [Fact]
        public void Post_ReturnsTrueWhenAccepted()
        {
            Assert.True(Box().Post(Evt()));
        }

        [Fact]
        public void Count_ReflectsPendingEvents()
        {
            var box = Box();
            Assert.Equal(0, box.Count);
            box.Post(Evt());
            box.Post(Evt());
            Assert.Equal(2, box.Count);
        }

        [Fact]
        public void Post_WithNullEvent_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Box().Post(null!));
            Assert.Equal("evt", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNonPositiveCapacity_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PbjMailbox(0));
            Assert.Equal("capacity", ex.ParamName);
        }

        // --- backpressure: a hostile peer must not be able to grow the heap ---

        [Fact]
        public void Post_WhenAtCapacity_DropsAndReturnsFalse()
        {
            var box = Box(capacity: 2);
            Assert.True(box.Post(Evt(1)));
            Assert.True(box.Post(Evt(2)));
            Assert.False(box.Post(Evt(3)));
            Assert.Equal(2, box.Count);
        }

        [Fact]
        public void DroppedCount_TracksDiscardedEvents()
        {
            var box = Box(capacity: 1);
            box.Post(Evt());
            Assert.Equal(0, box.DroppedCount);
            box.Post(Evt());
            box.Post(Evt());
            Assert.Equal(2, box.DroppedCount);
        }

        [Fact]
        public void Post_AfterDraining_AcceptsAgain()
        {
            var box = Box(capacity: 1);
            box.Post(Evt(1));
            Assert.False(box.Post(Evt(2)));
            box.DrainAll();
            Assert.True(box.Post(Evt(3)));
        }

        // --- the only genuinely concurrent test in the suite ---

        [Fact]
        public void Post_FromManyThreads_LosesNothingWithinCapacity()
        {
            const int writers = 4;
            const int perWriter = 250;
            var box = Box(capacity: writers * perWriter);

            Parallel.For(0, writers, w =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    box.Post(new PeerConnectedEvent(w, null));
                }
            });

            Assert.Equal(writers * perWriter, box.DrainAll().Count);
        }

        [Fact]
        public async Task DrainAll_ConcurrentWithPost_NeverLosesOrDuplicates()
        {
            const int total = 2000;
            var box = Box(capacity: total);
            var seen = new List<PbjInboundEvent>();
            var producerDone = 0;

            var producer = Task.Run(() =>
            {
                for (var i = 0; i < total; i++)
                {
                    box.Post(new PeerConnectedEvent(i, null));
                }
                Interlocked.Exchange(ref producerDone, 1);
            });

            while (Volatile.Read(ref producerDone) == 0 || box.Count > 0)
            {
                seen.AddRange(box.DrainAll());
            }
            await producer;
            seen.AddRange(box.DrainAll());

            Assert.Equal(total, seen.Count);
            for (var i = 0; i < total; i++)
            {
                Assert.Equal(i, ((PeerConnectedEvent)seen[i]).PeerId);
            }
        }
    }
}
