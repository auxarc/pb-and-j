using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The snapshot message, which carries more per-unit state than any other and
    // so earns a part of its own: position and pose, visibility, wrecked parts,
    // part state, frame integrity and arrival time.
    // The last two of those encode presence and value separately, and are tested
    // for both, because "absent" and "present and zero" are different facts that
    // a single float cannot tell apart. Part state and wrecked parts are counted
    // lists with no presence flag, and are capped by truncation on encode.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
        [Fact]
        public void RoundTrip_Snapshot_PreservesEveryFieldOfEveryUnit()
        {
            var m = RoundTrip(new SnapshotMessage(4, "abc123", new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(1.5f, -2.25f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                    new Vec3(0f, 0f, -1f), 0.625f),
                new UnitSnapshot("pb_mech_02", new Vec3(-9f, 0f, 0.125f), new Vec4(1f, 0f, 0f, 0f),
                    new Vec3(1f, 0f, 0f), 0f),
            }));

            Assert.Equal(4, m.Turn);
            Assert.Equal("abc123", m.Digest);
            Assert.Equal(2, m.Units.Count);

            var alive = m.Units[0];
            Assert.Equal("pb_mech_01", alive.Name);
            Assert.Equal(1.5f, alive.Position.X);
            Assert.Equal(-2.25f, alive.Position.Y);
            Assert.Equal(3f, alive.Position.Z);
            Assert.Equal(0.1f, alive.Rotation.X);
            Assert.Equal(0.4f, alive.Rotation.W);
            Assert.Equal(-1f, alive.Facing.Z);
            Assert.Equal(0.625f, alive.Integrity);

            var second = m.Units[1];
            Assert.Equal(0.125f, second.Position.Z);
        }

        // Three booleans that all default to a different value from the one
        // being asserted, per unit, so a decoder that read them in the wrong
        // order or dropped one cannot pass. They are the last fields in the
        // record, which is exactly where an off-by-one in the reader lands.
        [Fact]
        public void RoundTrip_Snapshot_PreservesVisibilityPerUnit()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isHidden: true, isHiddenDetectable: false, isDeployed: false),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isHidden: false, isHiddenDetectable: true, isDeployed: true),
            }));

            Assert.True(m.Units[0].IsHidden);
            Assert.False(m.Units[0].IsHiddenDetectable);
            Assert.False(m.Units[0].IsDeployed);

            Assert.False(m.Units[1].IsHidden);
            Assert.True(m.Units[1].IsHiddenDetectable);
            Assert.True(m.Units[1].IsDeployed);
        }

        // Per unit, and with a different count on each, so a decoder that read
        // one unit's list into the next unit's record cannot pass. The list is
        // the LAST thing in the record, which is exactly where a reader that
        // dropped a field lands — and where the two removed death fields used to
        // sit, so this leg is also what pins their removal.
        [Fact]
        public void RoundTrip_Snapshot_PreservesWreckedPartsPerUnit()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    wreckedParts: new[] { new PartDestruction("equipment_left", 4.25f) }),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    wreckedParts: new[]
                    {
                        new PartDestruction("core", 0f),
                        // The spawn sentinel. A codec that quantised or clamped
                        // the stamp would erase the sign that tells a
                        // pre-battle wreck from one this turn produced.
                        new PartDestruction("leg_right", -100f),
                    }),
            }));

            Assert.Empty(m.Units[0].WreckedParts);

            var one = Assert.Single(m.Units[1].WreckedParts);
            Assert.Equal("equipment_left", one.Socket);
            Assert.Equal(4.25f, one.Time);

            Assert.Equal(2, m.Units[2].WreckedParts.Count);
            Assert.Equal("core", m.Units[2].WreckedParts[0].Socket);
            Assert.Equal(0f, m.Units[2].WreckedParts[0].Time);
            Assert.Equal("leg_right", m.Units[2].WreckedParts[1].Socket);
            Assert.Equal(-100f, m.Units[2].WreckedParts[1].Time);
        }

        // The unit's own wreck travels beside its parts and is a SEPARATE fact,
        // so the units here are chosen to disagree in both directions: one
        // wrecked with no parts recorded, one with parts and not wrecked. A
        // decoder that inferred either from the other cannot pass.
        [Fact]
        public void RoundTrip_Snapshot_PreservesTheUnitWreckIndependentlyOfItsParts()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isWrecked: true, wreckedAt: 7.5f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    wreckedParts: new[] { new PartDestruction("core", 2f) }),
                // Negative is the "no moment to wait for" convention, shared
                // with PartDestruction.Time, and a codec that clamped or
                // quantised the stamp would erase it.
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isWrecked: true, wreckedAt: -100f),
            }));

            Assert.True(m.Units[0].IsWrecked);
            Assert.Equal(7.5f, m.Units[0].WreckedAt);
            Assert.Empty(m.Units[0].WreckedParts);

            Assert.False(m.Units[1].IsWrecked);
            Assert.Single(m.Units[1].WreckedParts);

            Assert.True(m.Units[2].IsWrecked);
            Assert.Equal(-100f, m.Units[2].WreckedAt);
        }

        // Truncation rather than a fault, and the asymmetry is the point: a
        // snapshot is a correction, so refusing to send one over a part list
        // would cost that unit its position and visibility too.
        [Fact]
        public void RoundTrip_Snapshot_TruncatesAnOversizeWreckedPartList()
        {
            var parts = new PartDestruction[PbjMessageCodec.MaxWreckedPartsPerUnit + 5];
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = new PartDestruction("socket_" + i, i);
            }

            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f, wreckedParts: parts),
            }));

            Assert.Equal(PbjMessageCodec.MaxWreckedPartsPerUnit, m.Units[0].WreckedParts.Count);
            Assert.Equal("socket_0", m.Units[0].WreckedParts[0].Socket);
        }

        // M16. Per unit and with a different count on each, so a decoder that
        // read one unit's list into the next unit's record cannot pass. This is
        // now the LAST list in the record, which is where a reader that dropped a
        // field lands.
        [Fact]
        public void RoundTrip_Snapshot_PreservesPartStatePerUnit()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    parts: new[] { new PartState("core", 0.375f, 0.5f) }),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    parts: new[]
                    {
                        // Integrity and barrier are independent, so the pair here
                        // is deliberately asymmetric in both directions: a decoder
                        // that read one into the other cannot pass.
                        new PartState("equipment_left", 0f, 1f),
                        new PartState("equipment_right", 1f, 0f),
                    }),
            }));

            Assert.Empty(m.Units[0].Parts);

            var one = Assert.Single(m.Units[1].Parts);
            Assert.Equal("core", one.Socket);
            Assert.Equal(0.375f, one.Integrity);
            Assert.Equal(0.5f, one.Barrier);

            Assert.Equal(2, m.Units[2].Parts.Count);
            Assert.Equal(0f, m.Units[2].Parts[0].Integrity);
            Assert.Equal(1f, m.Units[2].Parts[0].Barrier);
            Assert.Equal(1f, m.Units[2].Parts[1].Integrity);
            Assert.Equal(0f, m.Units[2].Parts[1].Barrier);
        }

        // M16, and the pairing this exists for: the two states a single float
        // could not tell apart are "absent on the host" — which is the whole
        // player squad, mid-combat — and "present and zero", which is a real
        // value the game writes for a wrecked unit. Before M16 both travelled as
        // a bare 0f and the client wrote a component the host did not have.
        [Fact]
        public void RoundTrip_Snapshot_PreservesFrameIntegrityPresenceAndValue()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 0f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 0f, hasFrameIntegrity: true),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 0.625f, hasFrameIntegrity: true),
            }));

            Assert.False(m.Units[0].HasFrameIntegrity);
            Assert.True(m.Units[1].HasFrameIntegrity);
            Assert.Equal(0f, m.Units[1].Integrity);
            Assert.True(m.Units[2].HasFrameIntegrity);
            Assert.Equal(0.625f, m.Units[2].Integrity);
        }

        [Fact]
        public void RoundTrip_Snapshot_TruncatesAnOversizePartStateList()
        {
            var parts = new PartState[PbjMessageCodec.MaxPartsPerUnit + 5];
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = new PartState("socket_" + i, 1f, 1f);
            }

            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f, parts: parts),
            }));

            Assert.Equal(PbjMessageCodec.MaxPartsPerUnit, m.Units[0].Parts.Count);
            Assert.Equal("socket_0", m.Units[0].Parts[0].Socket);
        }

        // Presence and value travel separately, so the pairs chosen here are the
        // two a single combined field could not tell apart: absent (which a host
        // reports for its whole player squad) and present-but-negative (which a
        // client manufactures for the same units, because the save writer stamps
        // -1 for an absent component and the loader adds it back to everything
        // deployed). Collapsing them would make the correction a no-op.
        [Fact]
        public void RoundTrip_Snapshot_PreservesArrivalTimePresenceAndValue()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    hasArrivalTime: true, arrivalTime: -1f),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    hasArrivalTime: true, arrivalTime: 10.13f),
            }));

            Assert.False(m.Units[0].HasArrivalTime);
            Assert.Equal(0f, m.Units[0].ArrivalTime);

            Assert.True(m.Units[1].HasArrivalTime);
            Assert.Equal(-1f, m.Units[1].ArrivalTime);

            Assert.True(m.Units[2].HasArrivalTime);
            Assert.Equal(10.13f, m.Units[2].ArrivalTime);
        }

        [Fact]
        public void RoundTrip_Snapshot_WithNoUnits_PreservesEmpty()
        {
            Assert.Empty(RoundTrip(new SnapshotMessage(1, null, null)).Units);
        }

        [Fact]
        public void RoundTrip_Snapshot_PreservesNonFiniteFloatsExactly()
        {
            // Raw IEEE-754 bits, not quantised and never formatted — a wrecked
            // unit can carry a NaN transform and it must survive the wire
            // identically on Mono-under-Wine and .NET.
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("u", new Vec3(float.NaN, float.PositiveInfinity, float.NegativeInfinity),
                    new Vec4(float.Epsilon, 0f, 0f, 1f), new Vec3(0f, 0f, 0f), float.NaN),
            }));

            Assert.True(float.IsNaN(m.Units[0].Position.X));
            Assert.True(float.IsPositiveInfinity(m.Units[0].Position.Y));
            Assert.True(float.IsNegativeInfinity(m.Units[0].Position.Z));
            Assert.Equal(float.Epsilon, m.Units[0].Rotation.X);
            Assert.True(float.IsNaN(m.Units[0].Integrity));
        }

        // --- M17 stage 2, wire v10: the pilot ---

        // Per unit, at the very tail of the record, and with each unit setting a
        // DIFFERENT combination — so a reader that dropped one field, read them
        // in the wrong order, or let one unit's tail run into the next unit's
        // name cannot pass. The three pilot bits are now the last things in the
        // record, which is exactly where an off-by-one lands.
        [Fact]
        public void RoundTrip_Snapshot_PreservesThePilotPerUnit()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    pilotDead: true, pilotDeathCause: "trauma",
                    pilotKnockedOut: false, pilotEjected: true),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    pilotDead: false, pilotDeathCause: null,
                    pilotKnockedOut: true, pilotEjected: false),
            }));

            Assert.True(m.Units[0].PilotDead);
            Assert.Equal("trauma", m.Units[0].PilotDeathCause);
            Assert.False(m.Units[0].PilotKnockedOut);
            Assert.True(m.Units[0].PilotEjected);

            Assert.False(m.Units[1].PilotDead);
            Assert.Null(m.Units[1].PilotDeathCause);
            Assert.True(m.Units[1].PilotKnockedOut);
            Assert.False(m.Units[1].PilotEjected);
        }

        // isWrecked has crossed since M15 and stage 2 adds NO second bit for it.
        // Asserted here rather than argued: a duplicate would show up as the flag
        // surviving one accessor and not the other.
        [Fact]
        public void RoundTrip_Snapshot_StillCarriesTheUnitWreckExactlyOnce()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isWrecked: true, wreckedAt: 3.5f,
                    pilotDead: true, pilotDeathCause: "trauma"),
            }));

            Assert.True(m.Units[0].IsWrecked);
            Assert.Equal(3.5f, m.Units[0].WreckedAt);
            Assert.True(m.Units[0].PilotDead);
        }

        // 🔴 The cap VALUE, pinned as a literal. Every other test here derives its
        // string length from the constant, which pins the mechanism ("the check
        // uses the cap") and says nothing about the number -- raising the cap
        // raised those tests with it and not one of them failed. Proven by doing
        // exactly that. This is the row that makes "raise the cap" a mutation
        // rather than a no-op.
        [Fact]
        public void MaxPilotDeathCauseLength_IsThirtyTwo()
        {
            // Small on purpose: the field is a content key the game writes
            // itself, not free-form text. It also has to stay well under
            // PbjWriter.MaxStringLength, or a test writing cap+1 characters
            // would trip the writer's own limit instead of this one -- which is
            // how the first version of the two tests below passed for the wrong
            // reason.
            Assert.Equal(32, PbjMessageCodec.MaxPilotDeathCauseLength);
            Assert.True(PbjMessageCodec.MaxPilotDeathCauseLength * 4 < PbjWriter.MaxStringLength);
        }

        // Refusal, not truncation, and on the ENCODE side: an over-long cause is
        // this machine's own bug, and a silently shortened one would travel as a
        // cause string the host never wrote. Raising the cap is the mutation.
        [Fact]
        public void Encode_SnapshotWithAnOverlongDeathCause_Throws()
        {
            var cause = new string('x', PbjMessageCodec.MaxPilotDeathCauseLength + 1);
            var message = new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f, pilotDead: true, pilotDeathCause: cause),
            });

            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Encode(message));
        }

        // The cap holds at exactly the limit, so the refusal above is a fact
        // about the cap and not about any long string at all.
        [Fact]
        public void RoundTrip_SnapshotWithADeathCauseAtTheCap_Survives()
        {
            var cause = new string('x', PbjMessageCodec.MaxPilotDeathCauseLength);
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f, pilotDead: true, pilotDeathCause: cause),
            }));

            Assert.Equal(cause, m.Units[0].PilotDeathCause);
        }

        // And the decode side, which the encode cap cannot speak for: a peer is
        // not this process and can put anything on the wire.
        [Fact]
        public void Decode_SnapshotWithAnOverlongDeathCause_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Snapshot);
            writer.WriteInt32(1);
            writer.WriteString("d");
            writer.WriteInt32(1);
            writer.WriteString("pb_mech_01");
            for (var i = 0; i < 10; i++)
            {
                writer.WriteSingle(0f);      // position, rotation, facing
            }
            writer.WriteSingle(1f);          // integrity
            writer.WriteBool(false);         // isHidden
            writer.WriteBool(false);         // isHiddenDetectable
            writer.WriteBool(true);          // isDeployed
            writer.WriteBool(false);         // hasArrivalTime
            writer.WriteSingle(0f);          // arrivalTime
            writer.WriteBool(false);         // isWrecked
            writer.WriteSingle(0f);          // wreckedAt
            writer.WriteInt32(0);            // wrecked parts
            writer.WriteBool(false);         // hasFrameIntegrity
            writer.WriteInt32(0);            // part states
            writer.WriteBool(true);          // pilotDead
            writer.WriteString(new string('x', PbjMessageCodec.MaxPilotDeathCauseLength + 1));
            // 🔴 The record MUST be completed. Stopping here made the reader run
            // off the end of the buffer, so Decode threw for framing rather than
            // for the cap and this test passed with the cap check deleted --
            // proven by deleting it and watching this stay green.
            writer.WriteBool(false);         // pilotKnockedOut
            writer.WriteBool(false);         // pilotEjected

            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_SnapshotWithTooManyUnits_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Snapshot);
            writer.WriteInt32(1);
            writer.WriteString("d");
            writer.WriteInt32(PbjMessageCodec.MaxUnitsPerSnapshot + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Encode_SnapshotAtTheCap_StaysWellUnderTheFrameLimit()
        {
            // The size claim the whole "the writer thread is not a snapshot
            // prerequisite" argument rests on.
            var units = new UnitSnapshot[PbjMessageCodec.MaxUnitsPerSnapshot];
            for (var i = 0; i < units.Length; i++)
            {
                units[i] = Snapshot("pb_mech_" + i.ToString("00"));
            }

            var bytes = PbjMessageCodec.Encode(new SnapshotMessage(1, "3f9c1a04", units));
            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength / 16,
                $"a full snapshot was {bytes.Length} bytes, more than 1/16th of the frame limit");
        }
    }
}
