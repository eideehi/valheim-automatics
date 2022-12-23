using System;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    [DisallowMultipleComponent]
    internal class FloraNode : ObjectNode<FloraNode, FloraNetwork>
    {
        private Pickable _pickable;
        private ZNetView _zNetView;

        private string Name => Objects.GetName(_pickable);

        public Vector3 Position => _pickable.transform.position;
        public ZDOID UniqueId => _zNetView.GetZDO().m_uid;

        protected override void Awake()
        {
            _pickable = GetComponent<Pickable>();
            _zNetView = GetComponent<ZNetView>();

            var destructible = _pickable.GetComponent<Destructible>();
            if (destructible != null) destructible.m_onDestroyed += OnDestroyed;

            base.Awake();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            _pickable = null;
            _zNetView = null;
        }

        public static FloraNode Find(Predicate<Pickable> predicate)
        {
            return ObjectNodes.Where(x => x.IsValid()).FirstOrDefault(x => predicate(x._pickable));
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

                Center = center / count;
            };
        }

        public Vector3 Center { get; private set; }
    }
}