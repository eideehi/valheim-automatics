using Automatics.Valheim;

namespace Automatics
{
    /// <summary>
    ///     The request registry on a clock the cases set: when a container another game owns is
    ///     asked for, and which open replies are kept from the player. Keys stand for containers
    ///     and owner revisions for owners; the game side (the gate and the hooks) is not run here.
    /// </summary>
    internal static partial class Program
    {
        private const float Window = ContainerRequests<int>.OwnerStableWindow;
        private const float Timeout = ContainerRequests<int>.Timeout;
        private const float Cooldown = ContainerRequests<int>.Cooldown;
        private const float Moment = 0.01f;

        private static void RunRequestCases()
        {
            Run("requests: a container another game owns is asked for once while the request is open", AskedOnceWhileOpen);
            Run("requests: a granted request waits for the ownership it brings without asking again", GrantWaitsForOwnership);
            Run("requests: a refused request ends, and the container is asked again after the cooldown", RefusalEndsRequest);
            Run("requests: the cooldown after a refusal outlasts the request's timeout", CooldownAfterRefusal);
            Run("requests: an unanswered request times out and the container is asked again", UnansweredRequestTimesOut);
            Run("requests: the player's own open gets its reply and holds requests back", PlayerOpenCancelsRequest);
            Run("requests: granted and refused replies to a request are kept from the player", RepliesAreKept);
            Run("requests: a reply that arrives after its request timed out is kept from the player", LateReplyIsKept);
            Run("requests: a reply for a container nobody asked for reaches the player", OtherRepliesPass);
            Run("requests: a container whose owner just changed is not asked for within the window", WindowAfterOwnerChange);
            Run("requests: a container this game just handed to another is not asked for within the window", WindowAfterHandOver);
            Run("requests: a container seen for the first time is asked for at once", FirstSightAsksAtOnce);
            Run("requests: unloading the world forgets every request and window", ClearForgetsEverything);
        }

        private sealed class Clock
        {
            public float Now;
        }

        private static ContainerRequests<int> NewRequests(Clock clock)
        {
            return new ContainerRequests<int>(() => clock.Now);
        }

        private static void AskedOnceWhileOpen()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            AssertTrue(requests.TryRequest(1, 7), "The first need sends a request.");

            clock.Now = 100.5f;
            AssertFalse(requests.TryRequest(1, 7), "No second request while the first is open.");
            clock.Now = 100f + Timeout - Moment;
            AssertFalse(requests.TryRequest(1, 7), "No second request until the first times out.");
            AssertTrue(requests.TryRequest(2, 7), "Another container is asked for on its own.");
        }

