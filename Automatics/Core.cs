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
            LoadTranslations(Automatics.GetDefaultResourcePath("Languages"));
            Config.Initialize();

            foreach (var automaticsChildModDir in Automatics.GetAutomaticsChildModDirs())
                LoadTranslations(Path.Combine(automaticsChildModDir, "Languages"));

            LoadTranslations(Automatics.GetInjectedResourcePath("Languages"));

            Automatics.OnInitTerminal += Command.Register;
        }

        private static void LoadTranslations(string languagesDir)
        {
            if (string.IsNullOrEmpty(languagesDir)) return;

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