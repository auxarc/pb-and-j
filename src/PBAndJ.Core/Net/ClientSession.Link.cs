using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The connection itself: opened, accepted, and given up on.
    //
    // `Start` sends the Hello, or the Rejoin when this session is a return.
    // `HandleWelcome` takes an acceptance; a refusal is answered inline in
    // ClientSession.Dispatch.cs. `HandleTick` is the keepalive, and the only thing
    // here that ends a session rather than beginning one.
    //
    // The clock fields it stamps stay in ClientSession.cs, because HandleMessage
    // touches them too -- any inbound traffic proves the host is alive. Which tick
    // that traffic is credited to is the whole subtlety here: see HandleTick.
    //
    // One part of ClientSession, a single class split across files. Class-level prose
    // lives ONLY in ClientSession.cs: this file uses // rather than /// so the
    // compiler cannot concatenate summaries from every part into one type entry in
    // PBAndJ.Core.xml.
    public sealed partial class ClientSession
    {
        /// <summary>Opens the handshake. Called once the transport connects.</summary>
        public IReadOnlyList<PbjEffect> Start()
        {
            if (resumeToken == null)
            {
                return new PbjEffect[]
                {
                    new SendEffect(HostConnectionId, new HelloMessage(
                        PbjProtocol.Magic, PbjProtocol.Version, modVersion, playerName,
                        GameBuild, Passphrase)),
                };
            }

            return new PbjEffect[]
            {
                new SendEffect(HostConnectionId, new RejoinMessage(
                    PbjProtocol.Magic, PbjProtocol.Version, modVersion, playerName,
                    resumeSessionId, resumePeerId, resumeToken, GameBuild, Passphrase)),
                new LogEffect(NetLog.Rejoining(resumeSessionId, resumePeerId)),
            };
        }

        private void HandleWelcome(WelcomeMessage welcome, List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Handshaking)
            {
                Fault(NetLog.TransportFailed("host sent a second Welcome"), effects);
                effects.Add(new DisconnectEffect(HostConnectionId, "protocol violation"));
                return;
            }

            PeerId = welcome.AssignedPeerId;
            SessionId = welcome.SessionId;
            HostName = welcome.HostName;
            Turn = welcome.CurrentTurn;
            ResumeToken = welcome.ResumeToken;
            State = bridge.InCombat ? ClientSessionState.Planning : ClientSessionState.Lobby;

            effects.Add(new LogEffect(NetLog.Welcomed(PeerId, SessionId, HostName, Turn)));
            var roster = new List<string>();
            for (var i = 0; i < welcome.Peers.Count; i++)
            {
                roster.Add("#" + welcome.Peers[i].PeerId + " '" + Describe(welcome.Peers[i].Name) + "'");
            }
            effects.Add(new LogEffect(NetLog.SessionSummary(roster)));
        }

        /// <summary>Gives up on a host that has gone quiet.</summary>
        private void HandleTick(TickEvent tick, List<PbjEffect> effects)
        {
            nowSeconds = tick.NowSeconds;
            ticked = true;

            // Consumed here and nowhere else. This is the first moment the
            // session clock reads a time at or after the drain that produced the
            // traffic, so it is the only honest moment to credit it.
            var spokeSincePreviousTick = inboundSinceTick;
            inboundSinceTick = false;

            if (!stamped)
            {
                // Seed rather than judge on the first tick — in-game the clock
                // starts at the process uptime, so an unstamped session would
                // otherwise look silent since time zero and fault immediately.
                lastInboundSeconds = nowSeconds;
                stamped = true;
                return;
            }

            if (spokeSincePreviousTick)
            {
                // The host spoke during the pump this tick closes, so re-stamp
                // at the clock that judges rather than leave HandleMessage's
                // stamp of the PREVIOUS tick's clock standing. Without this the
                // silence below is measured from before a stall that the proof
                // of life arrived during: one 30s frame gap — a long load, a
                // hitched frame — faulted a host that had just answered a ping.
                //
                // A restart, not a reprieve: a host that speaks once and then
                // dies is still faulted a full HostTimeoutSeconds after this.
                lastInboundSeconds = nowSeconds;
                return;
            }

            var silent = nowSeconds - lastInboundSeconds;
            if (silent >= PbjProtocol.HostTimeoutSeconds)
            {
                // Fault already unlocks execution, which is the invariant that
                // matters: a lost host must never leave the local execute button
                // disabled.
                Fault(NetLog.HostTimedOut(silent), effects);
            }
        }
    }
}
