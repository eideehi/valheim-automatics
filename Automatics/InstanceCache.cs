using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics
{
    public class InstanceCache<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static readonly HashSet<T> Cache;

        static InstanceCache()
        {
            Cache = new HashSet<T>();
        }

        public static IEnumerable<T> GetAllInstance() => Cache.ToList();

        private T _instance;

        private void Awake()
        {
            _instance = GetComponent<T>();
            Cache.Add(_instance);
        }

        private void OnDestroy()
        {
            Cache.Remove(_instance);
            _instance = null;
        }
    }

    [DisallowMultipleComponent]
    public sealed class ContainerCache : InstanceCache<Container>
    {
    }
}