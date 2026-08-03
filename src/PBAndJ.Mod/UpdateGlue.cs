using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PBAndJ.Core;
using PBAndJ.Core.Net;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;
using UnityEngine.Networking;

namespace PBAndJ.Mod
{
    // Asks GitHub whether a newer mod build has been released.
    //
    // Humble object, as everywhere else: this does the network and the JSON, and
    // every decision — is this newer, what should it say — lives in
    // PBAndJ.Core's UpdateCheck and UpdateLog, under the coverage gate. The two
    // things that can be quietly wrong are version ordering ("0.9.0" sorts after
    // "0.10.0" as a string) and the wording of the result, and both are tested
    // there rather than here.
    //
    // NOT run at launch, deliberately. The mod's standing promise is that
    // nothing leaves the machine unless a session is started, and an unsolicited
    // call to GitHub every time the game boots would quietly break it. It fires
    // when a session starts — which is both an explicit opt-in to networking and
    // exactly when a version mismatch is about to matter — or on demand via
    // pbj.check-update.
    [ExcludeFromCodeCoverage]
    internal static class UpdateGlue
    {
        private const string ReleasesUrl =
            "https://api.github.com/repos/auxarc/pb-and-j/releases/latest";

        private const string ReleasesPage =
            "https://github.com/auxarc/pb-and-j/releases/latest";

        private const int TimeoutSeconds = 10;

        /// <summary>
        /// The MonoBehaviour we run coroutines on.
        /// </summary>
        /// <remarks>
        /// UnityWebRequest needs one and the mod has none of its own. Heartbeat
        /// is already patched for the network pump, lives for the whole process,
        /// and is handed to us by Harmony as __instance — cheaper and less
        /// fragile than creating and owning a GameObject.
        /// </remarks>
        private static MonoBehaviour? host;

        /// <summary>One in flight at a time; a second request would tell us nothing new.</summary>
        private static bool checking;

        /// <summary>
        /// Whether this process has already put the update dialog in front of
        /// somebody. Asking once and being ignored has to mean ignored.
        /// </summary>
        private static bool offered;

        internal static void SetCoroutineHost(MonoBehaviour behaviour)
        {
            host = behaviour;
        }

        /// <summary>
        /// Starts a check if one is not already running. Never throws and never
        /// blocks — a caller starting a session must not care whether this works.
        /// </summary>
        /// <param name="offerPrompt">
        /// Whether a dialog may be raised if the build turns out to be stale.
        /// False for the automatic session-start check: that fires from
        /// pbj.host/pbj.join, which can happen mid-combat, and a modal landing
        /// on somebody in the middle of a turn is worse than a log line. The
        /// explicit pbj.check-update path passes true, and M10c's connect screen
        /// will too — it runs at the main menu, where a modal is expected.
        /// </param>
        internal static void CheckInBackground(bool offerPrompt = false)
        {
            if (checking || host == null)
            {
                return;
            }

            try
            {
                checking = true;
                host.StartCoroutine(Run(offerPrompt));
            }
            catch (Exception e)
            {
                checking = false;
                Debug.Log(UpdateLog.CheckFailed(e.GetType().Name + ": " + e.Message));
            }
        }

        public static string CheckUpdate()
        {
            if (host == null)
            {
                return UpdateLog.CheckFailed("the game is not far enough through startup yet");
            }
            if (checking)
            {
                return "[pb-and-j] an update check is already running";
            }
            CheckInBackground(offerPrompt: true);
            return UpdateLog.Checking("api.github.com");
        }

        private static IEnumerator Run(bool offerPrompt)
        {
            Debug.Log(UpdateLog.Checking("api.github.com"));

            using (var request = UnityWebRequest.Get(ReleasesUrl))
            {
                // GitHub refuses requests without a User-Agent, and the version
                // header pins the response shape this code parses.
                request.SetRequestHeader("User-Agent", "pb-and-j/" + PbjProtocol.ModVersion);
                request.SetRequestHeader("Accept", "application/vnd.github+json");
                request.timeout = TimeoutSeconds;

                yield return request.SendWebRequest();

                string? body = null;
                string? failure = null;

                // Unity moved the failure flags between versions; read whichever
                // this build has rather than binding to one at compile time.
                if (request.responseCode == 404)
                {
                    // Not a failure: this is what GitHub says when a repo has
                    // never cut a release, which is an ordinary state and not
                    // something to put a scary line in the log about.
                    checking = false;
                    Debug.Log(UpdateLog.NoReleases());
                    yield break;
                }

                if (IsFailure(request))
                {
                    failure = string.IsNullOrEmpty(request.error) ? "request failed" : request.error;
                }
                else
                {
                    body = request.downloadHandler?.text;
                    if (string.IsNullOrEmpty(body))
                    {
                        failure = "empty response";
                    }
                }

                if (failure != null)
                {
                    checking = false;
                    Debug.Log(UpdateLog.CheckFailed(failure));
                    yield break;
                }

                Report(body!, offerPrompt);
            }

            checking = false;
        }

