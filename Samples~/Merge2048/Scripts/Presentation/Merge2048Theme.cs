using UnityEngine;

namespace Merge2048.Presentation
{
    public static class Merge2048Theme
    {
        public static readonly Color BACKGROUND_COLOR = new Color32(0xFA, 0xF8, 0xEF, 0xFF);
        public static readonly Color BOARD_BACKGROUND_COLOR = new Color32(0xBB, 0xAD, 0xA0, 0xFF);
        public static readonly Color EMPTY_CELL_COLOR = new Color32(0xCD, 0xC1, 0xB4, 0x59);

        public static readonly Color BUTTON_NORMAL_COLOR = new Color32(0x8F, 0x7A, 0x66, 0xFF);
        public static readonly Color BUTTON_HIGHLIGHTED_COLOR = new Color32(0xA3, 0x8E, 0x79, 0xFF);
        public static readonly Color BUTTON_PRESSED_COLOR = new Color32(0x74, 0x62, 0x51, 0xFF);
        public static readonly Color BUTTON_DISABLED_COLOR = new Color32(0x8F, 0x7A, 0x66, 0x80);

        // Unity's ColorBlock defaults selectedColor to near-white, which clashes with the
        // palette and lingers after a tap on touch. Keep it on-theme (matches highlighted).
        public static readonly Color BUTTON_SELECTED_COLOR = new Color32(0xA3, 0x8E, 0x79, 0xFF);

        public static readonly Color TEXT_ON_DARK_COLOR = new Color32(0xF9, 0xF6, 0xF2, 0xFF);
        public static readonly Color TEXT_ON_LIGHT_COLOR = new Color32(0x77, 0x6E, 0x65, 0xFF);

        // Dark scrim over the still-visible, frozen Gameplay board (not an opaque page swap).
        public static readonly Color GAME_OVER_SCRIM_COLOR = new Color32(0x1A, 0x16, 0x12, 0xB0);

        public const float TITLE_FONT_SIZE = 72f;
        public const float HUD_FONT_SIZE = 42f;
        public const float BUTTON_FONT_SIZE = 40f;
        public const float HUD_CAPTION_FONT_SIZE = 22f;
        public const float HUD_BADGE_FONT_SIZE = 28f;
        public const float SCORE_POPUP_FONT_SIZE = 36f;

        // Single shared radius: RoundedRectSprite bakes ONE cached sprite reused by
        // buttons, cards and tiles alike, so a per-surface radius would be unused.
        public const float CORNER_RADIUS_PX = 12f;

        public const float FADE_DURATION = 0.15f;
        public const float TILE_SLIDE_DURATION = 0.1f;
        public const float MERGE_POP_DURATION = 0.12f;
        public const float MERGE_POP_SCALE = 1.15f;
        public const float SPAWN_SCALE_DURATION = 0.12f;
        public const float SCORE_POPUP_DURATION = 0.5f;
        public const float SCORE_POPUP_RISE_DISTANCE = 60f;
    }
}
