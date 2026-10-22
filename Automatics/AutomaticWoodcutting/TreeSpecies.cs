using System;
using System.Collections.Generic;
using Automatics.Valheim;

namespace Automatics.AutomaticWoodcutting
{
    /// <summary>
    ///     The species of a tree, log or stump. An object matched by its own prefab name has that
    ///     species. Any other object takes the species of every standing tree whose prefabs lead to
    ///     it: the tree's log and stump, and each log's smaller log in turn. That graph is read from
    ///     the prefabs of the current ZNetScene the first time it is needed, and read again for a
    ///     new ZNetScene; it is never written to. The species of a prefab name is matched against
    ///     the tree data once, and again after a new ZNetScene or a change of the custom trees.
    /// </summary>
    internal static class TreeSpecies
    {
        private static readonly Dictionary<string, List<string>> RootsByPrefab =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // The species identifiers of each prefab name asked about, by the rule in the summary.
        private static readonly Dictionary<string, string[]> SpeciesByPrefab =
            new Dictionary<string, string[]>(StringComparer.Ordinal);

        private static readonly string[] NoSpecies = new string[0];

        private static readonly HashSet<int> StubPrefabs = new HashSet<int>();
        private static ZNetScene _scene;

        /// <summary>
        ///     Whether Allow Woodcutting Tree allows the object with this prefab name. An object of
        ///     no species is not allowed, and one reached from trees of several species is allowed
        ///     while any of them is.
        /// </summary>
        internal static bool IsAllowed(string prefabName)
        {
            var species = ResolveSpecies(prefabName);
            if (species.Length == 0) return false;

            var allowed = Config.AllowWoodcuttingTree;
            for (var i = 0; i < species.Length; i++)
                if (allowed.Contains(species[i]))
                    return true;

            return false;
        }

        /// <summary>
        ///     Adds the species identifiers of the object with this prefab name: its own, or else
        ///     those of the standing trees that lead to it. Adds none for an object of no species.
        /// </summary>
        internal static void GetSpecies(string prefabName, ISet<string> identifiers)
        {
            var species = ResolveSpecies(prefabName);
            for (var i = 0; i < species.Length; i++)
                identifiers.Add(species[i]);
        }

        /// <summary>
        ///     Whether the object with this prefab name has any of these species identifiers, by the
        ///     same rule as <see cref="GetSpecies" />.
        /// </summary>
        internal static bool HasAnySpecies(string prefabName, ISet<string> identifiers)
        {
            var species = ResolveSpecies(prefabName);
            for (var i = 0; i < species.Length; i++)
                if (identifiers.Contains(species[i]))
                    return true;

            return false;
        }

        /// <summary>
        ///     Drops the species resolved so far, so the next question matches the tree data again.
        /// </summary>
        internal static void ClearSpecies()
        {
            SpeciesByPrefab.Clear();
        }

        /// <summary>
        ///     Subscribed to <see cref="ValheimObject.RegistryChanged" /> through the module's scope
        ///     while it runs. A change of the tree data, which only the custom trees can make,
        ///     changes the species the tree data names.
        /// </summary>
        internal static void OnRegistryChanged(ValheimObject registry)
        {
            if (ReferenceEquals(registry, Globals.Tree)) ClearSpecies();
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

        // The species identifiers of the object with this prefab name: its own when the tree data
        // names the prefab, else those of the standing trees that lead to it, else none.
        private static string[] ResolveSpecies(string prefabName)
        {
            Refresh();
            if (prefabName == null) return NoSpecies;
            if (SpeciesByPrefab.TryGetValue(prefabName, out var species)) return species;

            if (Globals.Tree.GetIdentify(prefabName, out var identifier))
            {
                species = new[] { identifier };
            }
            else if (RootsByPrefab.TryGetValue(prefabName, out var roots))
            {
                var identifiers = new List<string>();
                foreach (var root in roots)
                    if (Globals.Tree.GetIdentify(root, out identifier) && !identifiers.Contains(identifier))
                        identifiers.Add(identifier);
                species = identifiers.ToArray();
            }
            else
            {
                species = NoSpecies;
            }

            SpeciesByPrefab.Add(prefabName, species);
            return species;
        }

        private static void Refresh()
        {
            var scene = ZNetScene.instance;
            if (ReferenceEquals(scene, _scene)) return;

            _scene = scene;
            SpeciesByPrefab.Clear();
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
