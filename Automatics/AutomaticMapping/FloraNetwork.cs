using System;
using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    [DisallowMultipleComponent]
    internal class FloraNode : ObjectNode<FloraNode, FloraNetwork>, IScopedComponent
    {
        // The largest flora_pins_merge_range the setting accepts (see Config).
        private const float MaxMergeRange = 16f;

        // The cells the nodes are looked up by are a metre wider than that range, so two plants
        // the merge test accepts, float rounding included, stand in the same cell or in
        // neighbouring ones.
        private const float CellSize = MaxMergeRange + 1f;

        private static readonly Dictionary<ZDOID, FloraNode> NodeIndex =
            new Dictionary<ZDOID, FloraNode>();

        // The live nodes by name and cell, so that a node looks for its patch among the nodes of
        // its own name in the cells around it instead of among every flora node in the world.
        private static readonly Dictionary<(string Name, int X, int Z), List<FloraNode>> NodesByCell =
            new Dictionary<(string Name, int X, int Z), List<FloraNode>>();

        private Pickable _pickable;
        private Destructible _destructible;
        private ZNetView _zNetView;
        private ZDOID _uniqueId;
        private (string Name, int X, int Z) _cell;
        private bool _inCell;

        public int ScopeGeneration { get; set; }

        private string Name => _pickable ? Objects.GetName(_pickable) : string.Empty;

        public Vector3 Position => _pickable ? _pickable.transform.position : Vector3.zero;
        public ZDOID UniqueId => _uniqueId;

        protected override void Awake()
        {
            _pickable = GetComponent<Pickable>();
            _zNetView = GetComponent<ZNetView>();
            _uniqueId = _zNetView != null && _zNetView.GetZDO() != null
                ? _zNetView.GetZDO().m_uid
                : ZDOID.None;

            _destructible = _pickable.GetComponent<Destructible>();
            if (_destructible != null) _destructible.m_onDestroyed += OnDestroyed;

            if (_uniqueId != ZDOID.None)
                NodeIndex[_uniqueId] = this;
            ObjectNodes.Add(this);
            // Under the name and cell it has as it is set up, so a construction nearby can find it
            // before its own runs; that one enters it again under the ones it has then.
            EnterCell(Name, Position);
            Invoke(nameof(NetworkConstruction), UnityEngine.Random.Range(1f, 2f));
        }

        // Turning the module off destroys this node and leaves the pickable, so the callback Awake
        // added to the pickable's destructible is taken off again here; the node the next
        // activation attaches adds its own.
        protected override void OnDestroy()
        {
            if (_destructible != null) _destructible.m_onDestroyed -= OnDestroyed;
            _destructible = null;

            if (_uniqueId != ZDOID.None &&
                NodeIndex.TryGetValue(_uniqueId, out var node) &&
                ReferenceEquals(node, this))
                NodeIndex.Remove(_uniqueId);

            ObjectNodes.Remove(this);
            RemoveFromCell();
            Network?.RemoveNode(this);
            Network = null;

            _pickable = null;
            _zNetView = null;
            _uniqueId = ZDOID.None;
        }

        public static FloraNode Find(ZDOID uniqueId)
        {
            return uniqueId != ZDOID.None &&
                   NodeIndex.TryGetValue(uniqueId, out var node) &&
                   node.IsValid()
                ? node
                : null;
        }

        // Awake schedules the network construction, and a node the scope is about to destroy must
        // not join a network in the part of the frame it has left: neither by its own construction
        // nor by being found in the lookup by another node's.
        public void OnScopeDetaching()
        {
            CancelInvoke(nameof(NetworkConstruction));
            RemoveFromCell();
        }

        // Puts this node in the lookup under the name and cell it has now, out of any it was in.
        private void EnterCell(string name, Vector3 position)
        {
            var cell = (name, Mathf.FloorToInt(position.x / CellSize), Mathf.FloorToInt(position.z / CellSize));
            if (_inCell && _cell.Equals(cell)) return;

            RemoveFromCell();
            _cell = cell;
            if (!NodesByCell.TryGetValue(cell, out var nodes))
            {
                nodes = new List<FloraNode>();
                NodesByCell.Add(cell, nodes);
            }

            nodes.Add(this);
            _inCell = true;
        }

        // Safe to call twice: the scope detaches a node before it destroys it.
        private void RemoveFromCell()
        {
            if (!_inCell) return;

            _inCell = false;
            if (!NodesByCell.TryGetValue(_cell, out var nodes)) return;

            for (var i = 0; i < nodes.Count; i++)
            {
                if (!ReferenceEquals(nodes[i], this)) continue;

                nodes.RemoveAt(i);
                if (nodes.Count == 0) NodesByCell.Remove(_cell);
                return;
            }
        }

        private void OnDestroyed()
        {
            Network?.RemoveNode(this);
        }

        protected override FloraNetwork CreateNetwork()
        {
            return new FloraNetwork();
        }

        private void NetworkConstruction()
        {
            if (!IsValid()) return;

            // The game can still move or rename a plant right after it appears, so the node enters
            // the lookup again under the name and cell it has now. Only the nodes under its name in
            // the 3x3 cells around that cell are visited, instead of every flora node in the world:
            // a node within the merge range is less than a cell away on each axis, so it is among
            // them once it has entered under its current name and position, at the latest by its
            // own construction. Each visited node meets the full test, its live name included.
            // Safe: the merge body only mutates per-network membership, never the lookup being
            // iterated.
            var thisPos = Position;
            var thisName = Name;
            EnterCell(thisName, thisPos);
            var mergeRangeSqr = (float)Config.FloraPinMergeRange * Config.FloraPinMergeRange;
            for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!NodesByCell.TryGetValue((thisName, _cell.X + dx, _cell.Z + dz), out var nodes))
                    continue;

                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (node == null || ReferenceEquals(node, this) || !node.IsValid()) continue;
                    if (Network != null && ReferenceEquals(Network, node.Network)) continue;
                    if (thisName != node.Name) continue;
                    if ((thisPos - node.Position).sqrMagnitude > mergeRangeSqr) continue;

                    if (Network == null && node.Network == null)
                    {
                        Network = CreateNetwork();
                        Network.AddNode(this);
                        node.Network = Network;
                        Network.AddNode(node);
                    }
                    else if (Network == null)
                    {
                        Network = node.Network;
                        Network.AddNode(this);
                    }
                    else if (node.Network == null)
                    {
                        node.Network = Network;
                        Network.AddNode(node);
                    }
                    else
                    {
                        var src = Network.NodeCount >= node.Network.NodeCount ? node.Network : Network;
                        var dest = Network.NodeCount >= node.Network.NodeCount ? Network : node.Network;
                        foreach (var member in src.GetAllNodes())
                        {
                            member.Network.RemoveNode(member);
                            member.Network = dest;
                            member.Network.AddNode(member);
                        }
                    }
                }
            }

            if (Network != null) return;

            Network = CreateNetwork();
            Network.AddNode(this);
        }

        protected override bool IsConnectable(FloraNode other)
        {
            return Vector3.Distance(Position, other.Position) <= Config.FloraPinMergeRange &&
                   Name == other.Name;
        }

        public bool IsValid()
        {
            return _pickable && _zNetView && _zNetView.GetZDO() != null;
        }
    }

    internal class FloraNetwork : ObjectNetwork<FloraNode, FloraNetwork>
    {
        public FloraNetwork()
        {
            Center = Vector3.zero;

            OnNodeChanged += nodes =>
            {
                var count = 0;
                var center = Vector3.zero;

                foreach (var node in nodes.Where(x => x.IsValid()))
                {
                    center += node.Position;
                    count++;
                }

                Center = count > 0 ? center / count : Vector3.zero;
            };
        }

        public Vector3 Center { get; private set; }

        public void FillValidNodes(List<FloraNode> buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            buffer.Clear();
            foreach (var node in EnumerateNodes())
                if (node != null && node.IsValid())
                    buffer.Add(node);
        }
    }
}
