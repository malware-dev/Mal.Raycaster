// Procedurally generated wall textures. No external image files: each wall index
// gets a power-of-two (64x64) texture built from a base colour plus a pattern, so
// sampling can use cheap bit-masking for wrap-around.
//
// Ported from the SpriteRaycaster spike to SE mod rules: System.Math (no MathF),
// VRageMath.Color, no target-typed new.
using System;
using VRageMath;

namespace Mal.Raycaster
{
    public sealed class Texture
    {
        public readonly int W, H;
        public readonly Color[] Pixels;

        public Texture(int w, int h)
        {
            W = w; H = h;
            Pixels = new Color[w * h];
        }

        // W/H are powers of two, so mask instead of modulo to wrap.
        public Color Sample(int x, int y)
        {
            return Pixels[(y & (H - 1)) * W + (x & (W - 1))];
        }
    }

    public static class Textures
    {
        const int Size = 64;
        static readonly Color Mortar = new Color(40, 38, 36);

        /// <summary>Builds the texture table indexed to match the wall palette (0 unused).</summary>
        public static Texture[] BuildWallSet()
        {
            return new Texture[]
            {
                Brick(new Color(0, 0, 0)),       // 0 unused
                Brick(new Color(190, 70, 60)),   // 1 red brick
                Brick(new Color(70, 110, 190)),  // 2 blue brick
                Blocks(new Color(80, 175, 95)),  // 3 green blocks
                Stone(new Color(200, 175, 80)),  // 4 yellow stone
                Panel(new Color(160, 95, 195)),  // 5 purple tech panel
            };
        }

        /// <summary>
        /// Billboard textures (transparent background: pixels left at A=0 are skipped
        /// when drawn). Indexed by the Tex field on a Billboard.
        /// </summary>
        public static Texture[] BuildBillboards()
        {
            return new Texture[]
            {
                Pillar(),   // 0
                Barrel(),   // 1
                Slime(),    // 2
            };
        }

        /// <summary>A lit stone column, wider at cap and base. Full height.</summary>
        static Texture Pillar()
        {
            var t = new Texture(Size, Size); // defaults to fully transparent
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float hw = (y < 6 || y >= 58) ? 16f : 10f; // wider cap/base
                    float dx = (x - 32) / hw;
                    if (dx < -1f || dx > 1f) continue;
                    float shade = (float)Math.Sqrt(Math.Max(0f, 1f - dx * dx)); // cylinder lighting
                    int v = (int)(70 + 130 * shade) - (int)(Hash(x, y / 8) * 12);
                    t.Pixels[y * Size + x] = new Color(ClampByte(v), ClampByte(v), ClampByte(v - 12), 255);
                }
            return t;
        }

