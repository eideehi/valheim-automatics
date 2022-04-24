using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Automatics.ModUtils
{
    public static class Obj
    {
        private static readonly ConditionalWeakTable<Component, ZNetView> ZNetViewCache;
        private static readonly ConditionalWeakTable<Component, string> NameCache;

        static Obj()
        {
            ZNetViewCache = new ConditionalWeakTable<Component, ZNetView>();
            NameCache = new ConditionalWeakTable<Component, string>();
        }

        public static string GetPrefabName(GameObject gameObject) => Utils.GetPrefabName(gameObject);

        public static string GetName(Component component)
        {
            if (component == null) return "";

            if (NameCache.TryGetValue(component, out var name)) return name;

            name = AccessTools.GetDeclaredFields(component.GetType())
                .Where(x => x.Name == "m_name" && x.FieldType == typeof(string))
                .Select(x => x.GetValue(component) as string)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(name))
            {
                var hoverable = component.GetComponent<Hoverable>();
                if (hoverable != null)
                    name = hoverable is HoverText text ? text.m_text : hoverable.GetHoverName();
                else
                    name = Utils.GetPrefabName(component.gameObject);
            }

            NameCache.Add(component, name);
            return name;
        }

        public static bool GetZNetView(Component component, out ZNetView zNetView)
        {
            if (component == null)
            {
                zNetView = null;
                return false;
            }

            if (ZNetViewCache.TryGetValue(component, out zNetView)) return zNetView != null;

            zNetView = AccessTools.GetDeclaredFields(component.GetType())
                .Where(x => x.Name == "m_nview" && x.FieldType == typeof(ZNetView))
                .Select(x => x.GetValue(component) as ZNetView)
                .FirstOrDefault() ?? component.GetComponent<ZNetView>();

            ZNetViewCache.Add(component, zNetView);
            return zNetView != null;
        }

        public static bool GetZdoid(Component component, out ZDOID id)
        {
            var zdo = GetZNetView(component, out var zNetView) ? zNetView.GetZDO() : null;
            id = zdo?.m_uid ?? ZDOID.None;
            return id != ZDOID.None;
        }

        public static List<(Collider collider, T obj, float distance)> GetInsideSphere<T>(Vector3 origin, float radius,
            Func<Collider, T> convertor, Collider[] buffer, int layerMask = -1)
        {
            var result = new List<(Collider collider, T obj, float distance)>();

            var size = Physics.OverlapSphereNonAlloc(origin, radius, buffer, layerMask);
            for (var i = 0; i < size; i++)
            {
                var collider = buffer[i];
                var obj = convertor.Invoke(collider);
                if (obj != null)
                    result.Add((collider, obj, Vector3.Distance(origin, collider.transform.position)));
            }

            return result;
        }

        public static List<(Collider collider, T obj, float distance)> GetInsideSphere<T>(Vector3 origin, float radius,
            Func<Collider, T> convertor, int bufferSize = 128, int layerMask = -1)
        {
            return GetInsideSphere(origin, radius, convertor, new Collider[bufferSize], layerMask);
        }
    }
}