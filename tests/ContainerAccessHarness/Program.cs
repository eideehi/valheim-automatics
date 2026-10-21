using System;
using System.Collections.Generic;
using Automatics.Valheim;

namespace Automatics
{
    /// <summary>
    ///     Offline checks for the rules that decide when a module may write a container, and for
    ///     the requests it sends to the game that owns one. The project compiles the product files
    ///     directly and references nothing but the base class library, so the decision, the claim
    ///     and load steps and the request timing (on a clock the cases set) are executed here
    ///     rather than read from the game code.
    ///     The named cases cover each rule; the exhaustive cases walk every combination of the
    ///     inputs and hold the decision to the whole rule set, so a rule that is dropped or turned
    ///     around fails both.
    /// </summary>
    internal static partial class Program
    {
        private static int _failures;

        private static int Main()
        {
            Run("single-player: an idle container the game owns is loaded, then written", OwnedContainerIsLoadedThenWritten);
            Run("a container open in this game is not written", OpenContainerIsNotWritten);
            Run("the owner goes by its own in-use flag", OwnerGoesByItsOwnFlag);
            Run("a container another game has open is not claimed", ContainerOpenElsewhereIsNotClaimed);
            Run("a game that does not own a container goes by the shared in-use flag", NonOwnerGoesBySharedFlag);
            Run("a container on a cart in use is not claimed or written", CartInUseBlocksEverything);
            Run("the server claims an idle container nobody owns", ServerClaimsUnownedContainer);
            Run("a client never claims a container nobody owns", ClientNeverClaims);
            Run("a container another game owns is asked for, never claimed or written", ForeignContainerIsAskedFor);
            Run("without a local player a container another game owns is not asked for", NoRequestWithoutPlayer);
            Run("a container another game has open, or on a cart in use, is not asked for", NoRequestWhileInUse);
            Run("a cart's or ship's container is never claimed", BorrowedViewIsNeverClaimed);
            Run("a cart's or ship's container another game owns is not asked for", BorrowedViewIsNeverAskedFor);
            Run("a cart's or ship's container the game owns is loaded, then written", OwnedBorrowedViewIsWritten);
            Run("a client without a local player writes nothing", ClientWithoutPlayerWritesNothing);
            Run("single-player without a local player writes its own idle container after loading it", SinglePlayerWithoutPlayerWrites);
            Run("the server without a local player writes and claims as with one", ServerWithoutPlayerWrites);
            Run("the server without a local player never writes or claims a container in use", ServerWithoutPlayerRespectsInUse);

            Run("every state: a write needs an owned, idle, loaded container and a player or the server", WriteNeedsEveryRule);
            Run("every state: a claim needs the server, nobody's idle container and its own view", ClaimNeedsEveryRule);
            Run("every state: a container the game may write is loaded before the write", LoadComesBeforeWrite);
            Run("every state: a request needs a local player and another game's idle container on its own view", RequestNeedsEveryRule);

            Run("the server claims an unowned container, loads it, then writes it", ClaimLoadWriteSequence);
            Run("a never-loaded container the game owns is loaded right before the write", LoadRightBeforeWrite);
            Run("a claim that does not take ends without a write", ClaimThatDoesNotTake);
            Run("a container found in use after the claim is neither loaded nor written", InUseAfterClaim);
            Run("a load that cannot run refuses the write", LoadThatCannotRun);
            Run("the gate stops at a container another game owns, reporting it can be asked for, without a claim or load", ForeignContainerIsNotTouched);
            Run("a client leaves a container nobody owns to the server", ClientLeavesUnownedContainer);

            RunRequestCases();

            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"ok: {name}");
            }
            catch (Exception e)
            {
                _failures++;
                Console.Error.WriteLine($"FAIL: {name}");
                Console.Error.WriteLine(e.Message);
            }
        }

        // --- States ------------------------------------------------------------------------

        // Single-player or a host, with the local player: the container is this game's.
        private static ContainerWriteState OwnedHere()
        {
            return new ContainerWriteState
            {
                Owned = true,
                HasOwner = true,
                IsServer = true,
                LocalPlayer = true
            };
        }

        private static ContainerWriteState Unowned()
        {
            return new ContainerWriteState { IsServer = true, LocalPlayer = true };
        }

        private static ContainerWriteState OwnedElsewhere()
        {
            return new ContainerWriteState { HasOwner = true, IsServer = true, LocalPlayer = true };
        }

        private static ContainerWriteState OnClient(ContainerWriteState state)
        {
            state.IsServer = false;
            return state;
        }

        // The server with nobody to act for: a dedicated server, or single-player or a host
        // while the player is dead, respawning or loading.
        private static ContainerWriteState OnServerWithoutPlayer(ContainerWriteState state)
        {
            state.IsServer = true;
            state.LocalPlayer = false;
            return state;
        }

        private static ContainerWriteState WithoutPlayer(ContainerWriteState state)
        {
            state.LocalPlayer = false;
            return state;
        }

        private static ContainerWriteState Loaded(ContainerWriteState state)
        {
            state.Loaded = true;
            return state;
        }

        // --- Decisions ---------------------------------------------------------------------

        private static void OwnedContainerIsLoadedThenWritten()
        {
            AssertStep(ContainerWriteStep.Load, OwnedHere(), "An owned container must be loaded first.");
            AssertStep(ContainerWriteStep.Write, Loaded(OwnedHere()), "A loaded owned container is written.");
        }

        private static void OpenContainerIsNotWritten()
        {
            var state = OwnedHere();
            state.InUseLocal = true;
            AssertStep(ContainerWriteStep.Skip, state, "An open container must not be loaded.");
            AssertStep(ContainerWriteStep.Skip, Loaded(state), "An open container must not be written.");
        }

        private static void OwnerGoesByItsOwnFlag()
        {
            var state = Loaded(OwnedHere());
            state.InUseReplicated = true;
            AssertStep(ContainerWriteStep.Write, state,
                "The owner keeps the in-use flag; a stale shared copy does not block it.");
        }

        private static void ContainerOpenElsewhereIsNotClaimed()
        {
            var unowned = Unowned();
            unowned.InUseReplicated = true;
            AssertStep(ContainerWriteStep.Skip, unowned, "The server must not claim a container that is open.");

            var foreign = OwnedElsewhere();
            foreign.InUseReplicated = true;
            AssertStep(ContainerWriteStep.Skip, foreign, "A container open in another game is skipped.");
        }

        private static void NonOwnerGoesBySharedFlag()
        {
            var state = Unowned();
            state.InUseLocal = true;
            AssertStep(ContainerWriteStep.Claim, state,
                "Without ownership the local flag is not the container's state; the shared one is.");
        }

        private static void CartInUseBlocksEverything()
        {
            foreach (var state in new[] { OwnedHere(), Loaded(OwnedHere()), Unowned() })
            {
                var onCart = state;
                onCart.CartInUse = true;
                onCart.BorrowedView = true;
                AssertStep(ContainerWriteStep.Skip, onCart, "A cart in use blocks the container.");
            }
        }

        private static void ServerClaimsUnownedContainer()
        {
            AssertStep(ContainerWriteStep.Claim, Unowned(), "Single-player or a host claims a container nobody owns.");
            AssertStep(ContainerWriteStep.Claim, OnServerWithoutPlayer(Unowned()),
                "The server without a player claims a container nobody owns.");
        }

        private static void ClientNeverClaims()
        {
            AssertStep(ContainerWriteStep.Skip, OnClient(Unowned()), "A client waits for the server to assign the owner.");
            AssertStep(ContainerWriteStep.Skip, OnClient(Loaded(Unowned())), "Loading does not let a client claim.");
        }

        private static void ForeignContainerIsAskedFor()
        {
            AssertStep(ContainerWriteStep.Request, OwnedElsewhere(), "The server asks the owner for it.");
            AssertStep(ContainerWriteStep.Request, OnClient(OwnedElsewhere()), "A client asks the owner for it.");
            AssertStep(ContainerWriteStep.Request, Loaded(OwnedElsewhere()), "Loading does not make it writable.");
        }

        // A server without a player would hand the container straight back: the server gives a
        // container near a player's character to that player's game.
        private static void NoRequestWithoutPlayer()
        {
            AssertStep(ContainerWriteStep.Skip, OnServerWithoutPlayer(OwnedElsewhere()),
                "The server without a player leaves another game's container alone.");
            AssertStep(ContainerWriteStep.Skip, WithoutPlayer(OnClient(OwnedElsewhere())),
                "A client without a player leaves another game's container alone.");
        }

        private static void NoRequestWhileInUse()
        {
            var open = OwnedElsewhere();
            open.InUseReplicated = true;
            AssertStep(ContainerWriteStep.Skip, open, "A container open in another game is not asked for.");

            var cart = OwnedElsewhere();
            cart.CartInUse = true;
            AssertStep(ContainerWriteStep.Skip, cart, "A container on a cart in use is not asked for.");
        }

        private static void BorrowedViewIsNeverClaimed()
        {
            var state = Unowned();
            state.BorrowedView = true;
            AssertStep(ContainerWriteStep.Skip, state, "A cart's or ship's container is never claimed.");
            AssertStep(ContainerWriteStep.Skip, OnServerWithoutPlayer(state),
                "The server without a player never claims a cart's or ship's container either.");
        }

        private static void BorrowedViewIsNeverAskedFor()
        {
            var state = OwnedElsewhere();
            state.BorrowedView = true;
            AssertStep(ContainerWriteStep.Skip, state, "A cart's or ship's container is never asked for.");
            AssertStep(ContainerWriteStep.Skip, OnClient(state), "Not by a client either.");
        }

        private static void OwnedBorrowedViewIsWritten()
        {
            var state = OwnedHere();
            state.BorrowedView = true;
            AssertStep(ContainerWriteStep.Load, state, "An owned cart or ship container is loaded first.");
            AssertStep(ContainerWriteStep.Write, Loaded(state), "An owned cart or ship container is written.");
        }

        private static void ClientWithoutPlayerWritesNothing()
        {
            AssertStep(ContainerWriteStep.Skip, WithoutPlayer(OnClient(Loaded(OwnedHere()))),
                "A client without a player must not write.");
            AssertStep(ContainerWriteStep.Skip, WithoutPlayer(OnClient(OwnedHere())),
                "A client without a player must not load for a write.");
            AssertStep(ContainerWriteStep.Skip, WithoutPlayer(OnClient(Unowned())),
                "A client without a player must not claim.");
        }

        // Single-player while the player is dead, respawning or loading: stations keep storing.
        private static void SinglePlayerWithoutPlayerWrites()
        {
            AssertStep(ContainerWriteStep.Load, WithoutPlayer(OwnedHere()),
                "Single-player without a player loads its own container first.");
            AssertStep(ContainerWriteStep.Write, WithoutPlayer(Loaded(OwnedHere())),
                "Single-player without a player writes its own idle, loaded container.");
        }

        private static void ServerWithoutPlayerWrites()
        {
            AssertStep(ContainerWriteStep.Write, OnServerWithoutPlayer(Loaded(OwnedHere())),
                "The server writes its own idle container without a player.");
            AssertStep(ContainerWriteStep.Claim, OnServerWithoutPlayer(Unowned()),
                "The server claims a container nobody owns without a player.");
        }

        private static void ServerWithoutPlayerRespectsInUse()
        {
            var owned = OnServerWithoutPlayer(Loaded(OwnedHere()));
            owned.InUseLocal = true;
            AssertStep(ContainerWriteStep.Skip, owned, "The server must not write its open container.");

            var unowned = OnServerWithoutPlayer(Unowned());
            unowned.InUseReplicated = true;
            AssertStep(ContainerWriteStep.Skip, unowned, "The server must not claim an open container.");

            var cart = OnServerWithoutPlayer(Loaded(OwnedHere()));
            cart.CartInUse = true;
            AssertStep(ContainerWriteStep.Skip, cart, "The server must not write on a cart in use.");
        }

        // --- Every state -------------------------------------------------------------------

        private static void WriteNeedsEveryRule()
        {
            foreach (var state in AllStates())
            {
                var expected = (state.LocalPlayer || state.IsServer) && state.Owned &&
                               !state.InUseLocal && !state.CartInUse && state.Loaded;
                AssertTrue((ContainerWritePolicy.Decide(state) == ContainerWriteStep.Write) == expected,
                    $"Write expected {expected} for {Describe(state)}.");
            }
        }

        private static void ClaimNeedsEveryRule()
        {
            foreach (var state in AllStates())
            {
                var expected = !state.Owned && !state.HasOwner && state.IsServer &&
                               !state.BorrowedView && !state.InUseReplicated && !state.CartInUse;
                AssertTrue((ContainerWritePolicy.Decide(state) == ContainerWriteStep.Claim) == expected,
                    $"Claim expected {expected} for {Describe(state)}.");
            }
        }

        private static void LoadComesBeforeWrite()
        {
            foreach (var state in AllStates())
            {
                var expected = (state.LocalPlayer || state.IsServer) && state.Owned &&
                               !state.InUseLocal && !state.CartInUse && !state.Loaded;
                AssertTrue((ContainerWritePolicy.Decide(state) == ContainerWriteStep.Load) == expected,
                    $"Load expected {expected} for {Describe(state)}.");
            }
        }

        private static void RequestNeedsEveryRule()
        {
            foreach (var state in AllStates())
            {
                var expected = state.LocalPlayer && !state.Owned && state.HasOwner &&
                               !state.BorrowedView && !state.InUseReplicated && !state.CartInUse;
                AssertTrue((ContainerWritePolicy.Decide(state) == ContainerWriteStep.Request) == expected,
                    $"Request expected {expected} for {Describe(state)}.");
            }
        }

        private static IEnumerable<ContainerWriteState> AllStates()
        {
            for (var bits = 0; bits < 1 << 9; bits++)
                yield return new ContainerWriteState
                {
                    Owned = (bits & 1) != 0,
                    HasOwner = (bits & 2) != 0,
                    IsServer = (bits & 4) != 0,
                    LocalPlayer = (bits & 8) != 0,
                    InUseLocal = (bits & 16) != 0,
                    InUseReplicated = (bits & 32) != 0,
                    CartInUse = (bits & 64) != 0,
                    BorrowedView = (bits & 128) != 0,
                    Loaded = (bits & 256) != 0
                };
        }

        // --- Claim and load steps ----------------------------------------------------------

        private static void ClaimLoadWriteSequence()
        {
            var container = new FakeContainer(Unowned());
            AssertGate(ContainerWriteStep.Write, container, "The server should get to write.");
            AssertSequence(container.Calls, "read", "claim", "read", "load", "read");
        }

        private static void LoadRightBeforeWrite()
        {
            var container = new FakeContainer(OwnedHere());
            AssertGate(ContainerWriteStep.Write, container, "The owner should get to write.");
            AssertSequence(container.Calls, "read", "load", "read");
        }

        private static void ClaimThatDoesNotTake()
        {
            var container = new FakeContainer(Unowned()) { ClaimTakes = false };
            AssertGate(ContainerWriteStep.Skip, container, "Without ownership there is no write.");
            AssertSequence(container.Calls, "read", "claim", "read");
        }

        private static void InUseAfterClaim()
        {
            var container = new FakeContainer(Unowned()) { InUseAfterClaim = true };
            AssertGate(ContainerWriteStep.Skip, container, "A container in use is not written.");
            AssertSequence(container.Calls, "read", "claim", "read");
        }

        private static void LoadThatCannotRun()
        {
            var container = new FakeContainer(OwnedHere()) { LoadRuns = false };
            AssertGate(ContainerWriteStep.Skip, container, "Without a load there is no write.");
            AssertSequence(container.Calls, "read", "load");
        }

        private static void ForeignContainerIsNotTouched()
        {
            var container = new FakeContainer(OwnedElsewhere());
            AssertGate(ContainerWriteStep.Request, container,
                "Another game's container is not written; the gate stops where it would be asked for.");
            AssertSequence(container.Calls, "read");
        }

        private static void ClientLeavesUnownedContainer()
        {
            var container = new FakeContainer(OnClient(Unowned()));
            AssertGate(ContainerWriteStep.Skip, container, "A client does not write a container nobody owns.");
            AssertSequence(container.Calls, "read");
        }

        // A container as the gate sees it: the state a claim leaves, and every call made.
        private sealed class FakeContainer : IContainerWriteTarget
        {
            private ContainerWriteState _state;

            public FakeContainer(ContainerWriteState state)
            {
                _state = state;
            }

            public bool ClaimTakes { get; set; } = true;
            public bool InUseAfterClaim { get; set; }
            public bool LoadRuns { get; set; } = true;
            public List<string> Calls { get; } = new List<string>();

            public ContainerWriteState Read(bool loaded)
            {
                Calls.Add("read");
                var state = _state;
                state.Loaded = loaded;
                return state;
            }

            public void Claim()
            {
                Calls.Add("claim");
                if (ClaimTakes)
                {
                    _state.Owned = true;
                    _state.HasOwner = true;
                }

                if (InUseAfterClaim) _state.InUseLocal = true;
            }

            public bool Load()
            {
                Calls.Add("load");
                return LoadRuns;
            }
        }

        // --- Assertions --------------------------------------------------------------------

        private static string Describe(ContainerWriteState state)
        {
            return $"owned={state.Owned} hasOwner={state.HasOwner} server={state.IsServer} " +
                   $"player={state.LocalPlayer} inUseLocal={state.InUseLocal} " +
                   $"inUseShared={state.InUseReplicated} cartInUse={state.CartInUse} " +
                   $"borrowedView={state.BorrowedView} loaded={state.Loaded}";
        }

        private static void AssertStep(ContainerWriteStep expected, ContainerWriteState state, string message)
        {
            var actual = ContainerWritePolicy.Decide(state);
            if (actual != expected)
                throw new Exception($"{message} Expected {expected}, got {actual} for {Describe(state)}.");
        }

        private static void AssertGate(ContainerWriteStep expected, FakeContainer container, string message)
        {
            var actual = ContainerWritePolicy.TryAcquire(container);
            if (actual != expected) throw new Exception($"{message} Expected {expected}, got {actual}.");
        }

        private static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
        {
            if (actual.Count != expected.Length)
                throw new Exception(
                    $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");

            for (var i = 0; i < expected.Length; i++)
                if (actual[i] != expected[i])
                    throw new Exception(
                        $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertFalse(bool condition, string message)
        {
            if (condition) throw new Exception(message);
        }
    }
}