        /// <summary>A hooped wooden barrel sitting in the lower-centre.</summary>
        static Texture Barrel()
        {
            var t = new Texture(Size, Size);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float nx = (x - 32) / 16f, ny = (y - 40) / 22f;
                    if (nx * nx + ny * ny > 1f) continue;
                    float shade = (float)Math.Sqrt(Math.Max(0f, 1f - nx * nx)); // round body
                    int r = (int)(150 * shade), g = (int)(95 * shade), b = (int)(50 * shade);
                    if (Math.Abs(y - 24) < 2 || Math.Abs(y - 40) < 2 || Math.Abs(y - 56) < 2)
                    {
                        int m = (int)(90 * shade) + 30; // metal hoops
                        r = g = b = m;
                    }
                    t.Pixels[y * Size + x] = new Color(ClampByte(r), ClampByte(g), ClampByte(b), 255);
                }
            return t;
        }

        /// <summary>A round green slime with two eyes.</summary>
        static Texture Slime()
        {
            var t = new Texture(Size, Size);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float nx = (x - 32) / 18f, ny = (y - 42) / 20f;
                    if (nx * nx + ny * ny > 1f) continue;
                    float shade = (float)Math.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny)) * 0.6f + 0.4f;
                    t.Pixels[y * Size + x] =
                        new Color(ClampByte(40 * shade), ClampByte(180 * shade), ClampByte(70 * shade), 255);
                }
            DrawEye(t, 25, 36);
            DrawEye(t, 39, 36);
            return t;
        }

        static void DrawEye(Texture t, int cx, int cy)
        {
            for (int y = cy - 4; y <= cy + 4; y++)
                for (int x = cx - 4; x <= cx + 4; x++)
                {
                    if (x < 0 || y < 0 || x >= t.W || y >= t.H) continue;
                    float d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d <= 16) t.Pixels[y * t.W + x] = Color.White;
                    if (d <= 4) t.Pixels[y * t.W + x] = new Color(20, 20, 20, 255); // pupil
                }
        }

        /// <summary>A metal sliding door: framed steel with a centre seam and mid rail.</summary>
        public static Texture Door()
        {
            var t = new Texture(Size, Size);
            Color steel = new Color(120, 125, 135);
            Color dark = new Color(64, 68, 76);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    Color c = Vary(steel, x, y, 6);
                    bool frame = x < 4 || x >= Size - 4 || y < 4 || y >= Size - 4;
                    bool seam = Math.Abs(x - 31.5f) < 1.5f;  // central vertical seam
                    bool rail = Math.Abs(y - 31.5f) < 1.5f;  // mid rail
                    if (frame || seam || rail) c = dark;
                    t.Pixels[y * Size + x] = c;
                }
            return t;
        }

        /// <summary>Texture for the floor plane.</summary>
        public static Texture Floor()
        {
            return Blocks(new Color(115, 95, 75)); // tan tiles
        }

        /// <summary>Texture for the ceiling plane.</summary>
        public static Texture Ceiling()
        {
            return Stone(new Color(60, 65, 90));  // dark blue stone
        }

        // Deterministic per-pixel pseudo-noise in [0,1].
        static float Hash(int x, int y)
        {
            int h = x * 374761393 + y * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535f;
        }

        static Color Vary(Color c, int x, int y, float amount)
        {
            float offset = (Hash(x, y) - 0.5f) * 2f * amount; // [-amount, amount]
            return new Color(
                ClampByte(c.R + offset), ClampByte(c.G + offset), ClampByte(c.B + offset));
        }

        static int ClampByte(float v)
        {
            return v < 0 ? 0 : v > 255 ? 255 : (int)v;
        }

        /// <summary>Offset-row brick courses with mortar gaps.</summary>
        static Texture Brick(Color baseC)
        {
            const int bh = 16, bw = 32, mortar = 2;
            var t = new Texture(Size, Size);
            for (int y = 0; y < Size; y++)
            {
                int row = y / bh;
                int offset = (row % 2) * (bw / 2);
                for (int x = 0; x < Size; x++)
                {
                    bool isMortar = (y % bh) < mortar || ((x + offset) % bw) < mortar;
                    t.Pixels[y * Size + x] = isMortar ? Mortar : Vary(baseC, x, y, 18);
                }
            }
            return t;
        }

        /// <summary>Square tiles, each tile slightly different, with grout lines.</summary>
        static Texture Blocks(Color baseC)
        {
            const int tile = 16, grout = 2;
            var t = new Texture(Size, Size);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    bool g = (x % tile) < grout || (y % tile) < grout;
                    if (g) { t.Pixels[y * Size + x] = Mortar; continue; }
                    // shift each tile's brightness a little
                    float tn = (Hash(x / tile, y / tile) - 0.5f) * 40f;
                    Color tc = new Color(ClampByte(baseC.R + tn), ClampByte(baseC.G + tn), ClampByte(baseC.B + tn));
                    t.Pixels[y * Size + x] = Vary(tc, x, y, 10);
                }
            return t;
        }

        /// <summary>Rough stone: heavy noise, no regular seams.</summary>
        static Texture Stone(Color baseC)
        {
            var t = new Texture(Size, Size);
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    // blend two noise octaves for a cloudy look
                    float n = Hash(x, y) * 0.6f + Hash(x / 2, y / 2) * 0.4f;
                    float d = (n - 0.5f) * 60f;
                    t.Pixels[y * Size + x] =
                        new Color(ClampByte(baseC.R + d), ClampByte(baseC.G + d), ClampByte(baseC.B + d));
                }
            return t;
        }

        /// <summary>Tech panel: framed border plus corner rivets.</summary>
        static Texture Panel(Color baseC)
        {
            const int cell = 32, border = 3, rivet = 2;
            var t = new Texture(Size, Size);
            Color frame = new Color(ClampByte(baseC.R - 50), ClampByte(baseC.G - 50), ClampByte(baseC.B - 50));
            Color rivetC = new Color(ClampByte(baseC.R + 60), ClampByte(baseC.G + 60), ClampByte(baseC.B + 60));
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int cx = x % cell, cy = y % cell;
                    bool isBorder = cx < border || cy < border || cx >= cell - border || cy >= cell - border;
                    bool isRivet = (cx >= border && cx < border + rivet && cy >= border && cy < border + rivet);
                    Color c = isRivet ? rivetC : isBorder ? frame : Vary(baseC, x, y, 12);
                    t.Pixels[y * Size + x] = c;
                }
            return t;
        }
    }
}
