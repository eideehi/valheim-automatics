using System;
using System.Collections.Generic;
using HarmonyLib;
using ProcessingLogics = Automatics.AutomaticProcessing.Logics;

namespace Automatics.SmokeTest
{
    // Discovery stays real, including cached search results. Only the mutation gate is
    // restricted, so enabling Processing for a smoke run cannot consume a user's chest.
    internal static class ProcessingIsolation
    {
        private const string HarmonyId = "net.eidee.automatics.smoke.processing.isolation";
        private static readonly List<Container> Fixtures = new List<Container>();
        private static readonly Func<string, bool> DetachHandBack = reason => Detach(out _);
        private static Harmony _harmony;

        public static bool Attach(out string problem)
        {
            problem = null;
            if (!ModuleRegistry.RuntimeToggleSupported)
            {
                problem = "this Harmony build cannot remove the Processing isolation patch at runtime";
                return false;
            }

            var gate = AccessTools.Method(typeof(ProcessingLogics), nameof(ProcessingLogics.TryClaimContainer),
                new[] { typeof(Container), typeof(Container).MakeByRefType() });
            if (gate == null)
            {
                problem = "the Processing container mutation gate was not found";
                return false;
            }

            if (_harmony != null && !Detach(out problem)) return false;
            SmokeSession.TrackHandBack(DetachHandBack);
            Fixtures.Clear();
            _harmony = new Harmony(HarmonyId);
            try
            {
                _harmony.Patch(gate, new HarmonyMethod(typeof(ProcessingIsolation), nameof(ClaimPrefix)));
                return true;
            }
            catch (Exception e)
            {
                problem = $"Processing isolation could not be added ({e.GetType().Name}: {e.Message})";
                Detach(out _);
                return false;
            }
        }

        public static void Register(Container container)
        {
            if (!ReferenceEquals(container, null) && !Fixtures.Exists(x => ReferenceEquals(x, container)))
                Fixtures.Add(container);
        }

        public static void Unregister(Container container)
        {
            Fixtures.RemoveAll(x => ReferenceEquals(x, container));
        }

        public static bool Detach(out string problem)
        {
            problem = null;
            Fixtures.Clear();
            if (_harmony != null)
            {
                try
                {
                    _harmony.UnpatchSelf();
                }
                catch (Exception e)
                {
                    problem = $"Processing isolation could not be removed ({e.GetType().Name}: {e.Message})";
                    return false;
                }

                _harmony = null;
            }

            SmokeSession.ReleaseHandBack(DetachHandBack);
            return true;
        }

        // Do not touch askable on refusal: even an ownership request for a user's container
        // would escape the fixture boundary. Registered fixtures use every real access rule.
        private static bool ClaimPrefix(Container container, ref bool __result)
        {
            if (Fixtures.Exists(x => ReferenceEquals(x, container))) return true;
            __result = false;
            return false;
        }
    }
}
