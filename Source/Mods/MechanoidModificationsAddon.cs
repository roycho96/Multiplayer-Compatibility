using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Mechanoid Modifications Addon - Skill Level and Work modifications by Gege</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3266163248"/>
    [MpCompatFor("Gege.mechanoidmodificationsLevelAndWork")]
    public class MechanoidModificationsAddon
    {
        // SkillsHandler.Work() uses a one-entry static cache (lastPawn + lastCalledTick +
        // cachedWorkTypes). In MP's dual-simulation model the host and client can stomp each
        // other's cached values, causing divergent results for all transpiler-patched vanilla
        // methods that call through Work(). We neutralise the cache by zeroing lastPawn before
        // every call so the cache check always misses and the value is recomputed fresh.
        private static AccessTools.FieldRef<Pawn> lastPawnRef;

        public MechanoidModificationsAddon(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("MechanoidModificationsLW.SkillsHandler");
            if (type == null)
            {
                Log.Warning("MpCompat (MechanoidModificationsAddon): could not find type MechanoidModificationsLW.SkillsHandler — skipping cache patch");
                return;
            }

            var lastPawnField = AccessTools.Field(type, "lastPawn");
            if (lastPawnField == null)
            {
                Log.Warning("MpCompat (MechanoidModificationsAddon): could not find field SkillsHandler.lastPawn — skipping cache patch");
                return;
            }

            lastPawnRef = AccessTools.StaticFieldRefAccess<Pawn>(lastPawnField);

            var workMethod = AccessTools.DeclaredMethod(type, "Work");
            if (workMethod == null)
            {
                Log.Warning("MpCompat (MechanoidModificationsAddon): could not find method SkillsHandler.Work — skipping cache patch");
                return;
            }

            MpCompat.harmony.Patch(
                workMethod,
                prefix: new HarmonyMethod(typeof(MechanoidModificationsAddon), nameof(InvalidateWorkCache)));
        }

        // Runs before every SkillsHandler.Work() call.
        // Zeroing lastPawn causes the cache-hit check (lastPawn == pawn) to fail, so the
        // method always recomputes the work-type list from scratch. The computation is cheap,
        // and this is the only way to prevent divergence without touching ThreadStatic storage.
        private static void InvalidateWorkCache()
        {
            lastPawnRef() = null;
        }
    }
}
