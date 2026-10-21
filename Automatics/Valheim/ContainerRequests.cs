using System;
using System.Collections.Generic;

namespace Automatics.Valheim
{
    /// <summary>
    ///     The requests this game sends for containers another game owns, one entry per container
    ///     (keyed by its ZDOID in the game). A request is the game's own open request
    ///     (<c>Container.RPC_RequestOpen</c>): the owner checks use, cart and access, hands the
    ///     container over and answers, and the answer to a request of Automatics is kept from the
    ///     player. The registry also remembers when it saw each container's owner change, so a
    ///     player who has just been given a container is not asked for it before they could mark
    ///     it in use. Nothing here runs between calls: the times below are read from the clock
    ///     when a container is next considered.
    /// </summary>
    /// <remarks>
    ///     The times come from how the game moves ownership. A game sends ZDO changes in rounds
    ///     that start once 0.05 s have passed and send to one peer per frame
    ///     (<c>ZDOMan.SendZDOToPeers2</c>): a change leaves a client within about 0.05 s, a server
    ///     with ten players at 30 frames a second within about 0.4 s. RPCs go out at once and the
    ///     server passes them on as they arrive (<c>ZRoutedRpc</c>). An owner that grants an open
    ///     answers at once and sends the container's ZDO with the new owner in its next round
    ///     (<c>Container.RPC_RequestOpen</c>), which the server passes on in its own round when the
    ///     old owner is a client; the answer arrives first. The player who opens the container
    ///     marks it in use on the first frame that both shows it and owns it
    ///     (<c>InventoryGui.UpdateContainer</c> calls <c>Container.SetInUse</c>, which writes the
    ///     flag to the ZDO), and the flag reaches other games in that game's next round and the
    ///     server's. So from a grant, the new owner has marked the container in use after two
    ///     rounds, two network hops and a frame (about 0.75 s on a full server at 100 ms a hop),
    ///     and other games see the flag after two more rounds and hops (about 1.5 s); from then on
    ///     the gate skips the container without asking, because it is in use.
    /// </remarks>
    internal sealed class ContainerRequests<TKey>
    {
        /// <summary>
        ///     Seconds a container's new owner keeps it before this game asks for it. A request
        ///     sooner could reach a player's game that has been given the container but not yet
        ///     marked it in use; it would hand it over, and that player would see an inventory
        ///     without the container. The window covers the time until the in-use flag is seen
        ///     here, with room to spare, and the request itself still needs two hops to arrive.
        ///     It starts when this game hands a container over or sees a new owner; the owner of a
        ///     container seen for the first time counts as settled, since nothing tells when it
        ///     came. Breaks when a connection holds ZDO changes back for more than about a second
        ///     (a round sends nothing to a peer while more than 10 KB wait for it,
        ///     <c>ZDOMan.SendZDOs</c>), or when another game's owner change was never seen here.
        /// </summary>
        public const float OwnerStableWindow = 2f;

        /// <summary>
        ///     Seconds a request waits for a refusal or for the ownership a grant brings, which
        ///     travels like the in-use flag above; then it counts as lost. Breaks when a grant takes
        ///     longer: the next request reaches the old owner, which ignores it.
        /// </summary>
        public const float Timeout = 2f;

        /// <summary>
        ///     Seconds after a refusal before asking again. An owner refuses a container it has
        ///     marked in use, and the flag arrives here within this time; then the gate does not
        ///     ask while it lasts. Breaks when an owner refuses for a reason not seen here (another
        ///     mod's rule): it is asked once per cooldown.
        /// </summary>
        public const float Cooldown = 2f;

        private const int UnseenOwner = -1;

        private readonly Func<float> _clock;
        private readonly Dictionary<TKey, Entry> _entries = new Dictionary<TKey, Entry>();

        public ContainerRequests(Func<float> clock)
        {
            _clock = clock;
        }

        /// <summary>Whether to ask the owner now; true records the request as sent.</summary>
        public bool TryRequest(TKey key, int ownerRevision)
        {
            var now = _clock();
            var entry = Get(key);
            if (entry.OwnerRevision != ownerRevision)
            {
                if (entry.OwnerRevision != UnseenOwner) OwnerChanged(entry, now);
                entry.OwnerRevision = ownerRevision;
            }

            if (now - entry.OwnerSince < OwnerStableWindow) return false;
            if (entry.Pending && now - entry.SentAt < Timeout) return false;
            if (now - entry.RefusedAt < Cooldown) return false;

            entry.Pending = true;
            entry.Asked = true;
            entry.SentAt = now;
            return true;
        }

        /// <summary>This game handed the container to another game.</summary>
        public void HandedOver(TKey key, int ownerRevision)
        {
            var entry = Get(key);
            OwnerChanged(entry, _clock());
            entry.OwnerRevision = ownerRevision;
        }

        /// <summary>
        ///     True when an open answer is to a request of Automatics, also a timed-out one, so
        ///     the player must not see it. After a grant the next answer is the player's again.
        /// </summary>
        public bool TakeReply(TKey key, bool granted)
        {
            if (!_entries.TryGetValue(key, out var entry)) return false;

            if (!granted)
            {
                entry.Pending = false;
                entry.RefusedAt = _clock();
            }

            var asked = entry.Asked;
            if (granted) entry.Asked = false;
            return asked;
        }

        /// <summary>
        ///     The player opens the container: the answers that follow are theirs, and nothing is
        ///     asked while that open waits, since its answer looks the same as one to Automatics.
        /// </summary>
        public void Forget(TKey key)
        {
            var entry = Get(key);
            entry.Asked = false;
            entry.Pending = true;
            entry.SentAt = _clock();
        }

        /// <summary>When the world unloads.</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        private Entry Get(TKey key)
        {
            if (_entries.TryGetValue(key, out var entry)) return entry;

            entry = new Entry();
            _entries.Add(key, entry);
            return entry;
        }

        // A request or refusal from the previous owner says nothing about the new one.
        private static void OwnerChanged(Entry entry, float now)
        {
            entry.OwnerSince = now;
            entry.Pending = false;
            entry.RefusedAt = float.NegativeInfinity;
        }

        private sealed class Entry
        {
            public int OwnerRevision = UnseenOwner;
            public float OwnerSince = float.NegativeInfinity;
            public float SentAt = float.NegativeInfinity;
            public float RefusedAt = float.NegativeInfinity;

            // A request (or the player's own open) waits for its outcome.
            public bool Pending;

            // Answers for the container go to Automatics, not to the player.
            public bool Asked;
        }
    }
}
