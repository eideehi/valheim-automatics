using System;
using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    [DisallowMultipleComponent]
    internal class FloraObject : ObjectNode<FloraObject, FloraCluster>
    {
        public static FloraObject Find(Predicate<Pickable> predicate)
        {
            return ObjectNodes.Where(x => x.IsValid()).FirstOrDefault(x => predicate(x._pickable));
        }

        private Pickable _pickable;
        private ZNetView _zNetView;

        private string Name => Obj.GetName(_pickable);

        public Vector3 Position => _pickable.transform.position;
        public ZDOID ZdoId => _zNetView.GetZDO().m_uid;

        private void OnDestroyed()
        {
            Network?.RemoveNode(this);
        }

        protected override void Awake()
        {
            _pickable = GetComponent<Pickable>();
            _zNetView = GetComponent<ZNetView>();

            var destructible = _pickable.GetComponent<Destructible>();
            if (destructible != null)
            {
                destructible.m_onDestroyed += OnDestroyed;
            }

            base.Awake();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            _pickable = null;
            _zNetView = null;
        }

        protected override FloraCluster CreateNetwork()
        {
            return new FloraCluster();
        }

        protected override bool IsConnectable(FloraObject other)
        {
            return Vector3.Distance(Position, other.Position) <= Config.FloraPinMergeRange && Name == other.Name;
        }

        public bool IsValid()
        {
            return _pickable != null && _zNetView != null && _zNetView.GetZDO() != null;
        }
    }

    internal class FloraCluster : ObjectNetwork<FloraObject, FloraCluster>
    {
        public Vector3 Center { get; private set; }

        public FloraCluster()
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
    }
}