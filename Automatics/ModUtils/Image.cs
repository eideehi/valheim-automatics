using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Automatics.ModUtils
{
    public static class Image
    {
        private static readonly Dictionary<string, Texture2D> _textureCache;

        static Image()
        {
            _textureCache = new Dictionary<string, Texture2D>();
        }

        private static Sprite CreateSprite(Texture2D texture, SpriteInfo info)
        {
            return Sprite.Create(texture, new Rect(0, 0, info.width, info.height), Vector2.zero);
        }

        public static Sprite CreateSprite(string texturesDir, SpriteInfo info)
        {
            var path = Path.Combine(texturesDir, info.file);

            if (_textureCache.TryGetValue(path, out var tex)) return tex != null ? CreateSprite(tex, info) : null;

            try
            {
                Automatics.ModLogger.LogInfo($"Try to create sprite: {path}");

                var texture = new Texture2D(0, 0);
                texture.LoadImage(File.ReadAllBytes(path));

                _textureCache.Add(path, texture);
                return CreateSprite(texture, info);
            }
            catch (Exception e)
            {
                Automatics.ModLogger.LogError($"Failed to create sprite: {path}\n{e}");
                _textureCache.Add(path, null);
                return null;
            }
        }
    }

    [Serializable]
    public struct SpriteInfo
    {
        public string file;
        public int width;
        public int height;
    }
}