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
                        protocolVersion, sessionId, assignedPeerId, hostName, peers, reader.ReadInt32());
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

                case PbjMessageType.Bye:
                    return new ByeMessage(reader.ReadString());

                default:
                    throw new PbjProtocolException("Unknown message type byte " + (int)type + ".");
            }
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
