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
        /// included. At ~85 bytes a unit this caps a snapshot near 11 KB, about
        /// 1% of <c>PbjRuntime.MaxFrameLength</c>.
        /// </remarks>
        public const int MaxUnitsPerSnapshot = 128;

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
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadString(), reader.ReadString());

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
                        reader.ReadInt32(), reader.ReadString());
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
            writer.WriteSingle(unit.Rotation.X);
            writer.WriteSingle(unit.Rotation.Y);
            writer.WriteSingle(unit.Rotation.Z);
            writer.WriteSingle(unit.Rotation.W);
            WriteVec3(writer, unit.Facing);
            writer.WriteSingle(unit.Integrity);
            writer.WriteBool(unit.IsDead);
            writer.WriteSingle(unit.DeathTime);
        }

        private static UnitSnapshot ReadUnitSnapshot(PbjReader reader)
        {
            var name = reader.ReadString();
            var position = ReadVec3(reader);
            var rotation = new Vec4(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var facing = ReadVec3(reader);
            return new UnitSnapshot(
                name, position, rotation, facing,
                reader.ReadSingle(), reader.ReadBool(), reader.ReadSingle());
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
