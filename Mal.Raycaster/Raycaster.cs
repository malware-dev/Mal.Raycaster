// Wolfenstein-style DDA raycaster. It is deliberately backend-agnostic: it only
// knows about Ion's RenderContext and the SE sprite API, emitting one round
// "Circle" sprite per logical output pixel -- a literal dot per pixel.
//
// Ported from the SpriteRaycaster spike to SE mod rules: C# 6 only (no MathF, no
// ref-locals, no tuples, no target-typed new), VRageMath types, and sprites go
// through RenderContext.AddSprite instead of a stand-in surface.
//
// Resolution-agnostic: the output dot grid is OutW columns wide and however many
// rows fit the surface at the same square dot size, so it fills the full width and
// the available height of any LCD without stretching. The camera plane length is
// set to (width/height)/2 each resize so the rendered world keeps square pixels --
// a wider screen simply sees a wider horizontal field, never a stretched one. The
// internal compute buffer is the dot grid times SS per axis (supersampling).
using System;
using System.Collections.Generic;
using Mal.MdkModMixin.Ion;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace Mal.Raycaster
{
    public sealed class Raycaster
    {
        public struct Input
        {
            public bool Forward, Back, TurnLeft, TurnRight, StrafeLeft, StrafeRight;
        }

        public const int OutW = 128;        // output dot columns (the "vibe" resolution)
        public const int SS = 2;            // supersample factor per axis (SS*SS samples per dot)
        // SS=2 keeps the anti-aliased look. The expensive per-texel lighting/shadow
        // march is NOT run SS*SS times, though: it's computed once per output dot (on
        // the dot's first subsample) and cached, then applied to all SS*SS texture
        // taps in that dot. So texture/edge AA is 2x but lighting runs at dot res.
        // Approximation: a dot straddling a wall/floor/ceiling seam gets one lighting
        // value for the whole dot -> a faint fringe at those edges. At SS=1 this is a
        // no-op (every texel is its own dot, so it lights every texel as before).

        // ----- world map cell codes -----
        // 0          = empty (passable)
        // 1..5       = solid wall, value indexes into _textures (see Textures.BuildWallSet)
        // DoorV (6)  = vertical door slab
        // DoorH (7)  = horizontal door slab
        readonly int[,] _map =
        {
            {1,1,1,1,1,1,1,1,2,2,2,2,2,2,2,2},
            {1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2},
            {1,0,3,3,0,0,0,0,0,0,4,4,4,0,0,2},
            {1,0,3,0,0,0,6,0,0,0,0,0,4,0,0,2},
            {1,0,3,0,0,0,0,0,0,0,0,0,4,0,0,2},
            {1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,2},
            {1,0,0,0,0,0,5,7,5,5,0,0,0,0,0,2},
            {1,0,0,0,0,0,5,0,0,5,0,0,0,0,0,2},
            {2,0,0,0,0,0,6,0,0,0,0,0,0,0,0,1},
            {2,0,0,0,0,0,5,5,0,5,0,0,0,0,0,1},
            {2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1},
            {2,0,0,3,0,0,0,0,0,0,0,0,4,0,0,1},
            {2,0,0,3,0,0,0,0,0,0,0,0,4,0,0,1},
            {2,0,0,3,3,0,0,0,0,0,0,4,4,0,0,1},
            {2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1},
            {2,2,2,2,2,2,2,2,1,1,1,1,1,1,1,1},
        };
        readonly int _mapW;
        readonly int _mapH;

        // wall textures by index (0 unused), built procedurally at startup
        readonly Texture[] _textures = Textures.BuildWallSet();
        readonly Texture _floorTex = Textures.Floor();
        readonly Texture _ceilTex = Textures.Ceiling();
        readonly Texture _doorTex = Textures.Door();
        readonly Texture[] _billboardTex = Textures.BuildBillboards();

        // ----- doors -----
        const int DoorV = 6;   // vertical slab (normal +-X), slide along Y
        const int DoorH = 7;   // horizontal slab (normal +-Y), slide along X

        // True for either door orientation; lets call sites read as intent rather than
        // repeating the DoorV/DoorH literal pair. The single-orientation checks elsewhere
        // stay explicit because they branch on which slab axis the ray crosses.
        static bool IsDoor(int cell) { return cell == DoorV || cell == DoorH; }
        const float DoorRange = 2.2f; // auto-open when the player is this close
        const float DoorSpeed = 2.5f; // open/close units per second

        struct DoorCell
        {
            public int X, Y;
            public DoorCell(int x, int y) { X = x; Y = y; }
        }

        readonly float[,] _doorOpen;          // 0 = shut, 1 = fully open
        readonly List<DoorCell> _doorCells = new List<DoorCell>();

        // ----- resolution: filled in by EnsureSize from the surface bounds -----
        int _outH;          // output dot rows (derived from the surface aspect)
        int _renderW;       // internal compute width  = OutW * SS
        int _renderH;       // internal compute height = _outH * SS
        float _planeLen;    // camera plane half-width (FOV); (width/height)/2 for square pixels

        // Per-column wall depth, written during the wall pass and read by the billboard
        // pass so sprites are correctly occluded by closer walls.
        float[] _zBuffer;

        // Internal hi-res colour buffer (one entry per supersample), then downsampled
        // into _outColor (one entry per output dot) before sprites are emitted.
        Color[] _cellColor;
        Color[] _outColor;

        // Per-output-dot diffuse light multiplier (R/G/B), computed once on the dot's
        // representative subsample and reused by the rest of the dot's texture taps.
        // Sized OutW * _outH.
        float[] _dotLitR;
        float[] _dotLitG;
        float[] _dotLitB;

        /// <summary>Average each SSxSS block into one dot (anti-aliased) vs point-sample.</summary>
        public bool SupersampleEnabled { get; set; }

        /// <summary>Backlight glow: average each NxN block of dots into one quad behind them.</summary>
        public bool BacklightEnabled { get; set; }
        const int BacklightBlock = 4;      // average a 4x4 block (16 dots) per quad
        const float BacklightGlow = 0.6f;  // dim the glow relative to the dots

        struct Billboard
        {
            public float X, Y;
            public int Tex;
            public Billboard(float x, float y, int tex) { X = x; Y = y; Tex = tex; }
        }

        // a few props scattered around the open areas of the map
        readonly Billboard[] _billboards =
        {
            new Billboard(8.5f, 8.5f, 0),   // pillar
            new Billboard(4.5f, 12.5f, 1),  // barrel
            new Billboard(12.5f, 6.5f, 1),  // barrel
            new Billboard(11.5f, 3.5f, 2),  // slime
            new Billboard(13.5f, 13.5f, 2), // slime
        };
        readonly int[] _spriteOrder;

        // ----- lighting -----
        /// <summary>Toggle colored point lights vs the plain distance-fog shading.</summary>
        public bool LightingEnabled { get; set; }

        /// <summary>Toggle per-light shadow occlusion (a grid-march toward each light).</summary>
        public bool ShadowsEnabled { get; set; }

        const float Ambient = 0.10f; // base light so unlit areas aren't pure black

        struct Light
        {
            public float X, Y, Z;      // world position; Z in [0,1] (floor..ceiling height)
            public float R, G, B;      // colour
            public float Intensity;
            public float Falloff;      // larger = tighter pool
            public bool Spot;          // spotlight (cone) vs omni point light
            public float DirX, DirY, DirZ;     // spotlight aim (unit vector)
            public float CosInner, CosOuter;   // cone: full inside inner, zero outside outer

            public Light(float x, float y, float z, float r, float g, float b, float i, float f)
            {
                X = x; Y = y; Z = z; R = r; G = g; B = b; Intensity = i; Falloff = f;
                Spot = false; DirX = 0f; DirY = 0f; DirZ = 0f; CosInner = 0f; CosOuter = 0f;
            }

            // spotlight constructor
            public Light(float x, float y, float z, float r, float g, float b, float i, float f,
                float dx, float dy, float dz, float cosInner, float cosOuter)
            {
                X = x; Y = y; Z = z; R = r; G = g; B = b; Intensity = i; Falloff = f;
                Spot = true; DirX = dx; DirY = dy; DirZ = dz; CosInner = cosInner; CosOuter = cosOuter;
            }
        }

        readonly Light[] _lights =
        {
            new Light(4.0f,  4.0f,  0.90f, 1.0f, 0.85f, 0.60f, 3.2f, 0.5f),  // warm ceiling lamp
            new Light(8.0f,  11.0f, 0.50f, 1.0f, 0.25f, 0.10f, 2.8f, 0.6f),  // red torch
            new Light(12.5f, 4.0f,  0.75f, 0.30f, 0.70f, 1.0f, 2.8f, 0.6f),  // cyan light
            new Light(11.5f, 3.5f,  0.40f, 0.30f, 1.0f, 0.40f, 1.6f, 0.9f),  // green slime glow
            new Light(13.5f, 13.5f, 0.40f, 0.30f, 1.0f, 0.40f, 1.6f, 0.9f),  // green slime glow
            // flashlight: spotlight re-aimed to the player each frame (see Update)
            new Light(3.5f, 3.5f, 0.5f, 1.0f, 0.95f, 0.85f, 5.0f, 0.10f, 1f, 0f, 0f, 0.970f, 0.918f),
        };
        readonly int _flashlightIndex;

        /// <summary>Toggle the player flashlight.</summary>
        public bool FlashlightEnabled { get; set; }
        const float FlashlightIntensity = 5.0f;

        /// <summary>Toggle between true textured floor/ceiling casting and the cheap gradient.</summary>
        public bool TexturedPlanesEnabled { get; set; }

        // Per-row plane-casting tables, rebuilt each frame. For row y, the floor/ceiling
        // world position is (BaseX + StepX*x, BaseY + StepY*x); rows depend only on y.
        float[] _planeBaseX;
        float[] _planeBaseY;
        float[] _planeStepX;
        float[] _planeStepY;
        float[] _planeFog;

        static readonly Color CeilingColor = new Color(70, 70, 100);
        static readonly Color FloorColor = new Color(95, 80, 65);

        // Per-row ceiling/floor colours, precomputed once per resize. Each row
        // corresponds to a fixed distance from the camera (rows near the horizon are
        // far away), so we run that distance through the same fog curve as the walls
        // -> a depth gradient that fades into the same haze the walls do.
        Color[] _ceilRow;
        Color[] _floorRow;

        // ----- player / camera -----
        float _px = 3.5f, _py = 3.5f;     // position in map units
        float _dirX = 1f, _dirY = 0f;     // direction vector (unit)
        float _planeX, _planeY;           // camera plane, derived from dir + _planeLen each render

        const float MoveSpeed = 3.0f;     // units / sec
        const float TurnSpeed = 2.4f;     // radians / sec

        float _time;                      // accumulated seconds, for animation

        public Raycaster()
        {
            // Full-feature defaults: the spike is "does the full thing run in-game".
            SupersampleEnabled = true;
            BacklightEnabled = true;
            LightingEnabled = true;
            ShadowsEnabled = true;
            FlashlightEnabled = true;
            TexturedPlanesEnabled = true;

            _mapH = _map.GetLength(0);
            _mapW = _map.GetLength(1);
            _spriteOrder = new int[_billboards.Length];
            _flashlightIndex = _lights.Length - 1;

            _doorOpen = new float[_mapH, _mapW];
            for (int y = 0; y < _mapH; y++)
                for (int x = 0; x < _mapW; x++)
                    if (IsDoor(_map[y, x]))
                        _doorCells.Add(new DoorCell(x, y));

            // Size-dependent buffers are allocated lazily in EnsureSize, once we know
            // the surface's aspect from the first Render.
        }

        // Allocate (or reallocate) the size-dependent buffers for a given output dot
        // grid, and set the FOV so the rendered world keeps square pixels at this
        // aspect. Cheap no-op when the dimensions are unchanged (the common case --
        // an LCD's surface size is fixed per block).
        void EnsureSize(int outH)
        {
            if (outH < 1) outH = 1;
            int renderW = OutW * SS;
            int renderH = outH * SS;
            if (renderW == _renderW && renderH == _renderH) return;

            _outH = outH;
            _renderW = renderW;
            _renderH = renderH;
            // square pixels: horizontal field widens with aspect, vertical stays fixed
            _planeLen = (OutW / (float)_outH) * 0.5f;

            _zBuffer = new float[_renderW];
            _cellColor = new Color[_renderW * _renderH];
            _outColor = new Color[OutW * _outH];
            _dotLitR = new float[OutW * _outH];
            _dotLitG = new float[OutW * _outH];
            _dotLitB = new float[OutW * _outH];
            _planeBaseX = new float[_renderH];
            _planeBaseY = new float[_renderH];
            _planeStepX = new float[_renderH];
            _planeStepY = new float[_renderH];
            _planeFog = new float[_renderH];
            _ceilRow = new Color[_renderH];
            _floorRow = new Color[_renderH];

            PrecomputeFloorCeiling();
        }

        void PrecomputeFloorCeiling()
        {
            float horizon = _renderH / 2f;
            float camHeight = _renderH / 2f; // eye at mid-height of the wall band
            for (int y = 0; y < _renderH; y++)
            {
                // perpendicular pixel distance from the horizon row
                float distFromHorizon = Math.Abs(y + 0.5f - horizon);
                if (distFromHorizon < 1f) distFromHorizon = 1f;   // avoid div-by-zero at the horizon
                float rowDist = camHeight / distFromHorizon;      // far near the horizon, near at the edges
                float fog = FogFactor(rowDist);
                if (y < horizon) _ceilRow[y] = Scale(CeilingColor, fog);
                else _floorRow[y] = Scale(FloorColor, fog);
            }
        }

        public void Update(float dt, Input input)
        {
            _time += dt;

            // Demo: orbit the red torch around an open area. Lighting is fully dynamic,
            // so this costs nothing extra -- the shadows re-march from its new position
            // every frame. Mutate the struct field in place via the array indexer.
            float a = _time * 0.8f;
            _lights[1].X = 7.5f + 2.0f * (float)Math.Cos(a);
            _lights[1].Y = 11.0f + 2.0f * (float)Math.Sin(a);

            // flashlight: sits at the player, aims along the view direction (tilted
            // slightly down so the beam pools on the floor ahead).
            int fi = _flashlightIndex;
            _lights[fi].X = _px; _lights[fi].Y = _py; _lights[fi].Z = 0.5f;
            const float aimZ = -0.12f;
            float inv = 1f / (float)Math.Sqrt(_dirX * _dirX + _dirY * _dirY + aimZ * aimZ);
            _lights[fi].DirX = _dirX * inv; _lights[fi].DirY = _dirY * inv; _lights[fi].DirZ = aimZ * inv;
            _lights[fi].Intensity = FlashlightEnabled ? FlashlightIntensity : 0f;

            UpdateDoors(dt);

            float move = MoveSpeed * dt;
            float rot = TurnSpeed * dt;

            if (input.Forward) TryMove(_dirX * move, _dirY * move);
            if (input.Back) TryMove(-_dirX * move, -_dirY * move);
            // unit perpendicular to dir, so strafe speed doesn't depend on the FOV
            float perpX = -_dirY, perpY = _dirX;
            if (input.StrafeLeft) TryMove(perpX * move, perpY * move);
            if (input.StrafeRight) TryMove(-perpX * move, -perpY * move);

            // texture space has Y pointing down, which flips the visual sense of
            // rotation -- so turn-left rotates negative and turn-right positive.
            if (input.TurnLeft) Rotate(-rot);
            if (input.TurnRight) Rotate(rot);
        }

        void TryMove(float dx, float dy)
        {
            // simple per-axis collision with a small buffer off the walls. The pad is
            // applied in the direction of travel; when a delta is 0 that axis doesn't
            // move (nx==_px / ny==_py) so Math.Sign(0)==0 dropping the pad is moot.
            const float pad = 0.15f;
            float nx = _px + dx;
            float ny = _py + dy;
            if (!IsWall(nx + Math.Sign(dx) * pad, _py)) _px = nx;
            if (!IsWall(_px, ny + Math.Sign(dy) * pad)) _py = ny;
        }

        bool IsWall(float x, float y)
        {
            int mx = (int)x, my = (int)y;
            if (mx < 0 || my < 0 || mx >= _mapW || my >= _mapH) return true;
            int v = _map[my, mx];
            if (v == 0) return false;
            if (IsDoor(v)) return _doorOpen[my, mx] < 0.8f; // passable once mostly open
            return true;
        }

        // Doors auto-open when the player is near and close again when they leave.
        void UpdateDoors(float dt)
        {
            for (int i = 0; i < _doorCells.Count; i++)
            {
                int dx = _doorCells[i].X, dy = _doorCells[i].Y;
                float cx = dx + 0.5f, cy = dy + 0.5f;
                float dist2 = (cx - _px) * (cx - _px) + (cy - _py) * (cy - _py);
                float target = dist2 < DoorRange * DoorRange ? 1f : 0f;
                float cur = _doorOpen[dy, dx];
                if (cur < target) cur = Math.Min(target, cur + DoorSpeed * dt);
                else if (cur > target) cur = Math.Max(target, cur - DoorSpeed * dt);
                _doorOpen[dy, dx] = cur;
            }
        }

        void Rotate(float a)
        {
            float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
            float ndx = _dirX * cos - _dirY * sin;
            float ndy = _dirX * sin + _dirY * cos;
            // renormalize to kill accumulated floating drift over many turns
            float len = (float)Math.Sqrt(ndx * ndx + ndy * ndy);
            _dirX = ndx / len; _dirY = ndy / len;
        }

        public void Render(RenderContext ctx, RectangleF bounds)
        {
            // Square dots fill the full width; rows fill the available height. Dot size
            // comes from the width, the row count from the height -> always the surface's
            // own aspect, never stretched.
            float dot = bounds.Width / OutW;
            if (dot <= 0f) return;
            int outH = (int)(bounds.Height / dot + 0.5f);
            EnsureSize(outH);

            // derive the camera plane from the (unit) direction + this aspect's FOV
            _planeX = -_dirY * _planeLen;
            _planeY = _dirX * _planeLen;

            float originX = bounds.X;                                   // dots span the full width
            float gridH = _outH * dot;
            float originY = bounds.Y + (bounds.Height - gridH) * 0.5f;  // centre the row band

            BuildPlaneTables();

            for (int x = 0; x < _renderW; x++)
            {
                // ray direction for this column
                float cameraX = 2f * x / _renderW - 1f;
                float rayX = _dirX + _planeX * cameraX;
                float rayY = _dirY + _planeY * cameraX;

                int mapX = (int)_px;
                int mapY = (int)_py;

                float deltaX = rayX == 0 ? 1e30f : Math.Abs(1f / rayX);
                float deltaY = rayY == 0 ? 1e30f : Math.Abs(1f / rayY);

                int stepX, stepY;
                float sideDistX, sideDistY;
                if (rayX < 0) { stepX = -1; sideDistX = (_px - mapX) * deltaX; }
                else { stepX = 1; sideDistX = (mapX + 1f - _px) * deltaX; }
                if (rayY < 0) { stepY = -1; sideDistY = (_py - mapY) * deltaY; }
                else { stepY = 1; sideDistY = (mapY + 1f - _py) * deltaY; }

                // DDA
                int side = 0, hit = 0, wall = 0;
                bool doorHit = false;
                float doorPerp = 0f, doorLat = 0f; // distance to, and coord along, the door slab
                while (hit == 0)
                {
                    if (sideDistX < sideDistY) { sideDistX += deltaX; mapX += stepX; side = 0; }
                    else { sideDistY += deltaY; mapY += stepY; side = 1; }

                    if (mapX < 0 || mapY < 0 || mapX >= _mapW || mapY >= _mapH) { hit = 1; wall = 1; break; }
                    wall = _map[mapY, mapX];

                    if (IsDoor(wall))
                    {
                        // intersect the slab in the middle of the cell; the door is solid
                        // over the lateral range [0, 1-open] and a gap beyond it.
                        float open = _doorOpen[mapY, mapX];
                        if (wall == DoorV && Math.Abs(rayX) > 1e-6f)
                        {
                            float t = (mapX + 0.5f - _px) / rayX;
                            float frac = _py + t * rayY - mapY;
                            if (t > 0f && frac >= 0f && frac <= 1f - open)
                            { doorHit = true; doorPerp = t; doorLat = frac; side = 0; hit = 1; break; }
                        }
                        else if (wall == DoorH && Math.Abs(rayY) > 1e-6f)
                        {
                            float t = (mapY + 0.5f - _py) / rayY;
                            float frac = _px + t * rayX - mapX;
                            if (t > 0f && frac >= 0f && frac <= 1f - open)
                            { doorHit = true; doorPerp = t; doorLat = frac; side = 1; hit = 1; break; }
                        }
                        // otherwise the ray slips through the open gap: keep marching
                    }
                    else if (wall > 0) hit = 1;
                }

                float perpDist = doorHit
                    ? doorPerp
                    : (side == 0 ? sideDistX - deltaX : sideDistY - deltaY);
                if (perpDist < 0.0001f) perpDist = 0.0001f;
                _zBuffer[x] = perpDist;

                int lineH = (int)(_renderH / perpDist);
                if (lineH < 1) lineH = 1;
                int drawStart = -lineH / 2 + _renderH / 2;
                int drawEnd = lineH / 2 + _renderH / 2;
                if (drawStart < 0) drawStart = 0;
                if (drawEnd >= _renderH) drawEnd = _renderH - 1;

                // world-space point where the ray struck the wall (used for texturing + lighting)
                float hitX = _px + perpDist * rayX;
                float hitY = _py + perpDist * rayY;

                // texture column: where along the wall/door cell the ray landed
                Texture tex;
                int texX;
                if (doorHit)
                {
                    tex = _doorTex;
                    // doorLat shrinks as the door slides away, revealing the gap
                    texX = (int)(doorLat * tex.W);
                    if (texX >= tex.W) texX = tex.W - 1;
                }
                else
                {
                    // Invariant: a solid non-door cell carries a wall index in
                    // [1, _textures.Length-1] (the map palette and _textures table are
                    // built together in BuildWallSet), so this index is always in range.
                    tex = _textures[wall];
                    float wallX = (side == 0 ? hitY : hitX);
                    wallX -= (float)Math.Floor(wallX);
                    texX = (int)(wallX * tex.W);
                    // keep texture orientation consistent regardless of which face we see
                    if (side == 0 && rayX > 0) texX = tex.W - 1 - texX;
                    if (side == 1 && rayY < 0) texX = tex.W - 1 - texX;
                }

                float fog = FogFactor(perpDist);
                if (side == 1) fog *= 0.7f; // darker Y-faces for fake lighting
                float texStep = (float)tex.H / lineH;

                // This compute column is the representative for its output dot column
                // (and so writes the per-dot light cache) only on its first subsample.
                bool repCol = (x % SS) == 0;
                int dotCol = x / SS;

                // emit one cell per logical pixel in this column
                for (int y = 0; y < _renderH; y++)
                {
                    int dotIndex = dotCol + (y / SS) * OutW;
                    bool rep = repCol && (y % SS) == 0; // dot's representative subsample
                    Color c;
                    if (y < drawStart)
                    {
                        if (!TexturedPlanesEnabled) c = _ceilRow[y];
                        else
                        {
                            float wx, wy;
                            Color raw = SamplePlaneRaw(_ceilTex, x, y, out wx, out wy);
                            c = LightingEnabled
                                ? ApplyDotLight(dotIndex, rep, wx, wy, 1f, 0f, 0f, -1f, raw)
                                : Scale(raw, _planeFog[y]);
                        }
                    }
                    else if (y <= drawEnd)
                    {
                        int texY = (int)((y - _renderH / 2 + lineH / 2) * texStep);
                        Color raw = tex.Sample(texX, texY);
                        if (LightingEnabled)
                        {
                            // height up the wall: 1 at top (ceiling), 0 at floor
                            float wallFrac = (y - (_renderH * 0.5f - lineH * 0.5f)) / lineH;
                            float wz = 1f - wallFrac;
                            // axis-aligned face normal: X-sides face +-X, Y-sides face +-Y
                            float nx = side == 0 ? -stepX : 0f;
                            float ny = side == 1 ? -stepY : 0f;
                            c = ApplyDotLight(dotIndex, rep, hitX, hitY, wz, nx, ny, 0f, raw);
                        }
                        else c = Scale(raw, fog);
                    }
                    else
                    {
                        if (!TexturedPlanesEnabled) c = _floorRow[y];
                        else
                        {
                            float wx, wy;
                            Color raw = SamplePlaneRaw(_floorTex, x, y, out wx, out wy);
                            c = LightingEnabled
                                ? ApplyDotLight(dotIndex, rep, wx, wy, 0f, 0f, 0f, 1f, raw)
                                : Scale(raw, _planeFog[y]);
                        }
                    }

                    _cellColor[y * _renderW + x] = c;
                }
            }

            DrawBillboards();
            EmitFrame(ctx, originX, originY, dot);
        }

        // Emit the final sprites from _cellColor: optional backlight quads first (so they
        // sit behind), then one dot per cell on top.
        void EmitFrame(RenderContext ctx, float originX, float originY, float dot)
        {
            // 1) downsample the internal SS*SS-per-dot buffer to the output dot grid
            for (int oy = 0; oy < _outH; oy++)
                for (int ox = 0; ox < OutW; ox++)
                {
                    int sx = ox * SS, sy = oy * SS;
                    Color c;
                    if (SupersampleEnabled)
                    {
                        int r = 0, g = 0, b = 0;
                        for (int j = 0; j < SS; j++)
                            for (int i = 0; i < SS; i++)
                            {
                                Color s = _cellColor[(sy + j) * _renderW + (sx + i)];
                                r += s.R; g += s.G; b += s.B;
                            }
                        int n = SS * SS;
                        c = new Color(r / n, g / n, b / n);
                    }
                    else c = _cellColor[sy * _renderW + sx]; // point sample (one corner)
                    _outColor[oy * OutW + ox] = c;
                }

            // 2) backlight: average a block of output dots into one quad drawn behind them.
            // Blocks at the right/bottom edges may be partial when the grid isn't a
            // multiple of the block size, so clamp the inner extents.
            if (BacklightEnabled)
            {
                const int B = BacklightBlock;
                for (int by = 0; by < _outH; by += B)
                    for (int bx = 0; bx < OutW; bx += B)
                    {
                        int bw = Math.Min(B, OutW - bx);
                        int bh = Math.Min(B, _outH - by);
                        int r = 0, g = 0, b = 0;
                        for (int j = 0; j < bh; j++)
                            for (int i = 0; i < bw; i++)
                            {
                                Color cc = _outColor[(by + j) * OutW + (bx + i)];
                                r += cc.R; g += cc.G; b += cc.B;
                            }
                        int n = bw * bh;
                        Color avg = Scale(new Color(r / n, g / n, b / n), BacklightGlow);
                        var quadSize = new Vector2(dot * bw, dot * bh);
                        var pos = new Vector2(originX + (bx + bw * 0.5f) * dot, originY + (by + bh * 0.5f) * dot);
                        ctx.AddSprite(new MySprite
                        {
                            Type = SpriteType.TEXTURE,
                            Data = "SquareSimple",
                            Position = pos,
                            Size = quadSize,
                            Color = avg,
                            Alignment = TextAlignment.CENTER,
                        });
                    }
            }

            // 3) dots on top
            var dotSizeVec = new Vector2(dot, dot);
            for (int y = 0; y < _outH; y++)
                for (int x = 0; x < OutW; x++)
                {
                    var pos = new Vector2(originX + x * dot + dot * 0.5f, originY + y * dot + dot * 0.5f);
                    ctx.AddSprite(new MySprite
                    {
                        Type = SpriteType.TEXTURE,
                        Data = "Circle",
                        Position = pos,
                        Size = dotSizeVec,
                        Color = _outColor[y * OutW + x],
                        Alignment = TextAlignment.CENTER,
                    });
                }
        }

        void DrawBillboards()
        {
            int n = _billboards.Length;
            for (int i = 0; i < n; i++) _spriteOrder[i] = i;

            // sort far -> near (insertion sort; n is tiny) so nearer sprites overdraw
            for (int i = 1; i < n; i++)
            {
                int cur = _spriteOrder[i];
                float curD = Dist2(_billboards[cur]);
                int j = i - 1;
                while (j >= 0 && Dist2(_billboards[_spriteOrder[j]]) < curD)
                {
                    _spriteOrder[j + 1] = _spriteOrder[j];
                    j--;
                }
                _spriteOrder[j + 1] = cur;
            }

            // inverse of the [dir | plane] camera matrix
            float invDet = 1f / (_planeX * _dirY - _dirX * _planeY);

            for (int oi = 0; oi < _spriteOrder.Length; oi++)
            {
                int idx = _spriteOrder[oi];
                Billboard b = _billboards[idx];
                float relX = b.X - _px;
                float relY = b.Y - _py;

                // transformY is depth along the view direction (same metric as zBuffer)
                float transformX = invDet * (_dirY * relX - _dirX * relY);
                float transformY = invDet * (-_planeY * relX + _planeX * relY);
                if (transformY <= 0.05f) continue; // behind the camera

                int screenX = (int)(_renderW / 2f * (1f + transformX / transformY));
                int size = (int)(_renderH / transformY); // square billboard in world terms
                if (size < 1) continue;

                int startX = -size / 2 + screenX;
                int startY = -size / 2 + _renderH / 2;
                int x0 = Math.Max(0, startX);
                int x1 = Math.Min(_renderW - 1, startX + size);
                int y0 = Math.Max(0, startY);
                int y1 = Math.Min(_renderH - 1, startY + size);

                Texture tex = _billboardTex[b.Tex];

                // light the whole billboard uniformly from its world position (cheap)
                float fog = FogFactor(transformY);
                float lr = 0, lg = 0, lb = 0;
                if (LightingEnabled) LightAt(b.X, b.Y, 0.4f, out lr, out lg, out lb);

                for (int x = x0; x <= x1; x++)
                {
                    if (transformY >= _zBuffer[x]) continue; // occluded by a closer wall
                    int texX = (int)((x - startX) * (float)tex.W / size);
                    for (int y = y0; y <= y1; y++)
                    {
                        int texY = (int)((y - startY) * (float)tex.H / size);
                        Color t = tex.Sample(texX, texY);
                        if (t.A == 0) continue; // transparent texel
                        Color lit = LightingEnabled
                            ? new Color(ClampByte(t.R * lr), ClampByte(t.G * lg), ClampByte(t.B * lb))
                            : Scale(t, fog);
                        _cellColor[y * _renderW + x] = lit;
                    }
                }
            }
        }

        float Dist2(Billboard b)
        {
            float dx = b.X - _px, dy = b.Y - _py;
            return dx * dx + dy * dy;
        }

        /// <summary>Distance haze, shared by walls and the floor/ceiling gradient.</summary>
        static float FogFactor(float dist)
        {
            float f = 1f / (1f + dist * dist * 0.04f);
            return f > 1f ? 1f : f;
        }

        static Color Scale(Color c, float f)
        {
            return new Color((int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
        }

        static int ClampByte(float v)
        {
            return v < 0 ? 0 : v > 255 ? 255 : (int)v;
        }

        // Spotlight cone attenuation: 1 inside the inner cone, smooth to 0 at the outer.
        static float SpotFactor(float spotCos, float cosInner, float cosOuter)
        {
            if (spotCos <= cosOuter) return 0f;
            if (spotCos >= cosInner) return 1f;
            float t = (spotCos - cosOuter) / (cosInner - cosOuter);
            return t * t * (3f - 2f * t); // smoothstep
        }

        // True if a wall blocks the straight segment a->b before it reaches b's cell.
        // 2D grid DDA (Amanatides-Woo): walls are full-height, so XY occlusion suffices.
        bool Occluded(float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist < 1e-4f) return false;
            float dirX = dx / dist, dirY = dy / dist;

            int mapX = (int)ax, mapY = (int)ay;
            int targetX = (int)bx, targetY = (int)by;
            float deltaX = dirX == 0 ? 1e30f : Math.Abs(1f / dirX);
            float deltaY = dirY == 0 ? 1e30f : Math.Abs(1f / dirY);

            int stepX, stepY;
            float sideX, sideY;
            if (dirX < 0) { stepX = -1; sideX = (ax - mapX) * deltaX; }
            else { stepX = 1; sideX = (mapX + 1 - ax) * deltaX; }
            if (dirY < 0) { stepY = -1; sideY = (ay - mapY) * deltaY; }
            else { stepY = 1; sideY = (mapY + 1 - ay) * deltaY; }

            while (true)
            {
                if (sideX < sideY)
                {
                    if (sideX > dist) return false; // reached the light unobstructed
                    sideX += deltaX; mapX += stepX;
                }
                else
                {
                    if (sideY > dist) return false;
                    sideY += deltaY; mapY += stepY;
                }
                if (mapX < 0 || mapY < 0 || mapX >= _mapW || mapY >= _mapH) return false;
                if (mapX == targetX && mapY == targetY) return false; // light's own cell
                int v = _map[mapY, mapX];
                if (IsDoor(v))
                {
                    // a door blocks light only where its (partially open) slab actually is,
                    // matching the rendered slab -- so an open door lets light through.
                    float open = _doorOpen[mapY, mapX];
                    if (v == DoorV && Math.Abs(dirX) > 1e-6f)
                    {
                        float tt = (mapX + 0.5f - ax) / dirX;
                        float frac = ay + tt * dirY - mapY;
                        if (tt > 0f && tt < dist && frac >= 0f && frac <= 1f - open) return true;
                    }
                    else if (v == DoorH && Math.Abs(dirY) > 1e-6f)
                    {
                        float tt = (mapY + 0.5f - ay) / dirY;
                        float frac = ax + tt * dirX - mapX;
                        if (tt > 0f && tt < dist && frac >= 0f && frac <= 1f - open) return true;
                    }
                    // otherwise the light passes through the gap: keep marching
                }
                else if (v != 0) return true;                         // wall blocks the light
            }
        }

        // Omnidirectional light at a point (no surface normal): ambient + all lights,
        // attenuated by squared distance only. Used for billboards, which face the camera.
        void LightAt(float wx, float wy, float wz, out float lr, out float lg, out float lb)
        {
            lr = lg = lb = Ambient;
            for (int i = 0; i < _lights.Length; i++)
            {
                Light l = _lights[i];
                if (ShadowsEnabled && Occluded(wx, wy, l.X, l.Y)) continue;
                float dx = wx - l.X, dy = wy - l.Y, dz = wz - l.Z; // light -> surface
                float d2 = dx * dx + dy * dy + dz * dz;
                float a = l.Intensity / (1f + d2 * l.Falloff);
                if (l.Spot)
                {
                    float spotCos = (l.DirX * dx + l.DirY * dy + l.DirZ * dz) / (float)Math.Sqrt(d2);
                    float s = SpotFactor(spotCos, l.CosInner, l.CosOuter);
                    if (s <= 0f) continue;
                    a *= s;
                }
                lr += l.R * a; lg += l.G * a; lb += l.B * a;
            }
        }

        // Diffuse light reaching an oriented surface: ambient + sum of lights, each scaled
        // by the cosine to the light (N.L) and distance attenuation. Faces turned away from
        // a light are culled before the normalize, so most pixels do few sqrts.
        void LightDiffuse(float wx, float wy, float wz,
            float nx, float ny, float nz, out float lr, out float lg, out float lb)
        {
            lr = lg = lb = Ambient;
            for (int i = 0; i < _lights.Length; i++)
            {
                Light l = _lights[i];
                float dx = l.X - wx, dy = l.Y - wy, dz = l.Z - wz; // surface -> light
                float ndotl = nx * dx + ny * dy + nz * dz;
                if (ndotl <= 0f) continue;                          // facing away
                // nudge the start just off the surface so a wall can't shadow itself
                if (ShadowsEnabled && Occluded(wx + nx * 0.02f, wy + ny * 0.02f, l.X, l.Y)) continue;
                float d2 = dx * dx + dy * dy + dz * dz;
                float invDist = 1f / (float)Math.Sqrt(d2);
                float a = ndotl * invDist * l.Intensity / (1f + d2 * l.Falloff); // cosine * atten
                if (l.Spot)
                {
                    // angle between the beam aim and the light->surface direction (= -d)
                    float spotCos = -(l.DirX * dx + l.DirY * dy + l.DirZ * dz) * invDist;
                    float s = SpotFactor(spotCos, l.CosInner, l.CosOuter);
                    if (s <= 0f) continue;
                    a *= s;
                }
                lr += l.R * a; lg += l.G * a; lb += l.B * a;
            }
        }

        // Apply diffuse light to a raw texel, computing the light multiplier once per
        // output dot. On the dot's representative subsample (rep == true) it runs the
        // full LightDiffuse and caches the multiplier; every other subsample in the dot
        // reuses the cached value. The representative is always visited first (its
        // subsample is the lowest x and y in the dot), so the cache is populated before
        // any reuse within the frame.
        Color ApplyDotLight(int dotIndex, bool rep,
            float wx, float wy, float wz, float nx, float ny, float nz, Color raw)
        {
            float lr, lg, lb;
            if (rep)
            {
                LightDiffuse(wx, wy, wz, nx, ny, nz, out lr, out lg, out lb);
                _dotLitR[dotIndex] = lr; _dotLitG[dotIndex] = lg; _dotLitB[dotIndex] = lb;
            }
            else
            {
                lr = _dotLitR[dotIndex]; lg = _dotLitG[dotIndex]; lb = _dotLitB[dotIndex];
            }
            return new Color(ClampByte(raw.R * lr), ClampByte(raw.G * lg), ClampByte(raw.B * lb));
        }

        // Rebuild the per-row floor/ceiling casting tables for the current camera.
        // A horizontal floor row at screen y maps to a constant world-space distance;
        // across the row, world position interpolates from the leftmost ray to the
        // rightmost ray. Rows above/below the horizon are symmetric (same |y-horizon|).
        void BuildPlaneTables()
        {
            float horizon = _renderH / 2f;
            float posZ = _renderH / 2f;     // camera height above the floor plane
            float r0x = _dirX - _planeX, r0y = _dirY - _planeY; // ray at screen x = 0
            float r1x = _dirX + _planeX, r1y = _dirY + _planeY; // ray at screen x = W
            float dRayX = (r1x - r0x) / _renderW;
            float dRayY = (r1y - r0y) / _renderW;

            for (int y = 0; y < _renderH; y++)
            {
                float distFromHorizon = Math.Abs(y + 0.5f - horizon);
                if (distFromHorizon < 1f) distFromHorizon = 1f;  // avoid blow-up exactly at the horizon
                float rowDist = posZ / distFromHorizon;
                _planeBaseX[y] = _px + rowDist * r0x;
                _planeBaseY[y] = _py + rowDist * r0y;
                _planeStepX[y] = rowDist * dRayX;
                _planeStepY[y] = rowDist * dRayY;
                _planeFog[y] = FogFactor(rowDist);
            }
        }

        // Raw floor/ceiling texel for compute pixel (x, y), plus the world position it
        // sampled (returned so the caller can light it per-dot). Lighting/fog are applied
        // by the caller, not here.
        Color SamplePlaneRaw(Texture tex, int x, int y, out float wx, out float wy)
        {
            wx = _planeBaseX[y] + _planeStepX[y] * x;
            wy = _planeBaseY[y] + _planeStepY[y] * x;
            int tx = (int)(tex.W * (wx - (float)Math.Floor(wx)));
            int ty = (int)(tex.H * (wy - (float)Math.Floor(wy)));
            return tex.Sample(tx, ty);
        }
    }
}
