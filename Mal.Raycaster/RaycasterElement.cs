using Mal.MdkModMixin.Ion;
using Mal.MdkModMixin.Ion.Surface;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Mal.Raycaster
{
    /// <summary>
    ///     Hosts the <see cref="Raycaster" /> inside Ion's element tree. Fills its slot,
    ///     advances the simulation off the render clock (<see cref="ITickAnimation" />),
    ///     and feeds held movement keys to it when focused (<see cref="ITickInput" />).
    ///     <para>
    ///     The six CUBE_ROTATE menu inputs map one-to-one onto the raycaster's moves:
    ///     Up/Down = forward/back, Left/Right = turn, RollLeft/RollRight = strafe.
    ///     </para>
    /// </summary>
    public sealed class RaycasterElement : FocusableElement, ITickAnimation, ITickInput
    {
        readonly Raycaster _raycaster = new Raycaster();
        Raycaster.Input _input;

        public RaycasterElement()
        {
            Width = SizeConstraint.Fill;
            Height = SizeConstraint.Fill;
            // Drive a continuous animation loop: Tick re-requests itself every frame.
            RequestTick();
        }

        protected override Vector2 MeasureContent(IMyTextSurface surface, Vector2 availableSize)
        {
            return availableSize;
        }

        public void Tick(float deltaSeconds)
        {
            _raycaster.Update(deltaSeconds, _input);
            // Movement keys are re-sampled fresh on every focused input tick; clearing
            // here means movement stops the moment focus (and thus TickInput) drops.
            _input = default(Raycaster.Input);
            InvalidateRender();
            RequestTick();
        }

        public void TickInput(IInputProbe probe)
        {
            _input.Forward = probe.IsHeld(MenuInput.Up);
            _input.Back = probe.IsHeld(MenuInput.Down);
            _input.TurnLeft = probe.IsHeld(MenuInput.Left);
            _input.TurnRight = probe.IsHeld(MenuInput.Right);
            _input.StrafeLeft = probe.IsHeld(MenuInput.RollLeft);
            _input.StrafeRight = probe.IsHeld(MenuInput.RollRight);
        }

        protected override void DrawContent(RenderContext ctx, RectangleF bounds)
        {
            _raycaster.Render(ctx, bounds);
        }
    }
}
