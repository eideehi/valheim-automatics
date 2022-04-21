using System.IO;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics
{
    internal static class Core
    {
        [AutomaticsInitializer]
        private static void Initialize()
        {
            LoadTranslations();
            Config.Initialize();
            Automatics.OnInitTerminal += Command.Register;
        }

        private static void LoadTranslations()
        {
            var languagesDir = Path.Combine(Automatics.ModLocation, "Languages");
            Deprecated.LanguageLoader.LoadFromCsv(languagesDir);
            TranslationsLoader.LoadFromJson(languagesDir);
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