using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Binary codec for every <see cref="PbjMessage"/>: a type byte followed by
    /// the body. Pairs with <see cref="FrameEncoder"/>/<see cref="FrameDecoder"/>,
    /// which delimit whole messages on the stream.
    /// </summary>
    public static class PbjMessageCodec
    {
        /// <summary>Cap on orders in one <see cref="ReadyMessage"/>.</summary>
        public const int MaxOrdersPerReady = 256;

        /// <summary>Cap on roster entries in one <see cref="WelcomeMessage"/>.</summary>
        public const int MaxPeersPerWelcome = 16;

        /// <summary>Cap on units named in one <see cref="AssignmentsMessage"/> entry.</summary>
        public const int MaxUnitsPerPeer = 64;

        /// <summary>
        /// Cap on units in one <see cref="SnapshotMessage"/>.
        /// </summary>
        /// <remarks>
        /// Not <see cref="MaxUnitsPerPeer"/>: that is a roster cap for one peer's
        /// share, and a snapshot covers every unit in the combat, hostile ones
        /// included. At ~90 bytes a unit this caps a snapshot near 12 KB, about
        /// 1% of <c>PbjRuntime.MaxFrameLength</c>.
        /// </remarks>
        public const int MaxUnitsPerSnapshot = 128;

        /// <summary>Cap on tracks in one <see cref="KeyframesMessage"/>.</summary>
        /// <remarks>
        /// Mirrors <see cref="MaxUnitsPerSnapshot"/> on purpose: keyframes cover
        /// the same unit set the snapshot does, so a combat that fits one fits
        /// the other.
        /// </remarks>
        public const int MaxTracksPerKeyframes = 128;

        /// <summary>
        /// Cap on transform keys in one <see cref="UnitTrack"/>.
        /// </summary>
        /// <remarks>
        /// The host samples every 0.1 s, so a 5 s turn records about 53 keys —
        /// this leaves room for a longer turn and the unsampled keys written at
        /// execution start and end. At 32 bytes a key the two caps together bound
        /// a message near 786 KB, under <see cref="PbjRuntime.MaxFrameLength"/>,
        /// which a test pins rather than trusting the arithmetic.
        /// </remarks>
        public const int MaxKeysPerTrack = 192;

        /// <summary>
        /// Cap on joints in one <see cref="PoseKey"/>.
        /// </summary>
        /// <remarks>
        /// Measured mech skeletons carry 26, identically across every loadout
        /// sampled; tanks carry 3. Sixty-four leaves room for a legged frame
        /// whose joint list grows with its leg count without leaving the bound
        /// below meaningless.
        /// </remarks>
        public const int MaxJointsPerPose = 64;

        /// <summary>Cap on pose keys in one <see cref="UnitPoseTrack"/>.</summary>
        /// <remarks>
        /// Matches <see cref="MaxKeysPerTrack"/>: poses and transforms come off
        /// the same sampler, so the same bound describes both. Note this is a
        /// real bound and not a comfortable one — the sampling interval is a
        /// player-facing setting with a 0.016 s floor, which puts a five-second
        /// turn past three hundred keys. <see cref="PoseTracks.Thin"/> is what
        /// keeps that a repair rather than a dropped peer.
        /// </remarks>
        public const int MaxPoseKeysPerTrack = 192;

        /// <summary>
        /// Cap on the parts one turn's poses may split into.
        /// </summary>
        /// <remarks>
        /// One part per unit, so this mirrors <see cref="MaxTracksPerKeyframes"/>
        /// for the same reason it mirrors <see cref="MaxUnitsPerSnapshot"/>.
        /// Enforced at decode because a client sizes its accumulator from it: an
        /// unvalidated count off the wire is an allocation a peer chooses for us.
        /// </remarks>
        public const int MaxPosePartsPerTurn = 128;

        /// <summary>
        /// Longest unit or joint name a pose track may carry, in characters.
        /// </summary>
        /// <remarks>
        /// Far below <see cref="PbjWriter.MaxStringLength"/>, and deliberately
        /// counted in characters so that even all-multi-byte text stays inside
        /// it — 128 characters cannot exceed 512 UTF-8 bytes. That margin is the
        /// point: <c>PbjWriter</c> <i>throws</i> above its own limit, and
        /// <c>PbjRuntime.SendTo</c> encodes outside its try block, so a name
        /// that oversteps would not fail the message, it would empty the effect
        /// pump behind it. Capping here means encode cannot be the place it is
        /// discovered.
        /// </remarks>
        public const int MaxPoseNameLength = 128;

        /// <summary>
        /// Cap on tracks of <i>each</i> kind in one
        /// <see cref="ReplayAssetsMessage"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="ReplayAssetParts.Split"/> never fills a part past this
        /// many tracks in <b>total</b> across the three kinds, so the encoder
        /// stays well inside it. The decoder bounds each of the three lists
        /// separately at this value, because a decoder may not assume the
        /// sender packed the way ours does — it can only bound what it is about
        /// to allocate. The frame bound is therefore proved against three full
        /// lists, which is three times what any part we send carries.
        /// </remarks>
        public const int MaxAssetsPerPart = 64;

        /// <summary>Cap on the parts one turn's assets may split into.</summary>
        /// <remarks>
        /// With <see cref="MaxAssetsPerPart"/> this bounds a turn at 4096
        /// tracks, against a measured worst case of 1091 — and that figure was
        /// the host's whole unpruned five-turn accumulation, not one window's
        /// slice. A backstop rather than a working limit; see
        /// <see cref="ReplayAssetParts.Split"/> for what happens past it.
        /// </remarks>
        public const int MaxAssetPartsPerTurn = 64;

        /// <summary>
        /// Cap on keys in one projectile or beam track.
        /// </summary>
        /// <remarks>
        /// Far above the measurement — 364 projectiles carried 938 keys between
        /// them, under three each — and deliberately so. These come off the same
        /// player-configurable sampler M8's poses do, so the cap is set by what
        /// the setting can produce rather than by what one machine happened to
        /// record. <see cref="TrackThinning.Thin"/> makes it a repair.
        /// </remarks>
        public const int MaxAssetKeysPerTrack = 64;

        /// <summary>
        /// Cap on trail points in one projectile track.
        /// </summary>
        /// <remarks>
        /// This is a <b>wire-safety bound, not a fidelity dial</b>, and it is
        /// sized from the frame arithmetic rather than from the measurement.
        /// A trail point is 92 bytes — about 2.9 <see cref="TransformKey"/>s —
        /// and at 64 the trail term alone is <c>64 × 64 × 92</c> = 368 KiB,
        /// <b>half of a fully capped part</b>. Measured rather than estimated:
        /// a part at every cap encodes to <b>712 KiB</b> against the 1 MiB
        /// <see cref="PbjRuntime.MaxFrameLength"/>, so headroom is ≈ 1.44× and
        /// <b>a cap past ~112 breaches the frame</b>. That margin is pinned by
        /// a test, not left to arithmetic in a comment.
        /// <para>
        /// ⚠️ <see cref="PbjWriter.MaxBytesLength"/> is <b>not</b> the binding
        /// limit here, despite being the smaller number. Asset parts are written
        /// field by field; nothing about them goes through
        /// <c>WriteBytes</c>, so its 512 KiB throw never engages.
        /// </para>
        /// <para>
        /// ⚠️ <b>This cap DOES fire in ordinary play — an earlier draft of this
        /// comment claimed it never would, and a playtest refuted that.</b> The
        /// ~32-points-per-trail figure it rested on came from one turn; a real
        /// missile measured <b>~68</b>, so a fully-thinned ribbon is the normal
        /// case and not the pathological one. It is still the right cap — the
        /// coarsening from 68 to 64 is invisible, and a trail was confirmed by
        /// eye on a client with it in force — but it means
        /// <see cref="NetLog.AssetTrailsSent"/> has to report when it bites,
        /// because a five-fold coarsening on some longer-lived projectile would
        /// otherwise look exactly like this one.
        /// <para>
        /// When it fires, <see cref="TrackThinning.Thin"/> coarsens the ribbon
        /// and keeps both of its ends — which is why no trail-specific thinning
        /// exists. Dropping the <i>oldest</i> points would look like the
        /// sympathetic choice and is the opposite one: the game culls by
        /// <c>timeEnd</c>, so the oldest points are exactly the ones still
        /// visible in the frames right after the muzzle.
        /// </para>
        /// </para>
        /// </remarks>
        public const int MaxTrailPointsPerTrack = 64;

        /// <summary>
        /// Cap on weapon-light flashes carried on one unit's pose track.
        /// </summary>
        /// <remarks>
        /// A busy measured turn put 109 flashes across every unit in the fight,
        /// so 64 for a single unit is generous. They are ~40 bytes plus a socket
        /// string and ride the one-track-per-message pose wire, so they compete
        /// with nothing.
        /// </remarks>
        public const int MaxLightKeysPerUnit = 64;

        /// <summary>
        /// Cap on reaction-glow pings carried on one unit's pose track.
        /// </summary>
        /// <remarks>
        /// One ping per action start that uses reaction lights, so a turn's
        /// worth for a single unit is a handful. Four bytes each and no string,
        /// making this the cheapest list on the wire.
        /// <para>
        /// ⚠️ The cap is only safe because capture slices to the turn window
        /// first. The recorder's own list accumulates for the whole fight
        /// (<c>units</c> is cleared only when <c>experimentalMode</c> is off,
        /// and it defaults on), so a whole-list send would climb past any cap
        /// mid-fight and take the turn's real pings down with it.
        /// </para>
        /// </remarks>
        public const int MaxReactionPingsPerUnit = 64;

        /// <summary>
        /// Cap on melee swings carried on one unit's pose track.
        /// </summary>
        /// <remarks>
        /// A unit gets one melee action per turn in practice; 8 leaves room for
        /// a straddling swing from the previous window plus anything a longer
        /// turn allows. Same window-slice precondition as
        /// <see cref="MaxReactionPingsPerUnit"/>, and here it bites soonest —
        /// the recorder appends a record per swing for the whole fight, so a
        /// melee-oriented mech would cross 8 cumulative records within a few
        /// turns and lose its whole track.
        /// </remarks>
        public const int MaxMeleesPerUnit = 8;

        /// <summary>
        /// Cap on wrecked parts carried on one unit's snapshot record. M15.
        /// </summary>
        /// <remarks>
        /// A unit's part count is fixed by its blueprint's socket table and runs
        /// to single figures, so 32 is roughly four times the largest real
        /// answer. Unlike the two lists above this needs no window-slice
        /// precondition — the set is the unit's live wrecked parts, which is
        /// bounded by how many parts it has rather than by how long the fight
        /// has run, and it therefore cannot climb.
        /// <para>
        /// ⚠️ <b>The worst case is bounded by
        /// <see cref="PbjRuntime.MaxFrameLength"/>, not by
        /// <c>PbjWriter.MaxBytesLength</c>, and this comment said otherwise until
        /// M16.</b> That 512 KiB limit guards <c>WriteBytes</c>; a snapshot is
        /// written field by field and never calls it, exactly as the asset-part
        /// note above already records for its own payload. So the real ceiling is
        /// the 1 MiB frame, and the arithmetic — 128 units × (32 wrecked + 32
        /// stated) parts × (a short socket string plus two floats) — lands around
        /// 230 KB, comfortably inside it. The mis-citation mattered because it
        /// pointed at a limit that <b>throws</b>, and a throw inside the effect
        /// pump takes every effect queued behind it (the failure M11e paid for).
        /// </para>
        /// </remarks>
        public const int MaxWreckedPartsPerUnit = 32;

        /// <summary>
        /// Cap on stated parts carried on one unit's snapshot record. M16.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="MaxWreckedPartsPerUnit"/> deliberately: it bounds
        /// the same set of parts asked a different question, so the two could
        /// only differ by accident. Truncation rather than a fault, for the
        /// reason that governs the whole record — a snapshot is a correction, and
        /// refusing one over a part list would cost that unit its position and
        /// visibility too.
        /// </remarks>
        public const int MaxPartsPerUnit = 32;

        /// <summary>Longest pool key an asset track may carry, in characters.</summary>
        /// <remarks>
        /// Counted in characters and set well below
        /// <see cref="PbjWriter.MaxStringLength"/>, exactly as
        /// <see cref="MaxPoseNameLength"/> is and for the same reason: 128
        /// characters cannot exceed 512 UTF-8 bytes, so encode can never be
        /// where an over-long key is discovered.
        /// <see cref="AssetTrackFault.KeyTooLong"/> is where it is discovered.
        /// </remarks>
        public const int MaxAssetKeyLength = 128;

        /// <summary>
        /// Longest pilot death cause a unit snapshot may carry, in characters.
        /// M17 stage 2.
        /// </summary>
        /// <remarks>
        /// Two orders of magnitude smaller than the other string caps here
        /// because the field is not free-form: it is a content key the game
        /// writes itself, and the longest one shipped is a short word.
        /// <para>
        /// 🔴 <b>Unlike <see cref="MaxAssetKeyLength"/>, this cap is enforced on
        /// BOTH sides and it refuses rather than truncating.</b> Truncating
        /// would put a cause on the wire the host never wrote, and neither peer
        /// could tell. The encode check catches this machine's own bug loudly;
        /// the decode check is the one that matters against a peer, which is not
        /// this process and can send anything.
        /// </para>
        /// </remarks>
        public const int MaxPilotDeathCauseLength = 32;

        public static byte[] Encode(PbjMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            var writer = new PbjWriter();
            writer.WriteByte((byte)message.Type);

            switch (message)
            {
                case HelloMessage hello:
                    writer.WriteInt32(hello.Magic);
                    writer.WriteInt32(hello.ProtocolVersion);
                    writer.WriteString(hello.ModVersion);
                    writer.WriteString(hello.PlayerName);
                    writer.WriteString(hello.GameBuild);
                    writer.WriteString(hello.Passphrase);
                    break;

                case WelcomeMessage welcome:
                    writer.WriteInt32(welcome.ProtocolVersion);
                    writer.WriteString(welcome.SessionId);
                    writer.WriteInt32(welcome.AssignedPeerId);
                    writer.WriteString(welcome.HostName);
                    writer.WriteInt32(welcome.Peers.Count);
                    for (var i = 0; i < welcome.Peers.Count; i++)
                    {
                        writer.WriteInt32(welcome.Peers[i].PeerId);
                        writer.WriteString(welcome.Peers[i].Name);
                    }
                    writer.WriteInt32(welcome.CurrentTurn);
                    writer.WriteString(welcome.ResumeToken);
                    break;

                case RejoinMessage rejoin:
                    writer.WriteInt32(rejoin.Magic);
                    writer.WriteInt32(rejoin.ProtocolVersion);
                    writer.WriteString(rejoin.ModVersion);
                    writer.WriteString(rejoin.PlayerName);
                    writer.WriteString(rejoin.SessionId);
                    writer.WriteInt32(rejoin.ClaimedPeerId);
                    writer.WriteString(rejoin.ResumeToken);
                    writer.WriteString(rejoin.GameBuild);
                    writer.WriteString(rejoin.Passphrase);
                    break;

                case RejectMessage reject:
                    writer.WriteInt32((int)reject.Reason);
                    writer.WriteString(reject.Detail);
                    break;

                case PeerJoinedMessage joined:
                    writer.WriteInt32(joined.PeerId);
                    writer.WriteString(joined.Name);
                    break;

                case PeerLeftMessage left:
                    writer.WriteInt32(left.PeerId);
                    writer.WriteString(left.Name);
                    break;

                case ReadyMessage ready:
                    writer.WriteInt32(ready.Turn);
                    writer.WriteInt32(ready.Orders.Count);
                    for (var i = 0; i < ready.Orders.Count; i++)
                    {
                        OrderPayloadCodec.Write(writer, ready.Orders[i]);
                    }
                    break;

                case TurnCommitMessage commit:
                    writer.WriteInt32(commit.Turn);
                    break;

                case TurnCompleteMessage complete:
                    writer.WriteInt32(complete.Turn);
                    writer.WriteString(complete.Digest);
                    break;

                case AssignmentsMessage assignments:
                    writer.WriteInt32(assignments.Assignments.Count);
                    for (var i = 0; i < assignments.Assignments.Count; i++)
                    {
                        var entry = assignments.Assignments[i];
                        writer.WriteInt32(entry.PeerId);
                        writer.WriteInt32(entry.UnitNames.Count);
                        for (var u = 0; u < entry.UnitNames.Count; u++)
                        {
                            writer.WriteString(entry.UnitNames[u]);
                        }
                    }
                    break;

                case UnreadyMessage unready:
                    writer.WriteInt32(unready.Turn);
                    break;

                case OrderResultMessage result:
                    writer.WriteInt32(result.Turn);
                    writer.WriteInt32(result.Accepted);
                    writer.WriteInt32(result.Rejected.Count);
                    for (var i = 0; i < result.Rejected.Count; i++)
                    {
                        writer.WriteInt32(result.Rejected[i].Index);
                        writer.WriteInt32((int)result.Rejected[i].Reason);
                    }
                    break;

                case CombatStartMessage combatStart:
                    writer.WriteInt32(combatStart.Turn);
                    break;

                case CombatEndMessage:
                    // No body — the type byte is the whole message.
                    break;

                case SnapshotMessage snapshot:
                    writer.WriteInt32(snapshot.Turn);
                    writer.WriteString(snapshot.Digest);
                    writer.WriteInt32(snapshot.Units.Count);
                    for (var i = 0; i < snapshot.Units.Count; i++)
                    {
                        WriteUnitSnapshot(writer, snapshot.Units[i]);
                    }
                    break;

                case KeyframesMessage keyframes:
                    writer.WriteInt32(keyframes.Turn);
                    writer.WriteSingle(keyframes.WindowStart);
                    writer.WriteSingle(keyframes.WindowEnd);
                    writer.WriteInt32(keyframes.Tracks.Count);
                    for (var i = 0; i < keyframes.Tracks.Count; i++)
                    {
                        var track = keyframes.Tracks[i];
                        writer.WriteString(track.Name);
                        // Raw bits, so the negative-infinity sentinel survives
                        // verbatim rather than becoming a magic finite number
                        // the far side has to recognise.
                        writer.WriteSingle(track.RevealTime);
                        writer.WriteSingle(track.HideTime);
                        writer.WriteInt32(track.Transforms.Count);
                        for (var k = 0; k < track.Transforms.Count; k++)
                        {
                            WriteTransformKey(writer, track.Transforms[k]);
                        }
                    }
                    break;

                case PosesMessage poses:
                    writer.WriteInt32(poses.Turn);
                    writer.WriteInt32(poses.PartIndex);
                    writer.WriteInt32(poses.PartCount);
                    WriteUnitPoseTrack(writer, poses.Track);
                    break;

                case ReplayAssetsMessage assets:
                    writer.WriteInt32(assets.Turn);
                    writer.WriteInt32(assets.PartIndex);
                    writer.WriteInt32(assets.PartCount);
                    WriteAssetCapture(writer, assets.Assets);
                    break;

                case ScenarioOfferMessage offer:
                    writer.WriteString(offer.SaveName);
                    writer.WriteInt32(offer.TotalBytes);
                    writer.WriteString(offer.Digest);
                    break;

                case ScenarioRequestMessage request:
                    writer.WriteString(request.Digest);
                    break;

                case ScenarioMessage scenario:
                    writer.WriteString(scenario.SaveName);
                    writer.WriteString(scenario.Digest);
                    writer.WriteInt32(scenario.Files.Count);
                    for (var i = 0; i < scenario.Files.Count; i++)
                    {
                        var file = scenario.Files[i];
                        writer.WriteString(file.Name);
                        writer.WriteBytes(file.Content);
                    }
                    break;

                case LobbyStateMessage lobby:
                    writer.WriteInt32(lobby.SelectionVersion);
                    writer.WriteString(lobby.SaveKey);
                    writer.WriteString(lobby.SaveDigest);
                    writer.WriteInt32(lobby.Peers.Count);
                    for (var i = 0; i < lobby.Peers.Count; i++)
                    {
                        writer.WriteInt32(lobby.Peers[i].PeerId);
                        writer.WriteString(lobby.Peers[i].Name);
                        writer.WriteBool(lobby.Peers[i].Ready);
                    }
                    break;

                case LobbyReadyMessage lobbyReady:
                    writer.WriteInt32(lobbyReady.SelectionVersion);
                    break;

                case LobbyUnreadyMessage lobbyUnready:
                    writer.WriteInt32(lobbyUnready.SelectionVersion);
                    break;

                case LobbyLoadMessage lobbyLoad:
                    writer.WriteInt32(lobbyLoad.SelectionVersion);
                    writer.WriteString(lobbyLoad.SaveKey);
                    writer.WriteString(lobbyLoad.SaveDigest);
                    break;

                case LobbyLoadedMessage lobbyLoaded:
                    writer.WriteInt32(lobbyLoaded.SelectionVersion);
                    writer.WriteInt32((int)lobbyLoaded.Outcome);
                    break;

                case BasePositionMessage basePosition:
                    writer.WriteSingle(basePosition.X);
                    writer.WriteSingle(basePosition.Z);
                    break;

                case CombatOfferMessage combatOffer:
                    writer.WriteString(combatOffer.SaveName);
                    writer.WriteString(combatOffer.Digest);
                    writer.WriteInt32(combatOffer.Turn);
                    break;

                case CombatEnteredMessage combatEntered:
                    writer.WriteInt32(combatEntered.Turn);
                    writer.WriteInt32((int)combatEntered.Outcome);
                    break;

                case PingMessage ping:
                    writer.WriteInt32(ping.Nonce);
                    break;

                case PongMessage pong:
                    writer.WriteInt32(pong.Nonce);
                    break;

                case ByeMessage bye:
                    writer.WriteString(bye.Reason);
                    break;

                default:
                    throw new PbjProtocolException(
                        "No encoder for message type " + message.Type + " (" + message.GetType().Name + ").");
            }

            return writer.ToArray();
        }

        public static PbjMessage Decode(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            var reader = new PbjReader(payload);
            var type = (PbjMessageType)reader.ReadByte();
            var message = DecodeBody(reader, type);
            reader.EnsureConsumed();
            return message;
        }

        private static PbjMessage DecodeBody(PbjReader reader, PbjMessageType type)
        {
            switch (type)
            {
                case PbjMessageType.Hello:
                    return new HelloMessage(
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadString(), reader.ReadString(),
                        reader.ReadString(), reader.ReadString());

                case PbjMessageType.Welcome:
                {
                    var protocolVersion = reader.ReadInt32();
                    var sessionId = reader.ReadString();
                    var assignedPeerId = reader.ReadInt32();
                    var hostName = reader.ReadString();
                    var count = ReadCount(reader, MaxPeersPerWelcome, "peer");
                    var peers = new PeerInfo[count];
                    for (var i = 0; i < count; i++)
                    {
                        peers[i] = new PeerInfo(reader.ReadInt32(), reader.ReadString());
                    }
                    return new WelcomeMessage(
                        protocolVersion, sessionId, assignedPeerId, hostName, peers,
                        reader.ReadInt32(), reader.ReadString());
                }

                case PbjMessageType.Rejoin:
                {
                    var magic = reader.ReadInt32();
                    var protocolVersion = reader.ReadInt32();
                    var modVersion = reader.ReadString();
                    var playerName = reader.ReadString();
                    var sessionId = reader.ReadString();
                    return new RejoinMessage(
                        magic, protocolVersion, modVersion, playerName, sessionId,
                        reader.ReadInt32(), reader.ReadString(),
                        reader.ReadString(), reader.ReadString());
                }

                case PbjMessageType.Reject:
                    return new RejectMessage((RejectReason)reader.ReadInt32(), reader.ReadString());

                case PbjMessageType.PeerJoined:
                    return new PeerJoinedMessage(reader.ReadInt32(), reader.ReadString());

                case PbjMessageType.PeerLeft:
                    return new PeerLeftMessage(reader.ReadInt32(), reader.ReadString());

                case PbjMessageType.Ready:
                {
                    var turn = reader.ReadInt32();
                    var count = ReadCount(reader, MaxOrdersPerReady, "order");
                    var orders = new OrderPayload[count];
                    for (var i = 0; i < count; i++)
                    {
                        orders[i] = OrderPayloadCodec.Read(reader);
                    }
                    return new ReadyMessage(turn, orders);
                }

                case PbjMessageType.TurnCommit:
                    return new TurnCommitMessage(reader.ReadInt32());

                case PbjMessageType.TurnComplete:
                    return new TurnCompleteMessage(reader.ReadInt32(), reader.ReadString());

                case PbjMessageType.Assignments:
                {
                    var count = ReadCount(reader, MaxPeersPerWelcome, "assignment");
                    var entries = new PeerAssignment[count];
                    for (var i = 0; i < count; i++)
                    {
                        var peerId = reader.ReadInt32();
                        var unitCount = ReadCount(reader, MaxUnitsPerPeer, "unit");
                        var units = new string[unitCount];
                        for (var u = 0; u < unitCount; u++)
                        {
                            units[u] = reader.ReadString() ?? string.Empty;
                        }
                        entries[i] = new PeerAssignment(peerId, units);
                    }
                    return new AssignmentsMessage(entries);
                }

                case PbjMessageType.Unready:
                    return new UnreadyMessage(reader.ReadInt32());

                case PbjMessageType.OrderResult:
                {
                    var turn = reader.ReadInt32();
                    var accepted = reader.ReadInt32();
                    // A result cannot reject more orders than a batch can hold.
                    var count = ReadCount(reader, MaxOrdersPerReady, "rejection");
                    var rejected = new RejectedOrder[count];
                    for (var i = 0; i < count; i++)
                    {
                        rejected[i] = new RejectedOrder(
                            reader.ReadInt32(), (OrderApplyResult)reader.ReadInt32());
                    }
                    return new OrderResultMessage(turn, accepted, rejected);
                }

                case PbjMessageType.CombatStart:
                    return new CombatStartMessage(reader.ReadInt32());

                case PbjMessageType.CombatEnd:
                    return new CombatEndMessage();

                case PbjMessageType.Snapshot:
                {
                    var turn = reader.ReadInt32();
                    var digest = reader.ReadString();
                    var count = ReadCount(reader, MaxUnitsPerSnapshot, "snapshot unit");
                    var units = new UnitSnapshot[count];
                    for (var i = 0; i < count; i++)
                    {
                        units[i] = ReadUnitSnapshot(reader);
                    }
                    return new SnapshotMessage(turn, digest, units);
                }

                case PbjMessageType.Keyframes:
                {
                    var turn = reader.ReadInt32();
                    var windowStart = reader.ReadSingle();
                    var windowEnd = reader.ReadSingle();
                    var trackCount = ReadCount(reader, MaxTracksPerKeyframes, "track");
                    var tracks = new UnitTrack[trackCount];
                    for (var i = 0; i < trackCount; i++)
                    {
                        var name = reader.ReadString();
                        var revealTime = reader.ReadSingle();
                        var hideTime = reader.ReadSingle();
                        var keyCount = ReadCount(reader, MaxKeysPerTrack, "transform key");
                        var keys = new TransformKey[keyCount];
                        for (var k = 0; k < keyCount; k++)
                        {
                            keys[k] = ReadTransformKey(reader);
                        }
                        tracks[i] = new UnitTrack(name, keys, revealTime, hideTime);
                    }
                    return new KeyframesMessage(turn, windowStart, windowEnd, tracks);
                }

                case PbjMessageType.Poses:
                {
                    var turn = reader.ReadInt32();
                    var partIndex = reader.ReadInt32();
                    var partCount = ReadCount(reader, MaxPosePartsPerTurn, "pose part");
                    return new PosesMessage(turn, partIndex, partCount, ReadUnitPoseTrack(reader));
                }

                case PbjMessageType.ReplayAssets:
                {
                    var turn = reader.ReadInt32();
                    var partIndex = reader.ReadInt32();
                    var partCount = ReadCount(reader, MaxAssetPartsPerTurn, "asset part");
                    return new ReplayAssetsMessage(
                        turn, partIndex, partCount, ReadAssetCapture(reader));
                }

                case PbjMessageType.ScenarioOffer:
                {
                    var saveName = reader.ReadString();
                    var totalBytes = reader.ReadInt32();
                    return new ScenarioOfferMessage(saveName, totalBytes, reader.ReadString());
                }

                case PbjMessageType.ScenarioRequest:
                    return new ScenarioRequestMessage(reader.ReadString());

                case PbjMessageType.Scenario:
                {
                    var saveName = reader.ReadString();
                    var digest = reader.ReadString();
                    var fileCount = ReadCount(reader, ScenarioPayload.MaxFiles, "scenario file");
                    var files = new ScenarioFile[fileCount];
                    for (var i = 0; i < fileCount; i++)
                    {
                        var name = reader.ReadString();
                        files[i] = new ScenarioFile(name, reader.ReadBytes());
                    }
                    return new ScenarioMessage(saveName, digest, files);
                }

                case PbjMessageType.LobbyState:
                {
                    var selectionVersion = reader.ReadInt32();
                    var saveKey = reader.ReadString();
                    var saveDigest = reader.ReadString();
                    // Same roster, so the same cap Welcome and Assignments use.
                    var count = ReadCount(reader, MaxPeersPerWelcome, "peer");
                    var peers = new LobbyPeerState[count];
                    for (var i = 0; i < count; i++)
                    {
                        peers[i] = new LobbyPeerState(
                            reader.ReadInt32(), reader.ReadString(), reader.ReadBool());
                    }
                    return new LobbyStateMessage(selectionVersion, saveKey, saveDigest, peers);
                }

                case PbjMessageType.LobbyReady:
                    return new LobbyReadyMessage(reader.ReadInt32());

                case PbjMessageType.LobbyUnready:
                    return new LobbyUnreadyMessage(reader.ReadInt32());

                case PbjMessageType.LobbyLoad:
                    return new LobbyLoadMessage(
                        reader.ReadInt32(), reader.ReadString(), reader.ReadString());

                // The cast is unvalidated, the same way RejectReason's is: an
                // unknown value from a peer becomes an outcome nothing matches,
                // which the host treats as a failure rather than throwing.
                case PbjMessageType.LobbyLoaded:
                    return new LobbyLoadedMessage(
                        reader.ReadInt32(), (LoadOutcome)reader.ReadInt32());

                case PbjMessageType.BasePosition:
                    return new BasePositionMessage(reader.ReadSingle(), reader.ReadSingle());

                case PbjMessageType.CombatOffer:
                    return new CombatOfferMessage(
                        reader.ReadString(), reader.ReadString(), reader.ReadInt32());

                // Unvalidated cast, like LobbyLoaded's above: an unknown outcome
                // from a peer becomes a value nothing matches, which the barrier
                // treats as a failure rather than throwing.
                case PbjMessageType.CombatEntered:
                    return new CombatEnteredMessage(reader.ReadInt32(), (LoadOutcome)reader.ReadInt32());

                case PbjMessageType.Ping:
                    return new PingMessage(reader.ReadInt32());

                case PbjMessageType.Pong:
                    return new PongMessage(reader.ReadInt32());

                case PbjMessageType.Bye:
                    return new ByeMessage(reader.ReadString());

                default:
                    throw new PbjProtocolException("Unknown message type byte " + (int)type + ".");
            }
        }

        /// <summary>
        /// Writes one unit's state as raw float bits — deliberately unquantised.
        /// </summary>
        /// <remarks>
        /// Quantisation is a <em>digest</em> rule, not a wire rule.
        /// <see cref="StateDigest"/> quantises because it compares values across
        /// two runtimes via formatting; there is no formatting here.
        /// <see cref="PbjWriter.WriteSingle"/> reinterprets the float's bits and
        /// emits them with explicit little-endian shifts, so the bytes are
        /// identical on Mono-under-Wine and .NET, NaN payloads included.
        /// Quantising the wire would lose precision for no benefit.
        /// </remarks>
        private static void WriteUnitSnapshot(PbjWriter writer, UnitSnapshot unit)
        {
            writer.WriteString(unit.Name);
            WriteVec3(writer, unit.Position);
            WriteVec4(writer, unit.Rotation);
            WriteVec3(writer, unit.Facing);
            writer.WriteSingle(unit.Integrity);
            writer.WriteBool(unit.IsHidden);
            writer.WriteBool(unit.IsHiddenDetectable);
            writer.WriteBool(unit.IsDeployed);
            writer.WriteBool(unit.HasArrivalTime);
            writer.WriteSingle(unit.ArrivalTime);
            writer.WriteBool(unit.IsWrecked);
            writer.WriteSingle(unit.WreckedAt);

            // Capped by truncation rather than by faulting the record, and the
            // asymmetry is deliberate: a snapshot is a correction, so refusing to
            // send one because a unit somehow grew a thirty-third wrecked part
            // would cost that unit its position and visibility too. The cap is
            // four times the largest real answer, so reaching it means the input
            // is already wrong.
            var parts = unit.WreckedParts;
            var count = parts.Count < MaxWreckedPartsPerUnit
                ? parts.Count
                : MaxWreckedPartsPerUnit;
            writer.WriteInt32(count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteString(parts[i].Socket);
                writer.WriteSingle(parts[i].Time);
            }

            // M16, and last in the record on purpose: it is the newest field, so
            // the tail is where a peer one version behind stops reading rather
            // than misreading everything after it.
            writer.WriteBool(unit.HasFrameIntegrity);
            var stated = unit.Parts;
            var statedCount = stated.Count < MaxPartsPerUnit
                ? stated.Count
                : MaxPartsPerUnit;
            writer.WriteInt32(statedCount);
            for (var i = 0; i < statedCount; i++)
            {
                writer.WriteString(stated[i].Socket);
                writer.WriteSingle(stated[i].Integrity);
                writer.WriteSingle(stated[i].Barrier);
            }

            // M17 stage 2, and now the tail for the same reason M16's pair was:
            // the newest fields go last, so a peer one version behind stops
            // reading rather than misreading everything after them.
            //
            // isWrecked is NOT here. It has crossed since M15 -- see the
            // WriteBool(unit.IsWrecked) above -- and stage 2 adds no second bit
            // for it, only an apply path. A duplicate would read as the flag
            // surviving one accessor and not the other.
            writer.WriteBool(unit.PilotDead);
            WritePilotDeathCause(writer, unit.PilotDeathCause);
            writer.WriteBool(unit.PilotKnockedOut);
            writer.WriteBool(unit.PilotEjected);
        }

        /// <summary>
        /// Writes the death cause, refusing an over-long one outright.
        /// </summary>
        /// <remarks>
        /// Refusal rather than truncation, and a fault rather than a clamp,
        /// because the two failure modes are not comparable: a snapshot clamped
        /// at <see cref="MaxUnitsPerSnapshot"/> loses units it could not have
        /// carried anyway, while a shortened cause is a <i>wrong value</i> that
        /// arrives looking correct. There is no legitimate input over the cap.
        /// </remarks>
        private static void WritePilotDeathCause(PbjWriter writer, string? cause)
        {
            if (cause != null && cause.Length > MaxPilotDeathCauseLength)
            {
                throw new PbjProtocolException(
                    "Pilot death cause of " + cause.Length
                        + " characters exceeds the maximum of " + MaxPilotDeathCauseLength + ".");
            }
            writer.WriteString(cause);
        }

        /// <summary>Reads the death cause, refusing an over-long one.</summary>
        /// <remarks>
        /// The peer is not this process. <see cref="WritePilotDeathCause"/>
        /// speaks only for what we send.
        /// </remarks>
        private static string? ReadPilotDeathCause(PbjReader reader)
        {
            var cause = reader.ReadString();
            if (cause != null && cause.Length > MaxPilotDeathCauseLength)
            {
                throw new PbjProtocolException(
                    "Pilot death cause of " + cause.Length
                        + " characters exceeds the maximum of " + MaxPilotDeathCauseLength + ".");
            }
            return cause;
        }

        // Every field is read into its own local rather than into the argument
        // list. C# does evaluate arguments left to right, so the older nested
        // form was correct — but "correct because of an evaluation-order rule"
        // is not what a wire decoder should rest on, and this record now has
        // seventeen fields rather than seven.
        private static UnitSnapshot ReadUnitSnapshot(PbjReader reader)
        {
            var name = reader.ReadString();
            var position = ReadVec3(reader);
            var rotation = ReadVec4(reader);
            var facing = ReadVec3(reader);
            var integrity = reader.ReadSingle();
            var isHidden = reader.ReadBool();
            var isHiddenDetectable = reader.ReadBool();
            var isDeployed = reader.ReadBool();
            var hasArrivalTime = reader.ReadBool();
            var arrivalTime = reader.ReadSingle();
            var isWrecked = reader.ReadBool();
            var wreckedAt = reader.ReadSingle();

            // ReadCount, not a bare ReadInt32: a peer must not be able to make us
            // allocate an arbitrary array by claiming a large one.
            var partCount = ReadCount(reader, MaxWreckedPartsPerUnit, "wrecked part");
            var parts = new PartDestruction[partCount];
            for (var i = 0; i < partCount; i++)
            {
                var socket = reader.ReadString();
                parts[i] = new PartDestruction(socket, reader.ReadSingle());
            }

            var hasFrameIntegrity = reader.ReadBool();
            var statedCount = ReadCount(reader, MaxPartsPerUnit, "stated part");
            var stated = new PartState[statedCount];
            for (var i = 0; i < statedCount; i++)
            {
                var socket = reader.ReadString();
                stated[i] = new PartState(socket, reader.ReadSingle(), reader.ReadSingle());
            }

            var pilotDead = reader.ReadBool();
            var pilotDeathCause = ReadPilotDeathCause(reader);
            var pilotKnockedOut = reader.ReadBool();
            var pilotEjected = reader.ReadBool();

            return new UnitSnapshot(
                name, position, rotation, facing, integrity,
                isHidden, isHiddenDetectable, isDeployed, hasArrivalTime, arrivalTime,
                isWrecked, wreckedAt, parts, stated, hasFrameIntegrity,
                pilotDead, pilotDeathCause, pilotKnockedOut, pilotEjected);
        }

        /// <summary>
        /// 32 bytes: time, position, rotation — raw float bits throughout, for
        /// the same reason <see cref="WriteUnitSnapshot"/> is unquantised.
        /// </summary>
        private static void WriteTransformKey(PbjWriter writer, TransformKey key)
        {
            writer.WriteSingle(key.Time);
            WriteVec3(writer, key.Position);
            WriteVec4(writer, key.Rotation);
        }

        private static TransformKey ReadTransformKey(PbjReader reader)
        {
            var time = reader.ReadSingle();
            var position = ReadVec3(reader);
            return new TransformKey(time, position, ReadVec4(reader));
        }

        private static void WriteTrailKey(PbjWriter writer, TrailKey key)
        {
            writer.WriteSingle(key.Time);
            writer.WriteSingle(key.TimeEnd);
            WriteVec3(writer, key.Position);
            WriteVec3(writer, key.Velocity);
            WriteVec3(writer, key.PerlinDirection);
            WriteVec3(writer, key.Tangent);
            WriteVec3(writer, key.Normal);
            WriteVec4(writer, key.Colour);
            writer.WriteSingle(key.Thickness);
            writer.WriteSingle(key.Texcoord);
        }

        private static TrailKey ReadTrailKey(PbjReader reader)
        {
            var time = reader.ReadSingle();
            var timeEnd = reader.ReadSingle();
            var position = ReadVec3(reader);
            var velocity = ReadVec3(reader);
            var perlinDirection = ReadVec3(reader);
            var tangent = ReadVec3(reader);
            var normal = ReadVec3(reader);
            var colour = ReadVec4(reader);
            var thickness = reader.ReadSingle();
            return new TrailKey(
                time,
                timeEnd,
                position,
                velocity,
                perlinDirection,
                tangent,
                normal,
                colour,
                thickness,
                reader.ReadSingle());
        }

        private static void WriteUnitLightKey(PbjWriter writer, UnitLightKey key)
        {
            writer.WriteSingle(key.Time);
            writer.WriteString(key.Socket);
            WriteVec3(writer, key.Position);
            WriteVec4(writer, key.Colour);
            writer.WriteSingle(key.Intensity);
            writer.WriteSingle(key.DurationBuildup);
            writer.WriteSingle(key.DurationStable);
            writer.WriteSingle(key.DurationFade);
        }

        private static UnitLightKey ReadUnitLightKey(PbjReader reader)
        {
            var time = reader.ReadSingle();
            var socket = reader.ReadString();
            var position = ReadVec3(reader);
            var colour = ReadVec4(reader);
            var intensity = reader.ReadSingle();
            var durationBuildup = reader.ReadSingle();
            var durationStable = reader.ReadSingle();
            return new UnitLightKey(
                time,
                socket,
                position,
                colour,
                intensity,
                durationBuildup,
                durationStable,
                reader.ReadSingle());
        }

        private static void WriteUnitPoseTrack(PbjWriter writer, UnitPoseTrack? track)
        {
            // A null track is a real shape rather than a defect: it is what a
            // decoded part carrying no unit reads back as, and encode has to be
            // able to reproduce whatever decode can produce or the round-trip
            // tests are asserting on a narrower type than the wire admits.
            var joints = track == null ? EmptyNames : track.Joints;
            var keys = track == null ? NoPoseKeys : track.Keys;
            var lights = track == null ? NoLightKeys : track.Lights;
            var reactions = track == null ? NoReactionPings : track.Reactions;
            var melees = track == null ? NoMelees : track.Melees;

            writer.WriteString(track?.Name);
            writer.WriteInt32(joints.Count);
            for (var i = 0; i < joints.Count; i++)
            {
                writer.WriteString(joints[i]);
            }

            writer.WriteInt32(keys.Count);
            for (var k = 0; k < keys.Count; k++)
            {
                var key = keys[k];
                writer.WriteSingle(key.Time);
                writer.WriteBool(key.SyncLeftEquipment);
                writer.WriteBool(key.SyncRightEquipment);
                writer.WriteInt32(key.Joints.Count);
                for (var j = 0; j < key.Joints.Count; j++)
                {
                    WriteVec3(writer, key.Joints[j].Position);
                    WriteVec4(writer, key.Joints[j].Rotation);
                }
            }

            writer.WriteInt32(lights.Count);
            for (var l = 0; l < lights.Count; l++)
            {
                WriteUnitLightKey(writer, lights[l]);
            }

            writer.WriteInt32(reactions.Count);
            for (var r = 0; r < reactions.Count; r++)
            {
                writer.WriteSingle(reactions[r]);
            }

            writer.WriteInt32(melees.Count);
            for (var m = 0; m < melees.Count; m++)
            {
                WriteMeleeTrajectory(writer, melees[m]);
            }
        }

        private static void WriteMeleeTrajectory(PbjWriter writer, MeleeTrajectory melee)
        {
            writer.WriteSingle(melee.TimeStart);
            writer.WriteSingle(melee.TimeEnd);
            writer.WriteBool(melee.PartUsed);
            writer.WriteString(melee.ShockwaveKey);
            WriteVec3(writer, melee.PosStart);
            WriteVec3(writer, melee.PosEnd);
        }

        private static MeleeTrajectory ReadMeleeTrajectory(PbjReader reader)
        {
            var timeStart = reader.ReadSingle();
            var timeEnd = reader.ReadSingle();
            var partUsed = reader.ReadBool();
            var shockwaveKey = reader.ReadString();
            var posStart = ReadVec3(reader);
            return new MeleeTrajectory(
                timeStart, timeEnd, partUsed, shockwaveKey, posStart, ReadVec3(reader));
        }

        private static UnitPoseTrack ReadUnitPoseTrack(PbjReader reader)
        {
            var name = reader.ReadString();

            var jointCount = ReadCount(reader, MaxJointsPerPose, "joint");
            var joints = new string?[jointCount];
            for (var i = 0; i < jointCount; i++)
            {
                joints[i] = reader.ReadString();
            }

            var keyCount = ReadCount(reader, MaxPoseKeysPerTrack, "pose key");
            var keys = new PoseKey[keyCount];
            for (var k = 0; k < keyCount; k++)
            {
                var time = reader.ReadSingle();
                var syncLeft = reader.ReadBool();
                var syncRight = reader.ReadBool();

                // Bounded by the same cap as the name list, not by that list's
                // own length: a track whose keys disagree with its names is
                // malformed, but it is the sender's fault and not a reason to
                // drop the peer mid-fight. PoseTracks rejects the disagreement
                // where it can be reported, and the array stays bounded here.
                var poseJointCount = ReadCount(reader, MaxJointsPerPose, "pose joint");
                var poseJoints = new JointPose[poseJointCount];
                for (var j = 0; j < poseJointCount; j++)
                {
                    var jointPosition = ReadVec3(reader);
                    poseJoints[j] = new JointPose(jointPosition, ReadVec4(reader));
                }

                keys[k] = new PoseKey(time, syncLeft, syncRight, poseJoints);
            }

            var lightCount = ReadCount(reader, MaxLightKeysPerUnit, "weapon light");
            var lights = new UnitLightKey[lightCount];
            for (var l = 0; l < lightCount; l++)
            {
                lights[l] = ReadUnitLightKey(reader);
            }

            var reactionCount = ReadCount(reader, MaxReactionPingsPerUnit, "reaction ping");
            var reactions = new float[reactionCount];
            for (var r = 0; r < reactionCount; r++)
            {
                reactions[r] = reader.ReadSingle();
            }

            var meleeCount = ReadCount(reader, MaxMeleesPerUnit, "melee");
            var melees = new MeleeTrajectory[meleeCount];
            for (var m = 0; m < meleeCount; m++)
            {
                melees[m] = ReadMeleeTrajectory(reader);
            }

            return new UnitPoseTrack(name, joints!, keys, lights, reactions, melees);
        }

        /// <summary>
        /// One part's three collections, each length-prefixed. M14.
        /// </summary>
        /// <remarks>
        /// Three counted lists rather than one tagged sequence. A tag byte per
        /// track would be a byte spent restating something the layout already
        /// says, and a decoder reading a tagged stream cannot bound its
        /// allocations before it has read them all.
        /// </remarks>
        private static void WriteAssetCapture(PbjWriter writer, AssetCapture assets)
        {
            writer.WriteInt32(assets.Standalone.Count);
            for (var i = 0; i < assets.Standalone.Count; i++)
            {
                var track = assets.Standalone[i];
                writer.WriteInt32(track.Id);
                WriteAssetTrackHead(writer, track.Head);
                WriteVec3(writer, track.Position);
                WriteVec4(writer, track.Rotation);
                WriteVec3(writer, track.Scale);
                WriteVec4(writer, track.VelocityAndDecay);
                WriteVec3(writer, track.PositionLocal);
            }

            writer.WriteInt32(assets.Projectiles.Count);
            for (var i = 0; i < assets.Projectiles.Count; i++)
            {
                var track = assets.Projectiles[i];
                writer.WriteInt32(track.Id);
                WriteAssetTrackHead(writer, track.Head);
                WriteVec3(writer, track.Scale);
                writer.WriteInt32(track.Keys.Count);
                for (var k = 0; k < track.Keys.Count; k++)
                {
                    WriteTransformKey(writer, track.Keys[k]);
                }
                writer.WriteInt32(track.Trail.Count);
                for (var k = 0; k < track.Trail.Count; k++)
                {
                    WriteTrailKey(writer, track.Trail[k]);
                }
            }

            writer.WriteInt32(assets.Beams.Count);
            for (var i = 0; i < assets.Beams.Count; i++)
            {
                var track = assets.Beams[i];
                writer.WriteInt32(track.Id);
                WriteAssetTrackHead(writer, track.Head);
                writer.WriteInt32(track.Keys.Count);
                for (var k = 0; k < track.Keys.Count; k++)
                {
                    var key = track.Keys[k];
                    writer.WriteSingle(key.Time);
                    WriteVec3(writer, key.Position);
                    WriteVec4(writer, key.Rotation);
                    WriteVec3(writer, key.Parameters);
                }
            }
        }

        private static AssetCapture ReadAssetCapture(PbjReader reader)
        {
            var standaloneCount = ReadCount(reader, MaxAssetsPerPart, "standalone asset");
            var standalone = new StandaloneAssetTrack[standaloneCount];
            for (var i = 0; i < standaloneCount; i++)
            {
                var id = reader.ReadInt32();
                var head = ReadAssetTrackHead(reader);
                var position = ReadVec3(reader);
                var rotation = ReadVec4(reader);
                var scale = ReadVec3(reader);
                var velocityAndDecay = ReadVec4(reader);
                standalone[i] = new StandaloneAssetTrack(
                    id, head, position, rotation, scale, velocityAndDecay, ReadVec3(reader));
            }

            var projectileCount = ReadCount(reader, MaxAssetsPerPart, "projectile asset");
            var projectiles = new ProjectileAssetTrack[projectileCount];
            for (var i = 0; i < projectileCount; i++)
            {
                var id = reader.ReadInt32();
                var head = ReadAssetTrackHead(reader);
                var scale = ReadVec3(reader);
                var keyCount = ReadCount(reader, MaxAssetKeysPerTrack, "projectile key");
                var keys = new TransformKey[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    keys[k] = ReadTransformKey(reader);
                }
                var trailCount = ReadCount(reader, MaxTrailPointsPerTrack, "trail point");
                var trail = new TrailKey[trailCount];
                for (var k = 0; k < trailCount; k++)
                {
                    trail[k] = ReadTrailKey(reader);
                }
                projectiles[i] = new ProjectileAssetTrack(id, head, scale, keys, trail);
            }

            var beamCount = ReadCount(reader, MaxAssetsPerPart, "beam asset");
            var beams = new BeamAssetTrack[beamCount];
            for (var i = 0; i < beamCount; i++)
            {
                var id = reader.ReadInt32();
                var head = ReadAssetTrackHead(reader);
                var keyCount = ReadCount(reader, MaxAssetKeysPerTrack, "beam key");
                var keys = new BeamKey[keyCount];
                for (var k = 0; k < keyCount; k++)
                {
                    var time = reader.ReadSingle();
                    var position = ReadVec3(reader);
                    var rotation = ReadVec4(reader);
                    keys[k] = new BeamKey(time, position, rotation, ReadVec3(reader));
                }
                beams[i] = new BeamAssetTrack(id, head, keys);
            }

            return new AssetCapture(standalone, projectiles, beams);
        }

        /// <summary>
        /// The head every asset track shares, with both optional blocks
        /// present-flagged rather than sentinelled.
        /// </summary>
        /// <remarks>
        /// A flag byte and a payload written only when set, because absence is
        /// a real instruction here and the common case besides: a hue offset of
        /// zero tells the client to flatten the effect's hue, while an absent
        /// one tells it to leave the prefab's own alone. A sentinel float would
        /// have to be a value the game cannot legitimately produce, and there
        /// is no such value in a 0..1 block. The flag also pays for itself —
        /// the 727 standalone effects of a measured turn are overwhelmingly
        /// plain, so writing the 36 bytes unconditionally would cost more than
        /// the flags do.
        /// </remarks>
        private static void WriteAssetTrackHead(PbjWriter writer, AssetTrackHead head)
        {
            writer.WriteString(head.AssetKey);
            writer.WriteSingle(head.TimeStart);
            writer.WriteSingle(head.TimeEnd);

            writer.WriteBool(head.Hue.HasValue);
            if (head.Hue.HasValue)
            {
                writer.WriteSingle(head.Hue.Value);
            }

            writer.WriteBool(head.Colour.HasValue);
            if (head.Colour.HasValue)
            {
                WriteVec4(writer, head.Colour.Value.From);
                WriteVec4(writer, head.Colour.Value.To);
            }
        }

        private static AssetTrackHead ReadAssetTrackHead(PbjReader reader)
        {
            var key = reader.ReadString();
            var timeStart = reader.ReadSingle();
            var timeEnd = reader.ReadSingle();

            float? hue = null;
            if (reader.ReadBool())
            {
                hue = reader.ReadSingle();
            }

            AssetColour? colour = null;
            if (reader.ReadBool())
            {
                var from = ReadVec4(reader);
                colour = new AssetColour(from, ReadVec4(reader));
            }

            return new AssetTrackHead(key, timeStart, timeEnd, hue, colour);
        }

        private static void WriteVec4(PbjWriter writer, Vec4 value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
            writer.WriteSingle(value.Z);
            writer.WriteSingle(value.W);
        }

        private static Vec4 ReadVec4(PbjReader reader)
        {
            return new Vec4(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static void WriteVec3(PbjWriter writer, Vec3 value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
            writer.WriteSingle(value.Z);
        }

        private static Vec3 ReadVec3(PbjReader reader)
        {
            return new Vec3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        private static readonly string[] EmptyNames = new string[0];
        private static readonly PoseKey[] NoPoseKeys = new PoseKey[0];
        private static readonly UnitLightKey[] NoLightKeys = new UnitLightKey[0];
        private static readonly float[] NoReactionPings = new float[0];
        private static readonly MeleeTrajectory[] NoMelees = new MeleeTrajectory[0];

        private static int ReadCount(PbjReader reader, int max, string what)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > max)
            {
                throw new PbjProtocolException(
                    "Message declares " + count + " " + what + "(s), outside 0.." + max + ".");
            }
            return count;
        }
    }
}
