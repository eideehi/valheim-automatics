using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace Automatics
{
    /// <summary>
    ///     One module as the registry sees it. The registry only ever reads
    ///     <see cref="ModuleToggle" />; nothing here lets it write the stored setting back, which
    ///     is how R1.3b is kept structural rather than conventional.
    /// </summary>
    internal interface IAutomaticsModule
    {
        /// <summary>
        ///     The suffix of this module's Harmony id, for example <c>automatic-pickup</c>. It is
        ///     handed to <see cref="Automatics.GetHarmonyId" />, so every module owns a separate
        ///     id and no teardown can reach the always-on root patches (R3.1).
        /// </summary>
        string Name { get; }

        /// <summary>The config section, for example <c>automatic_pickup</c>.</summary>
        string ConfigSection { get; }

        /// <summary>
        ///     False means the module keeps the historical start-up-only path: the registry does
        ///     not subscribe to its toggle, so changing the setting takes a restart.
        /// </summary>
        bool SupportsRuntimeToggle { get; }

        /// <summary>Valid once <see cref="BindConfig" /> has run.</summary>
        ConfigEntry<AutomaticsModule> ModuleToggle { get; }

        /// <summary>
        ///     The R1.5a stop request. The registry raises it the moment a transition is asked
        ///     for, long before the pump applies it, so a hot path stops producing side effects
        ///     in the frame the user changed the setting rather than in the frame after it.
        ///     Implementations only write their own static field: the hot path reads that field
        ///     directly instead of paying for an interface call.
        /// </summary>
        void SetStopRequested(bool value);

        void BindConfig();

        /// <summary>
        ///     Acquires everything this activation owns through <paramref name="scope" />, and
        ///     raises the module's own effective-state flag as its very last statement, so a
        ///     throw part way through leaves the module inactive.
        /// </summary>
        void Enable(ModuleScope scope);

        /// <summary>
        ///     Lowers the effective-state flag as its very first statement (R1.5) and resets
        ///     module-private session state. It must not undo anything the scope owns: patches,
        ///     subscriptions and components are removed by <see cref="ModuleScope.Dispose" /> in
        ///     reverse acquisition order (R1.6).
        /// </summary>
        void Disable(ModuleScope scope);
    }

    /// <summary>
    ///     A component a <see cref="ModuleScope" /> can stamp with a generation and shut down
    ///     synchronously before it destroys it (R4.5 / R1.6).
    /// </summary>
    internal interface IScopedComponent
    {
        /// <summary>
        ///     The activation that owns this component. Zero is reserved for "not attached yet":
        ///     the scope writes the real generation immediately after <c>AddComponent</c>, and a
        ///     component that reads this from its own <c>Awake</c> would still see zero.
        /// </summary>
        int ScopeGeneration { get; set; }

        /// <summary>
        ///     Called synchronously by the owning scope immediately before it destroys this
        ///     component. Engine destruction is deferred to the end of the frame, so anything
        ///     that cannot wait that long - a running coroutine above all - has to be stopped
        ///     here rather than in <c>OnDestroy</c>.
        /// </summary>
        void OnScopeDetaching();
    }

    /// <summary>
    ///     Thrown by <see cref="ModuleScope.Attach{TComponent}" /> when the target still carries a
    ///     component from an older generation that the engine has not destroyed yet. The registry
    ///     turns this into a deferral and retries on the next tick; it is never allowed to escape
    ///     into a game call stack, which is what <see cref="ModuleScope.TryAttach{TComponent}" />
    ///     is for.
    /// </summary>
    internal sealed class ScopeAttachDeferredException : Exception
    {
        public ScopeAttachDeferredException(string message) : base(message)
        {
        }
    }

    /// <summary>
    ///     Startup probe for the Harmony features the module lifecycle depends on, and the
    ///     source of the one-line environment banner (R3.4).
    ///     Every Harmony member this class needs is looked up through <see cref="AccessTools" />
    ///     rather than called directly, so a Harmony build that no longer exposes the unpatch
    ///     API degrades to <see cref="CanUnpatch" /> == false instead of throwing (R3.2).
    /// </summary>
    internal static class HarmonyCapabilities
    {
        private const string Unknown = "unknown";

        private static bool _probed;

        /// <summary>
        ///     False selects the explicitly degraded mode of R3.2: a module that is turned off
        ///     can only be soft gated, and its Harmony patches stay applied until the game is
        ///     restarted.
        ///     True only means the API is present and shaped as expected. It is not evidence
        ///     that removing a patch actually works at runtime; that is AC-R3, and it cannot be
        ///     established before the game runs.
        /// </summary>
        public static bool CanUnpatch { get; private set; }

        /// <summary>
        ///     The version of whichever assembly supplied <see cref="Harmony" />. The banner
        ///     labels it HarmonyX because that is what BepInEx 5 ships, but the value is read
        ///     from the loaded assembly rather than assumed.
        /// </summary>
        public static string HarmonyVersion { get; private set; } = Unknown;

        public static string BepInExVersion { get; private set; } = Unknown;

        /// <summary>
        ///     Valheim's own version, or <c>unknown</c> when it cannot be read. See
        ///     <see cref="ReadValheimVersion" /> for the shapes that are understood.
        /// </summary>
        public static string ValheimVersion { get; private set; } = Unknown;

        /// <summary>
        ///     Runs once at startup and never throws. A probe that cannot finish is not recorded
        ///     as done, so the conservative defaults it started from are what callers keep.
        /// </summary>
        public static void Probe()
        {
            if (_probed) return;

            try
            {
                CanUnpatch = ProbeUnpatchSupport();
                HarmonyVersion = ReadAssemblyVersion(typeof(Harmony).Assembly);
                BepInExVersion = ReadAssemblyVersion(typeof(BaseUnityPlugin).Assembly);
                ValheimVersion = ReadValheimVersion();
                _probed = true;
            }
            catch (Exception e)
            {
                // Containment of last resort. Nothing below is expected to throw, but this
                // runs during plugin startup, where an escape would take the whole mod down.
                CanUnpatch = false;
                LogDebug("The Harmony capability probe did not complete", e);
            }
        }

        /// <summary>
        ///     The startup log line required by R3.4.
        /// </summary>
        public static string DescribeEnvironment()
        {
            return
                $"Environment: Valheim {ValheimVersion}, BepInEx {BepInExVersion}, " +
                $"HarmonyX {HarmonyVersion}, runtime patch removal: " +
                $"{(CanUnpatch ? "available" : "unavailable")}";
        }

        /// <summary>
        ///     Confirms that the loaded Harmony exposes the instance method that removes every
        ///     patch owned by one Harmony id, and the static predicate that reports whether an
        ///     id still owns any patch. Both are resolved by name so that a build without them
        ///     answers false instead of failing to load.
        ///     This establishes the shape of the API and nothing more: a method that is present
        ///     but throws, or that removes nothing, still probes as available. Whether removal
        ///     works is a runtime question (AC-R3).
        /// </summary>
        private static bool ProbeUnpatchSupport()
        {
            try
            {
                var unpatchSelf =
                    AccessTools.Method(typeof(Harmony), "UnpatchSelf", Type.EmptyTypes);
                if (unpatchSelf == null || unpatchSelf.IsStatic) return false;

                var hasAnyPatches = AccessTools.Method(typeof(Harmony), "HasAnyPatches",
                    new[] { typeof(string) });
                if (hasAnyPatches == null || !hasAnyPatches.IsStatic) return false;

                return hasAnyPatches.ReturnType == typeof(bool);
            }
            catch (Exception e)
            {
                LogDebug("Harmony capability probe failed", e);
                return false;
            }
        }

        /// <summary>
        ///     Valheim's version type sits in the global namespace and its name collides with
        ///     <see cref="System.Version" />, so it is taken from the assembly that defines the
        ///     game's own types rather than resolved by bare name. The name-based lookup is kept
        ///     only as a fallback.
        ///     Understood shapes: a static <c>CurrentVersion</c> property, or a static
        ///     <c>CurrentVersion</c> field. Anything else reports <c>unknown</c>; the banner is
        ///     diagnostic, so a missing version is not worth guessing at.
        /// </summary>
        private static string ReadValheimVersion()
        {
            try
            {
                var type = typeof(Player).Assembly.GetType("Version", false) ??
                           AccessTools.TypeByName("Version");
                if (type == null) return Unknown;

                object value = null;

                var getter = AccessTools.Property(type, "CurrentVersion")?.GetGetMethod(true);
                if (getter != null && getter.IsStatic)
                {
                    value = getter.Invoke(null, Array.Empty<object>());
                }
                else
                {
                    var field = AccessTools.Field(type, "CurrentVersion");
                    if (field != null && field.IsStatic)
                        value = field.GetValue(null);
                }

                var text = value?.ToString();
                return string.IsNullOrEmpty(text) ? Unknown : text;
            }
            catch (Exception e)
            {
                LogDebug("Failed to read the Valheim version", e);
                return Unknown;
            }
        }

        private static string ReadAssemblyVersion(Assembly assembly)
        {
            if (assembly == null) return Unknown;

            try
            {
                var attributes = assembly.GetCustomAttributes(
                    typeof(AssemblyInformationalVersionAttribute), false);
                if (attributes.Length > 0 &&
                    attributes[0] is AssemblyInformationalVersionAttribute informational &&
                    !string.IsNullOrEmpty(informational.InformationalVersion))
                    return informational.InformationalVersion;

                var version = assembly.GetName().Version;
                return version == null ? Unknown : version.ToString();
            }
            catch (Exception e)
            {
                LogDebug("Failed to read an assembly version", e);
                return Unknown;
            }
        }

        /// <summary>
        ///     Reporting a contained failure must not itself be able to escape: the logger may
        ///     not exist yet, and an exception is free to throw from its own ToString().
        /// </summary>
        private static void LogDebug(string message, Exception e)
        {
            try
            {
                Automatics.Logger?.Debug($"{message}\n{e}");
            }
            catch
            {
                // Nothing left to do. Losing a debug line is preferable to losing the
                // containment that produced it.
            }
        }
    }
}
