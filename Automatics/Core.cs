using System.IO;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics
{
    internal static class Core
    {
        public static void Initialize()
        {
            LanguageLoader.LoadFromCsv(Path.Combine(Automatics.ModLocation, "Languages"));
            Config.Initialize();
        }
    }

    [DisallowMultipleComponent]
    public sealed class ContainerCache : InstanceCache<Container>
    {
    }

    [DisallowMultipleComponent]
    public sealed class PickableCache : InstanceCache<Pickable>
    {
    }
}