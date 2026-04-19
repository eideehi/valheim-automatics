using System;
using System.Collections.Generic;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal readonly struct CellKey : IEquatable<CellKey>
    {
        public readonly int Cx;
        public readonly int Cz;

        public CellKey(int cx, int cz)
        {
            Cx = cx;
            Cz = cz;
        }

        public bool Equals(CellKey other)
        {
            return Cx == other.Cx && Cz == other.Cz;
        }

        public override bool Equals(object obj)
        {
            return obj is CellKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((Cx * 397) ^ Cz);
        }
    }

    /// <summary>
    /// Spatial hash of <see cref="Minimap.PinData"/> for cell-bounded
    /// queries. Pins whose position is mutated by vanilla
    /// <c>UpdateDynamicPins</c> outside of <see cref="Move"/> land in
    /// <see cref="Transients"/> instead of the cell index — the index
    /// cannot migrate cells for writes it never observes. See
    /// <see cref="IsTransientType"/> for the classification.
    /// </summary>
    internal static class PinIndex
    {
        public const float CellSize = 32f;

        private static readonly Dictionary<CellKey, List<Minimap.PinData>> Cells =
            new Dictionary<CellKey, List<Minimap.PinData>>();

        private static readonly Dictionary<Minimap.PinData, CellKey> PinCell =
            new Dictionary<Minimap.PinData, CellKey>();

        private static readonly List<Minimap.PinData> Transients =
            new List<Minimap.PinData>();

        public static IReadOnlyList<Minimap.PinData> TransientPins => Transients;

        public static bool IsTransientType(Minimap.PinType type)
        {
            switch (type)
            {
                case Minimap.PinType.Player:
                case Minimap.PinType.Ping:
                case Minimap.PinType.Shout:
                case Minimap.PinType.RandomEvent:
                case Minimap.PinType.EventArea:
                case Minimap.PinType.Bed:
                    return true;
                default:
                    return false;
            }
        }

        public static CellKey CellOf(Vector3 pos)
        {
            return new CellKey(
                Mathf.FloorToInt(pos.x / CellSize),
                Mathf.FloorToInt(pos.z / CellSize));
        }

        public static void Track(Minimap.PinData pin)
        {
            if (pin == null) return;

            if (IsTransientType(pin.m_type))
            {
                if (!Transients.Contains(pin)) Transients.Add(pin);
                return;
            }

            if (PinCell.ContainsKey(pin)) return;

            var cell = CellOf(pin.m_pos);
            if (!Cells.TryGetValue(cell, out var list))
            {
                list = new List<Minimap.PinData>();
                Cells[cell] = list;
            }
            list.Add(pin);
            PinCell[pin] = cell;
        }

        public static void Untrack(Minimap.PinData pin)
        {
            if (pin == null) return;

            if (PinCell.TryGetValue(pin, out var cell))
            {
                PinCell.Remove(pin);
                if (Cells.TryGetValue(cell, out var list))
                {
                    list.Remove(pin);
                    if (list.Count == 0) Cells.Remove(cell);
                }
            }

            Transients.Remove(pin);
        }

        public static bool Contains(Minimap.PinData pin)
        {
            if (pin == null) return false;
            return PinCell.ContainsKey(pin) || Transients.Contains(pin);
        }

        public static void Move(Minimap.PinData pin, Vector3 newPos)
        {
            if (pin == null) return;
            if (!PinCell.TryGetValue(pin, out var oldCell)) return;

            var newCell = CellOf(newPos);
            if (oldCell.Equals(newCell)) return;

            if (Cells.TryGetValue(oldCell, out var oldList))
            {
                oldList.Remove(pin);
                if (oldList.Count == 0) Cells.Remove(oldCell);
            }

            if (!Cells.TryGetValue(newCell, out var newList))
            {
                newList = new List<Minimap.PinData>();
                Cells[newCell] = newList;
            }
            newList.Add(pin);
            PinCell[pin] = newCell;
        }

        public static void Clear()
        {
            Cells.Clear();
            PinCell.Clear();
            Transients.Clear();
        }

        public static bool TryGetCell(CellKey key, out List<Minimap.PinData> pins)
        {
            return Cells.TryGetValue(key, out pins);
        }
    }
}
