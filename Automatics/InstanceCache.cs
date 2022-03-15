using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics
{
    public class InstanceCache<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static readonly HashSet<T> Cache;

        private static Action<T> _onAwake;
        private static Action<T> _onDestroy;

        static InstanceCache()
        {
            Cache = new HashSet<T>();
        }

        public static IEnumerable<T> GetAllInstance() => Cache.ToList();

        public static void AddAwakeListener(Action<T> onAwake)
        {
            _onAwake += onAwake;
        }

        public static void RemoveAwakeListener(Action<T> onAwake)
        {
            _onAwake -= onAwake;
        }

        public static void AddDestroyListener(Action<T> onDestroy)
        {
            _onDestroy += onDestroy;
        }

        public static void RemoveDestroyListener(Action<T> onDestroy)
        {
            _onDestroy -= onDestroy;
        }

        private T _instance;

        private void Awake()
        {
            _instance = GetComponent<T>();
            Cache.Add(_instance);
            _onAwake?.Invoke(_instance);
        }

        private void OnDestroy()
        {
            _onDestroy?.Invoke(_instance);
            Cache.Remove(_instance);
            _instance = null;
        }
    }
}