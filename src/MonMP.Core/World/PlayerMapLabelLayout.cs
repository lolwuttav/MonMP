using System;

namespace MonMP.Core.World
{
    // TextMesh glyph metrics are pixels; one glyph pixel occupies characterSize / 10 units.
    public readonly struct PlayerMapLabelLayout
    {
        public readonly float CharacterSize, U, V, LineStep, TextHeight;
        private PlayerMapLabelLayout(float size, float u, float v, float step, float height)
        { CharacterSize = size; U = u; V = v; LineStep = step; TextHeight = height; }

        public static PlayerMapLabelLayout Calculate(float width, float height, float size,
            float glyphWidth, float glyphHeight, float radius, int row, int rows, float u, float v)
        {
            if (!(width > 0) || !(height > 0) || !(size > 0) || !(glyphWidth > 0) || !(glyphHeight > 0) || rows < 1 || row < 0 || row >= rows)
                throw new ArgumentOutOfRangeException(nameof(rows));
            float textWidth = glyphWidth * size * .1f, textHeight = glyphHeight * size * .1f;
            float fullHeight = textHeight * (1 + 1.25f * (rows - 1));
            float shrink = Math.Min(1, Math.Min(width * .9f / textWidth, height * .82f / fullHeight));
            size *= shrink; textWidth *= shrink; textHeight *= shrink;
            float step = textHeight * 1.25f;
            float half = (step * (rows - 1) + textHeight) * .5f;
            float marginU = Math.Min(.49f, (textWidth * .5f + radius) / width);
            u = Clamp(u, marginU, 1 - marginU);
            float marginV = half / height;
            float offset = (radius * 1.45f + textHeight * .2f + half) / height;
            float center = v + offset;
            if (center > 1 - marginV) center = v - offset;
            center = Clamp(center, marginV, 1 - marginV);
            return new PlayerMapLabelLayout(size, u, center + ((rows - 1) * .5f - row) * step / height, step, textHeight);
        }
        private static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    }
}
