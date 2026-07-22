using UnityEngine;

namespace Merge2048.Presentation
{
    public static class RoundedRectSprite
    {
        private const int TEXTURE_SIZE = 32;
        private const float BORDER_PX = Merge2048Theme.CORNER_RADIUS_PX + 2f;

        private static Sprite _cached;

        public static Sprite Get()
        {
            if (_cached != null)
            {
                return _cached;
            }

            var texture = new Texture2D(TEXTURE_SIZE, TEXTURE_SIZE, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var pixels = new Color32[TEXTURE_SIZE * TEXTURE_SIZE];
            var opaque = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
            var transparent = new Color32(0xFF, 0xFF, 0xFF, 0x00);

            for (int y = 0; y < TEXTURE_SIZE; y++)
            {
                for (int x = 0; x < TEXTURE_SIZE; x++)
                {
                    pixels[(y * TEXTURE_SIZE) + x] = IsInsideRoundedRect(x, y) ? opaque : transparent;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            var rect = new Rect(0f, 0f, TEXTURE_SIZE, TEXTURE_SIZE);
            var pivot = new Vector2(0.5f, 0.5f);
            var border = new Vector4(BORDER_PX, BORDER_PX, BORDER_PX, BORDER_PX);

            _cached = Sprite.Create(texture, rect, pivot, 100f, 0, SpriteMeshType.FullRect, border);
            return _cached;
        }

        private static bool IsInsideRoundedRect(int x, int y)
        {
            float radius = Merge2048Theme.CORNER_RADIUS_PX;
            float pixelX = x + 0.5f;
            float pixelY = y + 0.5f;

            float cornerX = Mathf.Clamp(pixelX, radius, TEXTURE_SIZE - radius);
            float cornerY = Mathf.Clamp(pixelY, radius, TEXTURE_SIZE - radius);

            float deltaX = pixelX - cornerX;
            float deltaY = pixelY - cornerY;

            return ((deltaX * deltaX) + (deltaY * deltaY)) <= radius * radius;
        }
    }
}
