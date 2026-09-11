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
        private static readonly Dictionary<ZDOID, FloraNode> NodeIndex =
            new Dictionary<ZDOID, FloraNode>();

        private Pickable _pickable;
        private Destructible _destructible;
        private ZNetView _zNetView;
        private ZDOID _uniqueId;

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
        // not join a network in the part of the frame it has left.
        public void OnScopeDetaching()
        {
            CancelInvoke(nameof(NetworkConstruction));
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

            // Direct iteration with the cheapest rejections first (name before the
            // squared distance), instead of a per-call LINQ Where().ToList() over
            // every flora node in the world (O(N^2) plus a List allocation per node
            // during chunk streaming). Safe: the merge body only mutates per-network
            // membership, never the static ObjectNodes set being iterated.
            var thisPos = Position;
            var thisName = Name;
            var mergeRangeSqr = (float)Config.FloraPinMergeRange * Config.FloraPinMergeRange;
            foreach (var node in ObjectNodes)
            {
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
