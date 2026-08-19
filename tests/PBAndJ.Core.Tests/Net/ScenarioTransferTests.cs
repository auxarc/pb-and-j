using System.Collections.Generic;
using System.Linq;
using System.Text;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Where to start reading. M9's fixture: the fake bridge every session here is
    // built on, a well-formed save to offer, and the helpers that sift effects into
    // the one you meant.
    //
    // ScenarioTransferTests is one class split across this file and its siblings;
    // this one holds what they share.
    /// <summary>
    /// M9: the host offers its combat save, a peer that wants it asks, and the
    /// bytes cross. Replaces the hand-carried folder copy stage 2 needed.
    /// </summary>
    public partial class ScenarioTransferTests
    {
        private readonly FakeGameBridge bridge = new FakeGameBridge();

        private static ScenarioPayload Save(string name = "pbj_combat_test", string content = "zipped")
        {
            return new ScenarioPayload(name, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, Encoding.UTF8.GetBytes(content)),
                new ScenarioFile(ScenarioPayload.MetadataFileName, Encoding.UTF8.GetBytes("ver: 1")),
            });
        }

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

        private static IEnumerable<T> Messages<T>(IEnumerable<PbjEffect> effects) where T : PbjMessage =>
            effects.OfType<SendEffect>().Select(s => s.Message).OfType<T>();
    }
}
