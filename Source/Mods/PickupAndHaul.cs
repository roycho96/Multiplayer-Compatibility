using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Pick Up And Haul by Mehni</summary>
    /// <see href="https://github.com/Mehni/PickUpAndHaul/"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=1279012058"/>
    [MpCompatFor("Mehni.PickUpAndHaul")]
    public class PickupAndHaul
    {
        public PickupAndHaul(ModContentPack mod)
        {
            // Sorts the ListerHaulables list from UI, causes issues
            MpCompat.harmony.Patch(AccessTools.Method("PickUpAndHaul.WorkGiver_HaulToInventory:PotentialWorkThingsGlobal"),
                transpiler: new HarmonyMethod(typeof(PickupAndHaul), nameof(Transpiler)));

            // Desync-18: DropUnusedInventory_PostFix → CheckIfPawnShouldUnloadInventory
            // calls JobMaker.MakeJob → GetNextJobID inside command execution
            // (Reserve → EndCurrentJob → TryFindAndStartJob cascade).
            // Rand and UniqueID consumption must be isolated.
            PatchingUtilities.PatchPushPopRand("PickUpAndHaul.PawnUnloadChecker:CheckIfPawnShouldUnloadInventory");

            // Desync-23: WorkGiver_HaulToInventory.JobOnThing calls
            // StoreUtility.TryFindBestBetterStoreCellForWorker → Rand.Range
            // inside command execution (set_Drafted → TryFindAndStartJob).
            // When a different WorkGiver wins on each side, Rand consumption
            // diverges (host 2 calls vs local 10 calls in observed traces).
            PatchingUtilities.PatchPushPopRand("PickUpAndHaul.WorkGiver_HaulToInventory:JobOnThing");
            PatchingUtilities.PatchPushPopRand("PickUpAndHaul.WorkGiver_HaulToInventory:HasJobOnThing");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instr)
        {
            var target = AccessTools.Method(typeof(ListerHaulables), nameof(ListerHaulables.ThingsPotentiallyNeedingHauling));
            var newListCtor = AccessTools.Constructor(typeof(List<Thing>), new[] { typeof(IEnumerable<Thing>) });

            var patched = false;
            foreach (var ci in instr)
            {
                yield return ci;

                if (ci.opcode == OpCodes.Callvirt && ci.operand is MethodInfo method && method == target)
                {
                    yield return new CodeInstruction(OpCodes.Newobj, newListCtor);
                    patched = true;
                }
            }

            if (!patched)
                throw new Exception("Failed patching Pickup and Haul");
        }
    }
}