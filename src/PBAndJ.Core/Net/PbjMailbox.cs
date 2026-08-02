using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Where background threads hand work to the main thread.
    /// </summary>
    public interface IPbjInbox
    {
        /// <summary>
        /// Queues an event. Returns false if the mailbox is full and the event
        /// was dropped. Safe to call from any thread.
        /// </summary>
        bool Post(PbjInboundEvent evt);
    }

    /// <summary>
    /// Bounded, thread-safe hand-off queue. Transport threads
    /// <see cref="Post"/>; the pump <see cref="DrainAll"/>s once per frame.
    /// </summary>
    /// <remarks>
    /// This is the entire concurrency surface of PBAndJ.Core. Everything else —
    /// framing, decoding, session state — runs single-threaded on the main
    /// thread behind it.
    /// <para>
    /// <see cref="DrainAll"/> swaps the buffer out under the lock rather than
    /// copying, so draining is atomic and costs one allocation per non-empty
    /// pump.
    /// </para>
    /// <para>
    /// Bounded because the receive thread is fed by a peer we do not trust: an
    /// unbounded queue lets a flooding peer grow the host's heap without limit.
    /// Overflow drops newest and counts it, which the pump surfaces.
    /// </para>
    /// <para>
    /// Plain <c>lock</c> is safe under the 100% branch gate: it lowers to
    /// <c>Monitor.Enter(o, ref lockTaken)</c> plus <c>if (lockTaken)</c> in the
    /// finally, but coverlet 6.0.2 accounts for that generated branch — measured
    /// on 2026-08-02, 4 branches in <see cref="Post"/>, 0 uncovered. No
    /// hand-rolled Monitor.Enter/try/finally needed.
    /// </para>
    /// </remarks>
    public sealed class PbjMailbox : IPbjInbox
    {
        private static readonly PbjInboundEvent[] NoEvents = new PbjInboundEvent[0];

        private readonly object gate = new object();
        private readonly int capacity;

        private List<PbjInboundEvent> buffer = new List<PbjInboundEvent>();
        private int dropped;

        public PbjMailbox(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            this.capacity = capacity;
        }

        /// <summary>Events waiting to be drained.</summary>
        public int Count
        {
            get
            {
                lock (gate)
                {
                    return buffer.Count;
                }
            }
        }

        /// <summary>Total events discarded because the mailbox was full.</summary>
        public int DroppedCount
        {
            get
            {
                lock (gate)
                {
                    return dropped;
                }
            }
        }

        public bool Post(PbjInboundEvent evt)
        {
            if (evt == null)
            {
                throw new ArgumentNullException(nameof(evt));
            }

            lock (gate)
            {
                if (buffer.Count >= capacity)
                {
                    dropped++;
                    return false;
                }
                buffer.Add(evt);
                return true;
            }
        }

        /// <summary>
        /// Takes every pending event, leaving the mailbox empty. Call from the
        /// main thread only.
        /// </summary>
        public IReadOnlyList<PbjInboundEvent> DrainAll()
        {
            lock (gate)
            {
                if (buffer.Count == 0)
                {
                    return NoEvents;
                }
                var drained = buffer;
                buffer = new List<PbjInboundEvent>();
                return drained;
            }
        }
    }
}
