using System;
using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Furniture Expanded - Power by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2062943477"/>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaFurnitureExpanded-Power"/>
    /// Contribution to Multiplayer Compatibility by Sokyran and Reshiram
    [MpCompatFor("VanillaExpanded.VFEPower")]
    class VanillaPowerExpanded
    {
        public VanillaPowerExpanded(ModContentPack mod)
        {
            // Violence generator
            var type = AccessTools.TypeByName("VanillaPowerExpanded.CompSoulsPowerPlant");
            MpCompat.RegisterLambdaMethod(type, "CompGetGizmosExtra", 1); // Toggle on/off

            // Nuclear generator (VPE_NuclearGenerator) — CompPowerPlantNuclear.CompTick
            // accesses Map/position data that can be null during despawning.
            // Without this finalizer the NRE escapes Thing.DoTick which is wrapped
            // by SeededPushPrefixThing; the postfix PopState never runs → Rand stack leak.
            var nuclearCompType = AccessTools.TypeByName("VanillaPowerExpanded.CompPowerPlantNuclear");
            if (nuclearCompType != null)
            {
                var compTickMethod = AccessTools.Method(nuclearCompType, "CompTick");
                if (compTickMethod != null)
                    MpCompat.harmony.Patch(compTickMethod, finalizer: new HarmonyMethod(typeof(VanillaPowerExpanded), nameof(NuclearCompTickFinalizer)));
                else
                    Log.Warning("[Multiplayer] VanillaPowerExpanded.CompPowerPlantNuclear.CompTick not found — skipping NRE guard.");
            }
            else
                Log.Warning("[Multiplayer] VanillaPowerExpanded.CompPowerPlantNuclear not found — skipping NRE guard.");
        }

        /// <summary>
        /// Finalizer for CompPowerPlantNuclear.CompTick.
        /// Swallows NRE thrown when the nuclear generator's parent thing has a null
        /// Map (despawning), which would otherwise leave Rand.PushState unmatched.
        /// </summary>
        static Exception NuclearCompTickFinalizer(Exception __exception, ThingComp __instance)
        {
            if (__exception is NullReferenceException && __instance?.parent?.Map == null)
            {
                Log.WarningOnce(
                    $"[Multiplayer] Suppressed NRE in CompPowerPlantNuclear.CompTick for {__instance?.parent} (Map is null — despawning). Rand stack protected.",
                    (__instance?.parent?.thingIDNumber ?? 0) ^ 0x4B2E9F1);
                return null;
            }

            return __exception;
        }
    }
}