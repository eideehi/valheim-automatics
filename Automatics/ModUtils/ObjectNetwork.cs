using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.ModUtils
{
    public abstract class ObjectNode<TNode, TNetwork> : MonoBehaviour
        where TNode : ObjectNode<TNode, TNetwork>
        where TNetwork : ObjectNetwork<TNode, TNetwork>
    {
        protected static readonly HashSet<TNode> ObjectNodes;

        static ObjectNode()
        {
            ObjectNodes = new HashSet<TNode>();
        }

        private TNode This => (TNode)this;

        public TNetwork Network { get; set; }

        protected abstract TNetwork CreateNetwork();

        protected abstract bool IsConnectable(TNode other);

        protected virtual void Awake()
        {
            ObjectNodes.Add(This);

            Invoke(nameof(NetworkConstruction), UnityEngine.Random.Range(1f, 2f));
        }

        protected virtual void OnDestroy()
        {
            ObjectNodes.Remove(This);

            Network?.RemoveNode(This);
            Network = null;
        }

        private void NetworkConstruction()
        {
            foreach (var node in ObjectNodes.Where(IsConnectable))
            {
                if (Network == node.Network) continue;

                if (Network == null)
                {
                    Network = node.Network;
                    Network.AddNode(This);
                }
                else if (node.Network == null)
                {
                    node.Network = Network;
                    node.Network.AddNode(node);
                }
                else
                {
                    var src = Network.NodeCount >= node.Network.NodeCount ? node.Network : Network;
                    var dest = Network.NodeCount >= node.Network.NodeCount ? Network : node.Network;
                    foreach (var x in src.GetAllNodes())
                    {
                        x.Network.RemoveNode(x);
                        x.Network = dest;
                        x.Network.AddNode(x);
                    }
                }
            }

            if (Network != null) return;

            Network = CreateNetwork();
            Network.AddNode(This);
        }
    }

    public class ObjectNetwork<TNode, TNetwork>
        where TNode : ObjectNode<TNode, TNetwork>
        where TNetwork : ObjectNetwork<TNode, TNetwork>
    {
        private readonly HashSet<TNode> _nodes;

        protected ObjectNetwork()
        {
            _nodes = new HashSet<TNode>();
        }

        protected Action<IEnumerable<TNode>> OnNodeChanged { get; set; }

        public bool IsDirty { get; private set; }
        public int NodeCount => _nodes.Count;

        public IEnumerable<TNode> GetAllNodes() => _nodes.ToList();

        public void AddNode(TNode node)
        {
            if (_nodes.Add(node))
                IsDirty = true;
        }

        public void RemoveNode(TNode node)
        {
            if (_nodes.Remove(node))
                IsDirty = true;
        }

        public void Update()
        {
            if (!IsDirty) return;

            OnNodeChanged?.Invoke(GetAllNodes());
            IsDirty = false;
        }
    }
}