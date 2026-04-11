using System;
using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    [DisallowMultipleComponent]
    internal class FloraNode : ObjectNode<FloraNode, FloraNetwork>
    {
        private static readonly Dictionary<ZDOID, FloraNode> NodeIndex =
            new Dictionary<ZDOID, FloraNode>();

        private Pickable _pickable;
        private ZNetView _zNetView;
        private ZDOID _uniqueId;

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

            var destructible = _pickable.GetComponent<Destructible>();
            if (destructible != null) destructible.m_onDestroyed += OnDestroyed;

            if (_uniqueId != ZDOID.None)
                NodeIndex[_uniqueId] = this;
            base.Awake();
        }

        protected override void OnDestroy()
        {
            if (_uniqueId != ZDOID.None &&
                NodeIndex.TryGetValue(_uniqueId, out var node) &&
                ReferenceEquals(node, this))
                NodeIndex.Remove(_uniqueId);

            base.OnDestroy();

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

        private void OnDestroyed()
        {
            Network?.RemoveNode(this);
        }

        protected override FloraNetwork CreateNetwork()
        {
            return new FloraNetwork();
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
