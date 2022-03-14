using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    [DisallowMultipleComponent]
    internal class FloraObject : MonoBehaviour
    {
        private static readonly List<FloraObject> FloraObjects;

        static FloraObject()
        {
            FloraObjects = new List<FloraObject>();
        }

        private Pickable _pickable;
        private ZNetView _zNetView;

        public FloraCluster Cluster { get; private set; }

        public static FloraObject Find(Predicate<Pickable> predicate)
        {
            return FloraObjects.FirstOrDefault(x => predicate(x._pickable));
        }

        private void Awake()
        {
            FloraObjects.Add(this);

            _pickable = GetComponent<Pickable>();
            _zNetView = GetComponent<ZNetView>();

            var destructible = _pickable.GetComponent<Destructible>();
            if (destructible != null)
            {
                destructible.m_onDestroyed += () => { Cluster?.Leave(this); };
            }

            Invoke(nameof(ClusterConstruction), UnityEngine.Random.Range(1f, 2f));
        }

        private void OnDestroy()
        {
            FloraObjects.Remove(this);

            _pickable = null;
            _zNetView = null;

            Cluster?.Leave(this);
            Cluster = null;
        }

        private void ClusterConstruction()
        {
            var origin = _pickable.transform.position;
            var objectName = Utility.GetName(_pickable);

            foreach (var flora in
                     from x in FloraObjects
                     where Vector3.Distance(x.transform.position, origin) <= Config.FloraPinMergeRange &&
                           Utility.GetName(x._pickable) == objectName
                     select x)
            {
                if (Cluster == flora.Cluster) continue;

                if (Cluster == null)
                {
                    Cluster = flora.Cluster;
                    Cluster.Enter(this);
                }
                else if (flora.Cluster == null)
                {
                    flora.Cluster = Cluster;
                    flora.Cluster.Enter(flora);
                }
                else if (Cluster.Size >= flora.Cluster.Size)
                {
                    foreach (var member in flora.Cluster.Members.ToList())
                    {
                        member.Cluster.Leave(flora);
                        member.Cluster = Cluster;
                        member.Cluster.Enter(flora);
                    }
                }
                else
                {
                    foreach (var member in Cluster.Members.ToList())
                    {
                        member.Cluster.Leave(this);
                        member.Cluster = flora.Cluster;
                        member.Cluster.Enter(this);
                    }
                }
            }

            if (Cluster != null) return;

            Cluster = new FloraCluster();
            Cluster.Enter(this);
        }

        public bool IsValid()
        {
            return _pickable != null && _zNetView != null && _zNetView.GetZDO() != null;
        }

        public ZDOID ZdoId => _zNetView.GetZDO().m_uid;
    }

    internal class FloraCluster
    {
        private readonly HashSet<FloraObject> _members;

        public IEnumerable<FloraObject> Members => _members;

        public int Size => _members.Count;

        public Vector3 Center { get; private set; }

        public bool IsDirty { get; private set; }

        public FloraCluster()
        {
            _members = new HashSet<FloraObject>();
            Center = Vector3.zero;
        }

        private void MarkDirty()
        {
            IsDirty = true;
        }

        public void Enter(FloraObject floraObject)
        {
            if (_members.Add(floraObject))
                MarkDirty();
        }

        public void Leave(FloraObject floraObject)
        {
            if (_members.Remove(floraObject))
                MarkDirty();
        }

        public void Update()
        {
            if (!IsDirty) return;

            var count = 0;
            var center = Vector3.zero;
            foreach (var member in _members.Where(member => member.IsValid()))
            {
                center += member.transform.position;
                count++;
            }

            Center = center / count;

            IsDirty = false;
        }
    }
}