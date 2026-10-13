using System;
using System.Collections.Generic;

namespace Automatics.AutomaticWoodcutting
{
    /// <summary>
    ///     The species of a tree, log or stump. An object matched by its own prefab name has that
    ///     species. Any other object takes the species of every standing tree whose prefabs lead to
    ///     it: the tree's log and stump, and each log's smaller log in turn. That graph is read from
    ///     the prefabs of the current ZNetScene the first time it is needed, and read again for a
    ///     new ZNetScene; it is never written to.
    /// </summary>
    internal static class TreeSpecies
    {
        private static readonly Dictionary<string, List<string>> RootsByPrefab =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        private static readonly HashSet<int> StubPrefabs = new HashSet<int>();
        private static ZNetScene _scene;

        /// <summary>
        ///     Whether Allow Woodcutting Tree allows the object with this prefab name. An object of
        ///     no species is not allowed, and one reached from trees of several species is allowed
        ///     while any of them is.
        /// </summary>
        internal static bool IsAllowed(string prefabName)
        {
            if (Globals.Tree.GetIdentify(prefabName, out var identifier))
                return Config.AllowWoodcuttingTree.Contains(identifier);

            if (!TryGetRoots(prefabName, out var roots)) return false;
            foreach (var root in roots)
                if (Globals.Tree.GetIdentify(root, out identifier) &&
                    Config.AllowWoodcuttingTree.Contains(identifier))
                    return true;

            return false;
        }

        /// <summary>
        ///     Adds the species identifiers of the object with this prefab name: its own, or else
        ///     those of the standing trees that lead to it. Adds none for an object of no species.
        /// </summary>
        internal static void GetSpecies(string prefabName, ISet<string> identifiers)
        {
            if (Globals.Tree.GetIdentify(prefabName, out var identifier))
            {
                identifiers.Add(identifier);
                return;
            }

            if (!TryGetRoots(prefabName, out var roots)) return;
            foreach (var root in roots)
                if (Globals.Tree.GetIdentify(root, out identifier))
                    identifiers.Add(identifier);
        }

        /// <summary>
        ///     The prefab names of the standing trees whose log, stump or smaller log this prefab is.
        /// </summary>
        internal static bool TryGetRoots(string prefabName, out IReadOnlyList<string> roots)
        {
            Refresh();
            if (prefabName != null && RootsByPrefab.TryGetValue(prefabName, out var list))
            {
                roots = list;
                return true;
            }

            roots = null;
            return false;
        }

        /// <summary>
        ///     Whether the prefab with this hash is the stump of a standing tree.
        /// </summary>
        internal static bool IsStub(int prefabHash)
        {
            Refresh();
            return StubPrefabs.Contains(prefabHash);
        }

        private static void Refresh()
        {
            var scene = ZNetScene.instance;
            if (ReferenceEquals(scene, _scene)) return;

            _scene = scene;
            RootsByPrefab.Clear();
            StubPrefabs.Clear();
            if (!scene) return;

            foreach (var prefab in scene.m_prefabs)
            {
                var tree = prefab ? prefab.GetComponent<TreeBase>() : null;
                if (!tree) continue;

                var root = prefab.name;
                if (tree.m_stubPrefab)
                {
                    AddRoot(tree.m_stubPrefab.name, root);
                    StubPrefabs.Add(tree.m_stubPrefab.name.GetStableHashCode());
                }

                // A log already reached from this tree is not followed again, so a loop in the
                // prefabs ends.
                var log = tree.m_logPrefab;
                while (log && AddRoot(log.name, root))
                {
                    var treeLog = log.GetComponent<TreeLog>();
                    log = treeLog ? treeLog.m_subLogPrefab : null;
                }
            }
        }

        // Returns false when the prefab already has this root.
        private static bool AddRoot(string prefabName, string root)
        {
            if (!RootsByPrefab.TryGetValue(prefabName, out var roots))
            {
                roots = new List<string>();
                RootsByPrefab.Add(prefabName, roots);
            }

            if (roots.Contains(root)) return false;
            roots.Add(root);
            return true;
        }
    }
}