        private static void GrantWaitsForOwnership()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);

            clock.Now = 100.2f;
            AssertTrue(requests.TakeReply(1, true), "The grant answers the request.");
            clock.Now = 100f + Timeout - Moment;
            AssertFalse(requests.TryRequest(1, 7),
                "The ownership the grant brings is on its way; the owner is not asked again.");

            // Once the ownership arrives the container is this game's, and the gate writes it
            // without asking. Later it shows a new owner, and the window applies from then.
            clock.Now = 110f;
            AssertFalse(requests.TryRequest(1, 9), "A new owner is not asked at once.");
            clock.Now = 110f + Window;
            AssertTrue(requests.TryRequest(1, 9), "The new owner is asked once it held the container a while.");
        }

        private static void RefusalEndsRequest()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);

            clock.Now = 100.2f;
            AssertTrue(requests.TakeReply(1, false), "The refusal answers the request.");
            clock.Now = 100.2f + Cooldown;
            AssertTrue(requests.TryRequest(1, 7), "The container is asked again after the cooldown.");
        }

        private static void CooldownAfterRefusal()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);

            clock.Now = 101f;
            requests.TakeReply(1, false);
            clock.Now = 100f + Timeout;
            AssertFalse(requests.TryRequest(1, 7), "The cooldown runs from the refusal, not from the request.");
            clock.Now = 101f + Cooldown - Moment;
            AssertFalse(requests.TryRequest(1, 7), "Not asked again within the cooldown.");
            clock.Now = 101f + Cooldown;
            AssertTrue(requests.TryRequest(1, 7), "Asked again once the cooldown passed.");
        }

        private static void UnansweredRequestTimesOut()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);

            clock.Now = 100f + Timeout - Moment;
            AssertFalse(requests.TryRequest(1, 7), "The request is still open.");
            clock.Now = 100f + Timeout;
            AssertTrue(requests.TryRequest(1, 7), "An unanswered request times out and is sent again.");
        }

        private static void PlayerOpenCancelsRequest()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);

            clock.Now = 100.5f;
            requests.Forget(1);
            clock.Now = 100.6f;
            AssertFalse(requests.TryRequest(1, 7),
                "Nothing is asked while the player's own open waits for its reply.");
            AssertFalse(requests.TakeReply(1, true), "The reply after the player's own open is the player's.");

            requests.Forget(2);
            AssertFalse(requests.TryRequest(2, 3),
                "A container the player opens is not asked for while that open waits, even if never asked before.");
            AssertFalse(requests.TakeReply(2, false), "The player sees the refusal of their own open.");

            clock.Now = 100.5f + Timeout;
            AssertTrue(requests.TryRequest(1, 7), "Once the player's open is over the container can be asked for.");
        }

        private static void RepliesAreKept()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);
            requests.TryRequest(2, 7);

            clock.Now = 100.2f;
            AssertTrue(requests.TakeReply(1, true), "A granted reply opens no window.");
            AssertTrue(requests.TakeReply(2, false), "A refused reply shows no in-use message.");
            AssertTrue(requests.TakeReply(2, false), "Every refusal of an asked container is kept.");
            AssertFalse(requests.TakeReply(1, true), "After the grant, the next reply is the player's again.");
        }

        private static void LateReplyIsKept()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);
            requests.TryRequest(2, 7);

            clock.Now = 100f + Timeout + 5f;
            AssertTrue(requests.TakeReply(1, true), "A grant after the timeout is still kept.");

            AssertTrue(requests.TryRequest(2, 7), "The timed-out request is sent again.");
            AssertTrue(requests.TakeReply(2, false), "The first request's late refusal is kept.");
            AssertTrue(requests.TakeReply(2, true), "The second request's grant is kept.");
        }

        private static void OtherRepliesPass()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            AssertFalse(requests.TakeReply(9, true), "A grant nobody asked for here is the player's.");
            AssertFalse(requests.TakeReply(9, false), "A refusal nobody asked for here is the player's.");

            requests.HandedOver(8, 4);
            AssertFalse(requests.TakeReply(8, true), "Handing a container over asks for nothing.");
        }

        private static void WindowAfterOwnerChange()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);
            clock.Now = 100.2f;
            requests.TakeReply(1, false);

            clock.Now = 110f;
            AssertFalse(requests.TryRequest(1, 8), "The owner changed just now.");
            clock.Now = 110f + Window - Moment;
            AssertFalse(requests.TryRequest(1, 8), "Still within the window.");
            clock.Now = 110f + Window;
            AssertTrue(requests.TryRequest(1, 8), "Asked once the new owner held it for the window.");
        }

        private static void WindowAfterHandOver()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.HandedOver(1, 4);
            clock.Now = 100f + Window - Moment;
            AssertFalse(requests.TryRequest(1, 4), "This game handed it over just now.");
            clock.Now = 100f + Window;
            AssertTrue(requests.TryRequest(1, 4), "Asked once the window passed.");

            // A container this game got by asking, then handed on to another game.
            requests.TryRequest(2, 7);
            requests.TakeReply(2, true);
            clock.Now = 110f;
            requests.HandedOver(2, 9);
            clock.Now = 110f + Window - Moment;
            AssertFalse(requests.TryRequest(2, 9), "Not asked back within the window.");
            clock.Now = 110f + Window;
            AssertTrue(requests.TryRequest(2, 9), "Asked back once the window passed.");
        }

        private static void FirstSightAsksAtOnce()
        {
            var clock = new Clock { Now = 0f };
            var requests = NewRequests(clock);
            AssertTrue(requests.TryRequest(1, 3), "No owner change was seen, so nothing holds the request back.");
        }

        private static void ClearForgetsEverything()
        {
            var clock = new Clock { Now = 100f };
            var requests = NewRequests(clock);
            requests.TryRequest(1, 7);
            requests.HandedOver(2, 5);
            requests.Forget(3);

            requests.Clear();
            clock.Now = 100.1f;
            AssertFalse(requests.TakeReply(1, true), "No request is remembered.");
            AssertTrue(requests.TryRequest(1, 7), "No request is open.");
            AssertTrue(requests.TryRequest(2, 5), "No window is running.");
            AssertTrue(requests.TryRequest(3, 1), "No player's open is waiting.");
        }
    }
}
