using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>WVC - Work Modes by SergKart</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2888380373"/>
    [MpCompatFor("wvc.sergkart.biotech.MoreMechanoidsWorkModes")]
    public class WvcWorkModes
    {
        public WvcWorkModes(ModContentPack mod)
        {
            LongEventHandler.ExecuteWhenFinished(LatePatch);
        }

        private static void LatePatch()
        {
            // --- Namespace is WVC_WorkModes (NOT WVC_MoreMechanoidsWorkModes) ---
            // Confirmed via DLL strings inspection of WVC_MoreMechanoidsWorkModes_Main.dll

            // CompMechSettings.CompGetGizmosExtra
            // Iterator: <CompGetGizmosExtra>d__8
            // DLL shows 6 lambdas: b__8_0, b__8_1, b__2, b__8_3, b__8_4, b__8_5
            // These set workMode and mechGroup fields on the comp from gizmo clicks.
            // NOTE: ordinal mapping may not be sequential due to mixed instance/static
            // lambda naming (b__2 vs b__8_N). Using 0-5 as best-effort; if a specific
            // ordinal fails at runtime, check with decompiler and adjust.
            MpCompat.RegisterLambdaMethod("WVC_WorkModes.CompMechSettings", "CompGetGizmosExtra",
                0, 1, 2, 3, 4, 5);

            // CompSmartEscort — escortTarget (Pawn) assigned via gizmo using
            // Find.Selector/SelectedPawns (per-client UI state).
            MP.RegisterSyncField(
                AccessTools.TypeByName("WVC_WorkModes.CompSmartEscort"),
                "escortTarget");

            // Zone_MechanoidShutdown.GetGizmos
            // Iterator: d__17
            // DLL shows lambdas: b__17_0, b__17_2, b__17_3, b__17_4, b__17_5, b__17_6
            // Note gap at ordinal 1 — RegisterLambdaMethod uses sequential indexing
            // so we register 0-5 which maps to the 6 existing lambdas in order.
            MpCompat.RegisterLambdaMethod("WVC_WorkModes.Zone_MechanoidShutdown", "GetGizmos",
                0, 1, 2, 3, 4, 5);

            // Zone_MechanoidScavenge.GetGizmos
            // Iterator: d__10. DLL only shows b__1 as a single visible lambda.
            // Conservative: register only ordinal 0.
            // If more gizmos exist in GetZoneAddGizmos (d__11), they should be
            // added here after decompiler verification.
            MpCompat.RegisterLambdaMethod("WVC_WorkModes.Zone_MechanoidScavenge", "GetGizmos",
                0);

            // Command_Hide_ZoneShutdown
            // DLL shows lambdas under GetHideOptions (d__3): b__3_0, b__3_1
            // NOT under ProcessInput.
            MpCompat.RegisterLambdaMethod("WVC_WorkModes.Command_Hide_ZoneShutdown", "GetHideOptions",
                0, 1);
        }
    }
}
