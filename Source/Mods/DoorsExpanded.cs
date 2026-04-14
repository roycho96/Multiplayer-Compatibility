using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Doors Expanded by Jecrell</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3532342422"/>
    [MpCompatFor("jecrell.doorsexpanded")]
    public class DoorsExpanded
    {
        private static System.Type doorRemoteType;
        private static PropertyInfo buttonProperty;
        private static MethodInfo updateOpenStateMethod;

        public DoorsExpanded(ModContentPack mod)
        {
            doorRemoteType = AccessTools.TypeByName("DoorsExpanded.Building_DoorRemote");
            var buttonType = AccessTools.TypeByName("DoorsExpanded.Building_DoorRemoteButton");

            // Cache reflection for SyncedButtonConnect (avoid per-call overhead).
            buttonProperty = doorRemoteType.GetProperty("Button");
            updateOpenStateMethod = AccessTools.Method(doorRemoteType, "UpdateOpenStateFromButtonEvent");

            // Building_DoorRemote gizmos
            // - "Secured Remotely" toggle: toggleAction = () => SecuredRemotely = !SecuredRemotely
            //   ordinal 0 = isActive (read-only), ordinal 1 = toggleAction (mutates state)
            MpCompat.RegisterLambdaMethod("DoorsExpanded.Building_DoorRemote", "GetGizmos", 1);

            // - "Disconnect" gizmo calls ButtonDisconnect() — simple method, sync directly.
            MP.RegisterSyncMethod(doorRemoteType, "ButtonDisconnect");

            // - "Connect" gizmo calls ButtonConnect() which opens Find.Targeter.BeginTargeting.
            //   The lambda captures `this` and is compiled as an instance method on the parent
            //   type (not a nested display class), so RegisterLambdaDelegate fails.
            //   Fix: prefix ButtonConnect to intercept in MP, redirect through a synced method.
            MP.RegisterSyncMethod(typeof(DoorsExpanded), nameof(SyncedButtonConnect));
            MpCompat.harmony.Patch(
                AccessTools.Method(doorRemoteType, "ButtonConnect"),
                prefix: new HarmonyMethod(typeof(DoorsExpanded), nameof(PreButtonConnect)));

            // Building_DoorRemoteButton gizmos
            // - "Use Button/Lever" toggle: () => NeedsToBeSwitched = !NeedsToBeSwitched
            MP.RegisterSyncField(buttonType, "needsToBeSwitched");
        }

        /// <summary>
        /// Prefix on ButtonConnect: in MP, replace the targeter flow with a sync-safe version.
        /// The targeter UI still runs locally, but the callback routes through SyncedButtonConnect.
        /// </summary>
        private static bool PreButtonConnect(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var door = __instance;
            var tp = new TargetingParameters
            {
                validator = t => t.Thing != null && doorRemoteType != null
                    && AccessTools.TypeByName("DoorsExpanded.Building_DoorRemoteButton")
                        .IsAssignableFrom(t.Thing.GetType()),
                canTargetBuildings = true,
                canTargetPawns = false,
            };
            Find.Targeter.BeginTargeting(tp, t =>
            {
                if (t.Thing != null)
                    SyncedButtonConnect(door as Thing, t.Thing);
            }, null);
            return false;
        }

        /// <summary>
        /// Synced method: assigns the button to the door and updates state.
        /// Both parameters are Things — MP serializes by thingID.
        /// </summary>
        private static void SyncedButtonConnect(Thing door, Thing targetButton)
        {
            if (door == null || targetButton == null) return;
            if (buttonProperty == null) return;

            var currentButton = buttonProperty.GetValue(door);
            if (currentButton != targetButton)
            {
                buttonProperty.SetValue(door, targetButton);
                updateOpenStateMethod?.Invoke(door, null);
            }
        }
    }
}
