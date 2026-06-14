using Mal.MdkModMixin.Ion.Surface;
using Mal.Mods.Utilities;
using Mal.Mods.Utilities.Notifications;
using VRage.Game.Components;

namespace Mal.Raycaster
{
    // Same wiring shape as TerminalSession: AfterSimulation drives Ion's
    // SurfaceComponent render pass, BeforeSimulation keeps the session lifecycle
    // alive. FocusManager is what lets a seated player's CUBE_ROTATE input reach
    // the raycaster. The notification channel is built from day one even though
    // nothing sends yet — it costs nothing and skipping it crashes load.
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation | MyUpdateOrder.AfterSimulation)]
    public class RaycasterSession : ModSession
    {
        protected override void Configure(Builder builder)
        {
            builder.UseNotifications()
                .Using<SurfaceComponent>()
                .Using<FocusManager>();
        }

        protected override void OnBeforeStart()
        {
            NotificationsDriverComponent.EnsureChannelBuilt();
        }
    }
}