        private static void Report(string body, bool offerPrompt)
        {
            string? tag = null;
            string? assetUrl = null;

            try
            {
                // A real parser, not a string scan. A release body is
                // user-authored prose that can contain anything, including a
                // convincing "tag_name": "v9.9.9" — scanning for the key would
                // happily believe it.
                var release = JObject.Parse(body);
                tag = (string?)release["tag_name"];

                if (release["assets"] is JArray assets)
                {
                    foreach (var asset in assets)
                    {
                        var name = (string?)asset["name"];
                        if (name != null && name.StartsWith("pb-and-j-mod", StringComparison.OrdinalIgnoreCase))
                        {
                            assetUrl = (string?)asset["browser_download_url"];
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.Log(UpdateLog.CheckFailed("could not read GitHub's reply: " + e.GetType().Name));
                return;
            }

            var result = UpdateCheck.Compare(PbjProtocol.ModVersion, tag);
            Debug.Log(UpdateLog.Describe(result, assetUrl ?? ReleasesPage));

            if (!offerPrompt)
            {
                return;
            }

            // Whether to interrupt somebody is a rule, so it lives in Core and is
            // tested there. All that is decided here is whether the game has a
            // dialog to open yet.
            if (UpdatePrompt.Decide(result, DialogAvailable(), offered) == UpdateOffer.PointAtReleasePage)
            {
                Offer(result);
            }
        }

        /// <summary>
        /// The game's confirmation view is a scene singleton, so early in startup
        /// there is simply nothing to open.
        /// </summary>
        private static bool DialogAvailable()
        {
            try
            {
                return CIViewDialogConfirmation.ins != null;
            }
            catch (Exception)
            {
                // A missing or moved view is not worth taking the check down over.
                return false;
            }
        }

        /// <summary>
        /// Opens the game's own confirmation dialog and, on confirm, the releases
        /// page in a browser.
        /// </summary>
        /// <remarks>
        /// The shape is lifted from the game's own ExternalLinkHelper, down to
        /// the hue — a confirm-then-OpenURL modal is a thing Phantom Brigade
        /// already does for its Discord and changelog links, so this looks and
        /// behaves like part of the game rather than like a mod improvising.
        ///
        /// The URL is the compile-time ReleasesPage constant and never a link
        /// taken from GitHub's reply, so there is no question of whether the
        /// response could talk us into opening something else.
        ///
        /// Nothing is installed. The game loads mod assemblies once, during
        /// startup, with Assembly.LoadFrom into an AppDomain it cannot unload, so
        /// swapping a DLL underneath a running game is not possible and the
        /// wording says as much.
        /// </remarks>
        private static void Offer(UpdateResult result)
        {
            try
            {
                offered = true;
                CIViewDialogConfirmation.ins.Open(
                    UpdateLog.PromptHeader(),
                    UpdateLog.PromptBody(result),
                    () => Application.OpenURL(ReleasesPage),
                    null, null, null, 0.55f);
            }
            catch (Exception e)
            {
                // The check already logged the result, so a dialog that will not
                // open costs information nobody has lost.
                Debug.Log(UpdateLog.CheckFailed("could not open the update dialog: " + e.GetType().Name));
            }
        }

        /// <summary>
        /// True if the request did not succeed, across Unity versions.
        /// </summary>
        /// <remarks>
        /// <c>isNetworkError</c>/<c>isHttpError</c> were replaced by
        /// <c>result</c> in 2020.2 and the game is on 2020.3, but reflection
        /// costs nothing here and means a game update cannot turn this into a
        /// MissingMethodException at runtime — where it would look like a
        /// networking bug rather than a compile-target drift.
        /// </remarks>
        private static bool IsFailure(UnityWebRequest request)
        {
            try
            {
                var resultProperty = typeof(UnityWebRequest).GetProperty(
                    "result", BindingFlags.Instance | BindingFlags.Public);
                if (resultProperty != null)
                {
                    return Convert.ToInt32(resultProperty.GetValue(request)) != 1;
                }
            }
            catch (Exception)
            {
                // Fall through to the response code.
            }

            return request.responseCode < 200 || request.responseCode >= 300;
        }

        internal static void RegisterConsoleCommand()
        {
            var method = typeof(UpdateGlue).GetMethod(
                nameof(CheckUpdate), BindingFlags.Static | BindingFlags.Public);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.check-update"));
        }
    }
}
