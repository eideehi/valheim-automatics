using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Automatics
{
    public static class Utility
    {
        private static readonly char[] PrefabNameSeparator;
        private static readonly ConditionalWeakTable<MonoBehaviour, ZDO> ZdoCache;
        private static readonly ConditionalWeakTable<MonoBehaviour, string> NameCache;

        static Utility()
        {
            PrefabNameSeparator = " (".ToCharArray();
            ZdoCache = new ConditionalWeakTable<MonoBehaviour, ZDO>();
            NameCache = new ConditionalWeakTable<MonoBehaviour, string>();
        }

        public static string GetPrefabName(GameObject @object)
        {
            var name = @object.name;
            var index = name.IndexOfAny(PrefabNameSeparator);
            return index == -1 ? name : name.Remove(index);
        }

        public static string GetName(MonoBehaviour @object)
        {
            if (!@object) return "";

            if (NameCache.TryGetValue(@object, out var name)) return name;

            switch (@object)
            {
                case HoverText text:
                {
                    name = text.m_text;
                    break;
                }
                case Hoverable hoverable:
                {
                    name = hoverable.GetHoverName();
                    break;
                }
                default:
                {
                    var hoverable = @object.GetComponent<Hoverable>();
                    if (hoverable != null)
                    {
                        name = hoverable is HoverText text ? text.m_text : hoverable.GetHoverName();
                        break;
                    }

                    var zNetView = @object.GetComponent<ZNetView>();
                    name = zNetView ? zNetView.GetPrefabName() : GetPrefabName(@object.gameObject);
                    break;
                }
            }

            NameCache.Add(@object, name);
            return name;
        }

        public static bool GetZdoid(MonoBehaviour @object, out ZDOID id)
        {
            if (!ZdoCache.TryGetValue(@object, out var zdo))
            {
                var zNetView = @object.GetComponent<ZNetView>();
                zdo = zNetView ? zNetView.GetZDO() : null;
                ZdoCache.Add(@object, zdo);
            }

            id = zdo?.m_uid ?? ZDOID.None;
            return id != ZDOID.None;
        }

        public static List<(T, float)> GetObjectsInSphere<T>(Vector3 origin, float radius, Func<Collider, T> convertor,
            Collider[] buffer, int layerMask = -1)
        {
            var size = Physics.OverlapSphereNonAlloc(origin, radius, buffer, layerMask);
            var result = new List<(T, float)>(size);

            for (var i = 0; i < size; i++)
            {
                var collider = buffer[i];

                var @object = convertor.Invoke(collider);
                if (@object != null)
                    result.Add((@object, Vector3.Distance(origin, collider.transform.position)));
            }

            return result;
        }

        public static List<(T, float)> GetObjectsInSphere<T>(Vector3 origin, float radius, Func<Collider, T> convertor,
            int bufferSize = 128, int layerMask = -1)
        {
            return GetObjectsInSphere(origin, radius, convertor, new Collider[bufferSize], layerMask);
        }
    }
}