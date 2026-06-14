using Mal.MdkModMixin.Ion;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock = VRage.Game.ModAPI.Ingame.IMyCubeBlock;

namespace Mal.Raycaster
{
    /// <summary>
    ///     A Wolfenstein-style raycaster rendered as a grid of dot sprites, picked from
    ///     a surface's script dropdown as "Raycaster". The renderer (see
    ///     <see cref="Raycaster" />) is straight C# and only emits sprites, so it drops
    ///     onto any <see cref="IMyTextSurface" />. Movement only works on a block that
    ///     can receive menu input (a cockpit/seat); a wall LCD just shows the scene.
    /// </summary>
    [MyTextSurfaceScript("Mal_Raycaster_Main", "Raycaster")]
    public class RaycasterApp : IonApp
    {
        public RaycasterApp(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            var page = new Page(block, surface);
            // The dots are the picture edge-to-edge; the player's text-padding
            // slider shouldn't carve a border out of the scene.
            page.UseSurfacePadding = false;
            page.Root = new RaycasterElement();
            CurrentPage = page;
        }
    }
}
