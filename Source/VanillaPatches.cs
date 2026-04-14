using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>
    /// Vanilla RimWorld determinism fixes for cross-CPU multiplayer desyncs.
    /// These patch vanilla code that the core MP mod does not yet cover.
    /// </summary>
    public static class VanillaPatches
    {
        public static void Apply()
        {
            // CRITICAL: every phase MUST be isolated in its own try/catch.
            // Without this, a single bad patch target (e.g. a virtual method
            // not implemented on the subclass, as happened with
            // HediffGiver_Birthday in Desync-14) throws out of Apply() and
            // leaves every subsequent phase unpatched — which is exactly how
            // the entire STEP 9/10/11 defense disappeared in Desync-14.
            TryPatch("PatchFrameConstruction",        PatchFrameConstruction);
            TryPatch("PatchThinkNodePrioritySorter",  PatchThinkNodePrioritySorter);
            TryPatch("PatchHediffGivers",             PatchHediffGivers);
            TryPatch("PatchTickIsolation",            PatchTickIsolation);
            // STEP 12 rollback test (Desync-20) confirmed STEP 12 is required:
            // disabling Thing.DoTick seeded isolation brought back the classic
            // Rand cascade (CompSpawnerFilth → Pawn_FilthTracker → 1-call
            // drift). Re-enabled. Desync-18/19 MapComponent iteration issues
            // are independent and need their own fixes (below).
            TryPatch("PatchThingDoTickSeeded",        PatchThingDoTickSeeded);
            TryPatch("PatchFishShadowComponent",      PatchFishShadowComponent);
            // STEP 14c — One-time deterministic sort of BreakdownManager /
            // PowerNet internal lists, triggered from inside the existing
            // Thing.DoTick prefix (STEP 12). NO new Harmony.Patch calls on
            // MapComponentTick / PowerNetTick — those were confirmed to hang
            // the client loader in STEP 14a/14b regardless of guard strategy.
            // This step only pre-caches FieldInfo at startup.
            TryPatch("InitDeterministicSortFields",   InitDeterministicSortFields);
            TryPatch("PatchCompBreakdownableEntitySeed", PatchCompBreakdownableEntitySeed);
            TryPatch("PatchMapPostTickSeeded",        PatchMapPostTickSeeded);
            TryPatch("PatchGenLeavingsDoLeavingsFor", PatchGenLeavingsDoLeavingsFor);
            TryPatch("PatchDetermineNextJobPushPopRand", PatchDetermineNextJobPushPopRand);
            TryPatch("PatchDesignatorInstallNullGuard", PatchDesignatorInstallNullGuard);
            TryPatch("PatchDesignatorCancelNullGuard",  PatchDesignatorCancelNullGuard);
        }

        /// <summary>
        /// STEP 17 — seal <c>Verse.Map.MapPostTick</c> in a per-map per-tick
        /// seeded Rand scope. Desync-27 confirmed that STEP 12's
        /// <c>Thing.DoTick</c> seeded wrapper only isolates Thing ticks; all
        /// MapComponent ticks (PowerNetManager, BreakdownManager,
        /// WeatherManager, GameConditionManager, ...) run OUTSIDE it and
        /// still consume the global Rand stream. Between two PowerNet
        /// RandomElement samples, local drifted 2 Rand calls ahead of host
        /// — those 2 calls happened inside map-level code that our STEP 12
        /// shield does not cover.
        ///
        /// <para>Important: <c>Map.MapPostTick</c> is <b>already patched by
        /// the Multiplayer mod</b> (visible in every stack as
        /// <c>Map.MapPostTick_Patch1</c>). Adding another prefix/postfix
        /// goes through Harmony's existing patch chain and does NOT create
        /// a fresh MonoMod detour — this is believed safer than the STEP
        /// 14a/14b attempts which patched <i>virgin</i> methods
        /// (<c>BreakdownManager.MapComponentTick</c>,
        /// <c>PowerNet.PowerNetTick</c>) and reproducibly hung the client
        /// loader.</para>
        ///
        /// <para>Seed: <c>HashCombineInt(map.uniqueID, TicksGame)</c>. Each
        /// map's tick gets a unique stream; no herd behavior. All Rand
        /// calls inside the scope (every MapComponent tick + every direct
        /// Map.MapPostTick Rand call) are isolated from the outer global
        /// state.</para>
        ///
        /// <para>Rollback guidance: if this re-introduces the 90% client
        /// load hang, comment out the <c>TryPatch</c> call in
        /// <see cref="Apply"/>. STEP 12 seeded Thing.DoTick alone is still
        /// in effect.</para>
        /// </summary>
        private static void PatchMapPostTickSeeded()
        {
            var mapType = AccessTools.TypeByName("Verse.Map");
            if (mapType == null)
            {
                Log.Warning("MPCompat :: Verse.Map not found — MapPostTick seal skipped");
                return;
            }
            var method = AccessTools.Method(mapType, "MapPostTick");
            if (method == null)
            {
                Log.Warning("MPCompat :: Verse.Map.MapPostTick not found — seal skipped");
                return;
            }
            MpCompat.harmony.Patch(method,
                prefix:  new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(MapPostTickSeededPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(MapPostTickSeededPostfix))));
            Log.Message("MPCompat :: Verse.Map.MapPostTick — seeded PushPopRand seal installed (STEP 17, Desync-27 fix)");
        }

        public static void MapPostTickSeededPrefix(Map __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (Current.ProgramState != ProgramState.Playing) return;
                if (__instance == null) return;
                int seed = Gen.HashCombineInt(__instance.uniqueID, Find.TickManager?.TicksGame ?? 0)
                           ^ unchecked((int)0x7F3A9E11);
                Rand.PushState(seed);
                __state = true;
            }
            catch (Exception e)
            {
                if (__state) { try { Rand.PopState(); } catch { } __state = false; }
                Log.Warning($"MPCompat :: MapPostTickSeededPrefix — {e.GetType().Name}: {e.Message}");
            }
        }

        public static void MapPostTickSeededPostfix(bool __state)
        {
            if (!__state) return;
            try { Rand.PopState(); }
            catch (Exception e) { Log.Warning($"MPCompat :: MapPostTickSeededPostfix — {e.GetType().Name}: {e.Message}"); }
        }

        /// <summary>
        /// STEP 14d — entity-seeded Rand isolation for
        /// <c>CompBreakdownable.CheckForBreakdown</c>. Desync-22 showed that
        /// STEP 14c's "sort BreakdownManager.brokenDownThings" fix was
        /// targeting the wrong field (brokenDownThings is the list of
        /// already-broken things, not the iteration target in
        /// MapComponentTick). The real iteration is over an external shared
        /// list (likely listerBuildings.allBuildingsColonist), which we can't
        /// safely sort because other callers depend on its natural order.
        ///
        /// Instead, wrap <c>CheckForBreakdown</c> with a per-thing-per-tick
        /// seeded PushPopRand. The breakdown decision now depends on
        /// <c>(thing.thingIDNumber, TicksGame)</c>, making it independent of
        /// iteration order. Host and client can iterate the same things in
        /// different orders and still converge on the same break/no-break
        /// decisions per thing.
        ///
        /// <para>Patching <c>CompBreakdownable.CheckForBreakdown</c> is
        /// expected to be safe because <c>CompBreakdownable</c> is a
        /// ThingComp — not a MapComponent / PowerNet whose patching caused
        /// the STEP 14a/14b client-loader hang.</para>
        /// </summary>
        private static void PatchCompBreakdownableEntitySeed()
        {
            var type = AccessTools.TypeByName("RimWorld.CompBreakdownable");
            if (type == null)
            {
                Log.Warning("MPCompat :: CompBreakdownable not found — entity-seed patch skipped");
                return;
            }
            var method = AccessTools.Method(type, "CheckForBreakdown");
            if (method == null)
            {
                Log.Warning("MPCompat :: CompBreakdownable.CheckForBreakdown not found — entity-seed patch skipped");
                return;
            }
            if (method.IsAbstract)
            {
                Log.Warning("MPCompat :: CompBreakdownable.CheckForBreakdown is abstract — entity-seed patch skipped");
                return;
            }

            MpCompat.harmony.Patch(method,
                prefix:  new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(EntitySeededPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(EntitySeededPostfix))));
            Log.Message("MPCompat :: CompBreakdownable.CheckForBreakdown — entity-seeded Rand prefix/postfix installed (Desync-22 fix)");
        }

        /// <summary>
        /// Generic per-entity seeded Rand prefix for ThingComp methods that
        /// are called from an iteration loop where iteration order differs
        /// across MP clients. The seed is derived from the containing
        /// thing's stable <c>thingIDNumber</c> and current <c>TicksGame</c>,
        /// xor'd with a constant to avoid colliding with the
        /// <see cref="SeededPushPrefixThing"/> seed domain.
        /// </summary>
        public static void EntitySeededPrefix(ThingComp __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (Current.ProgramState != ProgramState.Playing) return;
                int id = __instance?.parent?.thingIDNumber ?? 0;
                int tick = (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
                // XOR with a distinct constant so per-entity-per-tick seeds
                // used here don't collide with Thing.DoTick seeds (STEP 12)
                // for the same (id, tick) pair.
                int seed = Gen.HashCombineInt(id, tick) ^ unchecked((int)0xBD4F3A21);
                Rand.PushState(seed);
                __state = true;
            }
            catch (Exception e)
            {
                if (__state) { try { Rand.PopState(); } catch { } __state = false; }
                Log.Warning($"MPCompat :: EntitySeededPrefix — {e.GetType().Name}: {e.Message}");
            }
        }

        public static void EntitySeededPostfix(bool __state)
        {
            if (!__state) return;
            try { Rand.PopState(); }
            catch (Exception e) { Log.Warning($"MPCompat :: EntitySeededPostfix — {e.GetType().Name}: {e.Message}"); }
        }

        /// <summary>
        /// Odyssey's <c>FishShadowComponent.MapComponentTick</c> calls
        /// <c>GenCollection.RandomElement&lt;IntVec3&gt;(HashSet&lt;IntVec3&gt;)</c>
        /// on a <c>WaterBody</c>'s cell set to spawn cosmetic fish shadow
        /// flecks. The Rand index matches across clients (confirmed by
        /// Desync-18 trace hashes being byte-identical), but the
        /// <c>HashSet&lt;IntVec3&gt;</c> enumeration order is not stable
        /// across the two processes — same index picks different cells →
        /// different fleck positions → map state hash diverges, while every
        /// Rand trace remains in perfect lockstep.
        ///
        /// The feature is purely visual (fleck motes fading in/out on water
        /// tiles). Skipping the tick has zero gameplay impact and removes the
        /// only known non-Rand desync source. If the component type is not
        /// present (Odyssey not installed), this patch is a no-op.
        /// </summary>
        private static void PatchFishShadowComponent()
        {
            var type = AccessTools.TypeByName("Verse.FishShadowComponent")
                       ?? AccessTools.TypeByName("FishShadowComponent");
            if (type == null)
            {
                Log.Message("MPCompat :: FishShadowComponent not found (Odyssey not installed) — skipping desync fix");
                return;
            }

            var tick = AccessTools.Method(type, "MapComponentTick");
            if (tick == null)
            {
                Log.Warning("MPCompat :: FishShadowComponent found but MapComponentTick not — skipping desync fix");
                return;
            }

            TryPatch("FishShadowComponent.MapComponentTick skip", () =>
            {
                MpCompat.harmony.Patch(tick,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(SkipOriginalPrefix))));
                Log.Message("MPCompat :: Disabled FishShadowComponent.MapComponentTick — cosmetic, caused HashSet-order map desync (Desync-18)");
            });
        }

        /// <summary>Harmony prefix that unconditionally skips the original method.</summary>
        public static bool SkipOriginalPrefix() => false;

        // ==== STEP 14c — Deterministic collection sort without new patches ====
        //
        // Rationale: Desync-19 (BreakdownManager iteration) and Desync-21
        // (PowerNet iteration) are caused by host/client seeing the same
        // Rand state but picking different elements from a collection at
        // the same index because the collection's iteration order differs.
        // STEP 14a/14b tried to fix this by adding Harmony prefixes on
        // BreakdownManager.MapComponentTick / PowerNet.PowerNetTick — both
        // variants reproducibly hung the client loader at ~90% load. The
        // root cause was not pinned down (likely a Harmony detour / Unity
        // ReflectionOnly scan interaction specific to those methods), but
        // the workaround is clear: do NOT install new Harmony patches on
        // those methods. Instead, piggyback on the existing Thing.DoTick
        // prefix (STEP 12) to run a one-time sort of the map's problematic
        // collections the first time any Thing ticks on that map. With
        // STEP 12 in place the game is fully deterministic, so the
        // initially-sorted order is preserved across subsequent ticks (List
        // Add/Remove preserve relative order on both sides identically).

        private static readonly HashSet<int> sortedMapIds = new HashSet<int>();

        // Cached reflection targets (resolved once at startup by InitDeterministicSortFields).
        private static Type breakdownManagerType;
        private static FieldInfo breakdownManager_brokenDownThings;
        private static Type powerNetType;
        private static FieldInfo powerNet_transmitters;
        private static FieldInfo powerNet_connectors;
        private static FieldInfo powerNet_batteryComps;
        private static FieldInfo powerNet_powerComps;
        private static FieldInfo powerNet_partsWantingPowerOn;
        private static Type powerNetManagerType;
        private static FieldInfo powerNetManager_allNetsList;

        private static void InitDeterministicSortFields()
        {
            int resolved = 0;

            breakdownManagerType = AccessTools.TypeByName("RimWorld.BreakdownManager");
            if (breakdownManagerType != null)
            {
                breakdownManager_brokenDownThings = AccessTools.Field(breakdownManagerType, "brokenDownThings");
                if (breakdownManager_brokenDownThings != null) resolved++;
                else Log.Warning("MPCompat :: BreakdownManager.brokenDownThings field not found");
            }

            powerNetType = AccessTools.TypeByName("RimWorld.PowerNet");
            if (powerNetType != null)
            {
                powerNet_transmitters       = AccessTools.Field(powerNetType, "transmitters");
                powerNet_connectors         = AccessTools.Field(powerNetType, "connectors");
                powerNet_batteryComps       = AccessTools.Field(powerNetType, "batteryComps");
                powerNet_powerComps         = AccessTools.Field(powerNetType, "powerComps");
                powerNet_partsWantingPowerOn = AccessTools.Field(powerNetType, "partsWantingPowerOn");
                foreach (var f in new[] { powerNet_transmitters, powerNet_connectors, powerNet_batteryComps, powerNet_powerComps, powerNet_partsWantingPowerOn })
                    if (f != null) resolved++;
            }

            powerNetManagerType = AccessTools.TypeByName("RimWorld.PowerNetManager");
            if (powerNetManagerType != null)
            {
                // Try known field names for the all-nets list across 1.x/1.6 variants
                powerNetManager_allNetsList = AccessTools.Field(powerNetManagerType, "allNets")
                                              ?? AccessTools.Field(powerNetManagerType, "powerNets")
                                              ?? AccessTools.Field(powerNetManagerType, "nets");
                if (powerNetManager_allNetsList != null) resolved++;
                else Log.Warning("MPCompat :: PowerNetManager all-nets list field not found (tried allNets/powerNets/nets)");
            }

            Log.Message($"MPCompat :: Deterministic sort fields cached ({resolved} resolved). One-time per-map sort will run on first Thing.DoTick.");
        }

        /// <summary>
        /// One-time deterministic sort of a map's non-Rand divergence
        /// sources. Called from <see cref="SeededPushPrefixThing"/> the first
        /// time any Thing on the given map ticks in Playing state. After
        /// this runs once per map, subsequent tick operations (List Add at
        /// tail, Remove preserving order) preserve the sorted ordering on
        /// both host and client identically — assuming STEP 12 keeps the
        /// game fully deterministic.
        /// </summary>
        private static void TrySortMapCollectionsOnce(Map map)
        {
            if (map == null) return;
            try
            {
                int bdmSortedCount = 0;
                int netsSortedCount = 0;

                // ---- BreakdownManager.brokenDownThings ----
                if (breakdownManagerType != null && breakdownManager_brokenDownThings != null)
                {
                    var bdm = map.components?.FirstOrDefault(c => breakdownManagerType.IsInstanceOfType(c));
                    if (bdm != null)
                    {
                        if (breakdownManager_brokenDownThings.GetValue(bdm) is System.Collections.IList list && list.Count > 1)
                        {
                            bdmSortedCount = TrySortIListOfThings(list);
                        }
                    }
                }

                // ---- All PowerNets on the map ----
                if (powerNetManagerType != null && powerNetManager_allNetsList != null && powerNetType != null)
                {
                    // Access map.powerNetManager via publicized field/property
                    var pnm = AccessTools.Field(typeof(Map), "powerNetManager")?.GetValue(map)
                              ?? AccessTools.Property(typeof(Map), "powerNetManager")?.GetValue(map);
                    if (pnm != null && powerNetManager_allNetsList.GetValue(pnm) is System.Collections.IEnumerable nets)
                    {
                        foreach (var net in nets)
                        {
                            if (net == null) continue;
                            SortPowerNetListsCached(net);
                            netsSortedCount++;
                        }
                    }
                }

                if (bdmSortedCount > 0 || netsSortedCount > 0)
                    Log.Message($"MPCompat :: Map {map.uniqueID} — sorted BreakdownManager ({bdmSortedCount} things) + {netsSortedCount} PowerNets");
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: TrySortMapCollectionsOnce for map {map.uniqueID} — {e.GetType().Name}: {e.Message}");
            }
        }

        private static void SortPowerNetListsCached(object net)
        {
            SortCompPowerList(powerNet_transmitters,         net);
            SortCompPowerList(powerNet_connectors,           net);
            SortCompPowerList(powerNet_batteryComps,         net);
            SortCompPowerList(powerNet_powerComps,           net);
            SortCompPowerList(powerNet_partsWantingPowerOn,  net);
        }

        private static void SortCompPowerList(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return;
            try
            {
                var list = field.GetValue(instance) as System.Collections.IList;
                if (list == null || list.Count < 2) return;
                var comps = new List<ThingComp>(list.Count);
                foreach (var item in list)
                {
                    if (!(item is ThingComp c) || c.parent == null) return;
                    comps.Add(c);
                }
                comps.Sort(CompByParentIdComparer);
                for (int i = 0; i < comps.Count; i++) list[i] = comps[i];
            }
            catch { /* swallow — best-effort */ }
        }

        private static int TrySortIListOfThings(System.Collections.IList list)
        {
            try
            {
                var things = new List<Thing>(list.Count);
                foreach (var item in list)
                {
                    if (!(item is Thing t)) return 0;
                    things.Add(t);
                }
                things.Sort(ThingIdComparer);
                for (int i = 0; i < things.Count; i++) list[i] = things[i];
                return things.Count;
            }
            catch { return 0; }
        }
        // ThingIdComparer + CompByParentIdComparer are reused from the STEP 14b
        // declarations above — do not re-declare here.

        /// <summary>
        /// Deterministic iteration fix for MapComponent/collection-iterating
        /// methods. Desync-19 (BreakdownManager) and Desync-21 (PowerNet) show
        /// the same pattern: trace hashes identical (Rand fully deterministic
        /// thanks to STEP 12), but <c>GenCollection.RandomElement</c> /
        /// per-item iteration on an unordered collection returns different
        /// elements between host and local at the same index, drifting map
        /// state without drifting Rand.
        ///
        /// This implementation is designed to be <b>impossible to hang the
        /// client loader</b>. Key differences from the earlier reflection-
        /// based <c>SortThingListFieldsPrefix</c>:
        /// <list type="bullet">
        /// <item>All <see cref="FieldInfo"/> lookups happen <b>once, at patch
        ///   time</b>. Runtime prefix has zero reflection.</item>
        /// <item>Each target has an explicit, typed sort lambda. No generic
        ///   field enumeration, no walking the type hierarchy at runtime.</item>
        /// <item>Every runtime call is guarded by
        ///   <c>Current.ProgramState == Playing</c> and wrapped in try/catch.
        ///   Any failure logs once and leaves the collection alone.</item>
        /// </list>
        /// </summary>
        private static readonly List<Action> DeterministicSortActions = new List<Action>();

        private static void PatchDeterministicIteration()
        {
            // ---------- BreakdownManager (Desync-19) ----------
            RegisterThingListSort(
                typeName: "RimWorld.BreakdownManager",
                tickMethodName: "MapComponentTick",
                fieldName: "brokenDownThings",
                label: "BreakdownManager.brokenDownThings");

            // ---------- PowerNet (Desync-21) ----------
            // PowerNet.PowerNetTick iterates power comps and picks via
            // GenCollection.RandomElement. Vanilla fields that may need
            // sorting: transmitters, connectors, batteryComps, powerComps,
            // partsWantingPowerOn. Elements are CompPower (or subclasses)
            // which have a .parent (Thing) with a stable thingIDNumber.
            RegisterCompPowerListSort("RimWorld.PowerNet", "PowerNetTick", "transmitters");
            RegisterCompPowerListSort("RimWorld.PowerNet", "PowerNetTick", "connectors");
            RegisterCompPowerListSort("RimWorld.PowerNet", "PowerNetTick", "batteryComps");
            RegisterCompPowerListSort("RimWorld.PowerNet", "PowerNetTick", "powerComps");
            RegisterCompPowerListSort("RimWorld.PowerNet", "PowerNetTick", "partsWantingPowerOn");

            if (DeterministicSortActions.Count > 0)
                Log.Message($"MPCompat :: Deterministic-iteration sorts registered: {DeterministicSortActions.Count}");
        }

        /// <summary>
        /// Register a sort-before-tick for a Thing list on a MapComponent-
        /// like target. Resolves FieldInfo once at patch time and captures it
        /// in a closure for the prefix.
        /// </summary>
        private static void RegisterThingListSort(string typeName, string tickMethodName, string fieldName, string label)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) { Log.Warning($"MPCompat :: {typeName} not found — sort skipped"); return; }
            var tick = AccessTools.Method(type, tickMethodName);
            if (tick == null) { Log.Warning($"MPCompat :: {typeName}.{tickMethodName} not found — sort skipped"); return; }
            var field = AccessTools.Field(type, fieldName);
            if (field == null) { Log.Warning($"MPCompat :: {typeName}.{fieldName} not found — sort skipped"); return; }

            // Build a closure that sorts this specific field when called with
            // an instance of the type. Prefix retrieves the right closure via
            // the registration index.
            int slot = DeterministicSortActions.Count;
            var fieldRef = field;
            DeterministicSortActions.Add(() => { /* placeholder, unused */ });

            var prefixMethod = new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(ThingListSortPrefix)));

            TryPatch($"{label} sort prefix", () =>
            {
                // Store the field reference in a static dict keyed by instance type.
                ThingListSortFields[type] = fieldRef;
                MpCompat.harmony.Patch(tick, prefix: prefixMethod);
                Log.Message($"MPCompat :: {label} — sort-by-thingIDNumber prefix installed");
            });
        }

        /// <summary>
        /// Register a sort-before-tick for a <c>List&lt;CompPower&gt;</c>-like
        /// field on PowerNet. CompPower has a <c>parent</c> Thing with a
        /// stable thingIDNumber to sort by.
        /// </summary>
        private static void RegisterCompPowerListSort(string typeName, string tickMethodName, string fieldName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) return; // silent — may be called multiple times
            var tick = AccessTools.Method(type, tickMethodName);
            if (tick == null) return;
            var field = AccessTools.Field(type, fieldName);
            if (field == null)
            {
                Log.Warning($"MPCompat :: {typeName}.{fieldName} not found — CompPower sort skipped for this field");
                return;
            }

            // Keep a list of (type, field) entries; prefix iterates all
            // matching entries for the instance type.
            if (!CompPowerSortFields.TryGetValue(type, out var list))
            {
                list = new List<FieldInfo>();
                CompPowerSortFields[type] = list;
                // Install the prefix exactly once per type.
                var prefixMethod = new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(CompPowerListSortPrefix)));
                TryPatch($"{typeName}.{tickMethodName} CompPower sort prefix", () =>
                {
                    MpCompat.harmony.Patch(tick, prefix: prefixMethod);
                    Log.Message($"MPCompat :: {typeName}.{tickMethodName} — CompPower list sort prefix installed");
                });
            }
            list.Add(field);
        }

        private static readonly Dictionary<Type, FieldInfo> ThingListSortFields = new Dictionary<Type, FieldInfo>();
        private static readonly Dictionary<Type, List<FieldInfo>> CompPowerSortFields = new Dictionary<Type, List<FieldInfo>>();

        /// <summary>Harmony prefix — sorts the pre-registered Thing list on the instance.</summary>
        public static void ThingListSortPrefix(object __instance)
        {
            if (__instance == null) return;
            if (Current.ProgramState != ProgramState.Playing) return;
            try
            {
                if (!ThingListSortFields.TryGetValue(__instance.GetType(), out var field)) return;
                var list = field.GetValue(__instance) as System.Collections.IList;
                if (list == null || list.Count < 2) return;
                // Sort by thingIDNumber. Materialize to typed list for stable sort.
                var things = new List<Thing>(list.Count);
                foreach (var item in list)
                {
                    if (!(item is Thing t)) return; // wrong type — bail out
                    things.Add(t);
                }
                things.Sort(ThingIdComparer);
                for (int i = 0; i < things.Count; i++) list[i] = things[i];
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: ThingListSortPrefix — {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>Harmony prefix — sorts every pre-registered CompPower list on the instance.</summary>
        public static void CompPowerListSortPrefix(object __instance)
        {
            if (__instance == null) return;
            if (Current.ProgramState != ProgramState.Playing) return;
            try
            {
                if (!CompPowerSortFields.TryGetValue(__instance.GetType(), out var fields)) return;
                for (int fi = 0; fi < fields.Count; fi++)
                {
                    var list = fields[fi].GetValue(__instance) as System.Collections.IList;
                    if (list == null || list.Count < 2) continue;
                    // Materialize and sort by parent.thingIDNumber (stable key).
                    var comps = new List<ThingComp>(list.Count);
                    foreach (var item in list)
                    {
                        if (!(item is ThingComp c) || c.parent == null) return; // wrong type / invalid — bail
                        comps.Add(c);
                    }
                    comps.Sort(CompByParentIdComparer);
                    for (int i = 0; i < comps.Count; i++) list[i] = comps[i];
                }
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: CompPowerListSortPrefix — {e.GetType().Name}: {e.Message}");
            }
        }

        private static readonly Comparison<Thing> ThingIdComparer =
            (a, b) => (a?.thingIDNumber ?? 0).CompareTo(b?.thingIDNumber ?? 0);

        private static readonly Comparison<ThingComp> CompByParentIdComparer =
            (a, b) => (a?.parent?.thingIDNumber ?? 0).CompareTo(b?.parent?.thingIDNumber ?? 0);

        /// <summary>
        /// Nuclear option: wrap <see cref="Thing.Tick"/> / Tick entry points
        /// with a <b>seeded</b> PushPopRand. Each individual thing (pawn,
        /// plant, filth, building, hive, ...) receives a deterministic but
        /// unique Rand stream derived from
        /// <c>HashCombineInt(thingIDNumber, TicksGame)</c>. Unlike the unseeded
        /// PushPopRand in <see cref="PatchTickIsolation"/>, seeded push:
        /// <list type="bullet">
        /// <item>Produces NO herd behavior across things in the same tick,
        ///   because each thing's seed is distinct.</item>
        /// <item>Fully contains any intra-tick drift (float comparison
        ///   branching, Mathf.Pow libm divergence, etc.) to the single thing's
        ///   tick. Pop restores the global Rand state unchanged.</item>
        /// <item>Broad enough to end the Desync-12..16 whack-a-mole: every
        ///   desync so far has been "1 extra Rand call somewhere inside a
        ///   thing's tick". Once the thing tick is sealed, the cascade cannot
        ///   escape.</item>
        /// </list>
        /// Tradeoff: gameplay RNG outcomes diverge from vanilla single-player,
        /// because pawn/thing rolls no longer advance the global Rand stream.
        /// Within MP this is fully deterministic.
        /// </summary>
        private static void PatchThingDoTickSeeded()
        {
            // 1.6 renamed Tick → TickInterval(int). Use whichever exists on
            // the base Thing type. Patch the base's declared method; via
            // virtual dispatch every subclass's override hits the patched body
            // through the base slot only if subclasses call base — so we also
            // iterate known overriders and patch each concrete declaration.
            //
            // Simpler alternative used here: patch the non-virtual public
            // entry point Thing.DoTick which every subclass funnels through.
            var doTick = AccessTools.Method(typeof(Thing), "DoTick");
            if (doTick == null)
            {
                // Fallback for potential 1.6 rename.
                doTick = AccessTools.Method(typeof(Thing), "Tick")
                         ?? AccessTools.Method(typeof(Thing), "TickInterval", new[] { typeof(int) });
            }

            if (doTick == null)
            {
                Log.Warning("MPCompat :: No Thing tick entry point found — seeded isolation skipped");
                return;
            }

            TryPatch($"Thing.{doTick.Name} seeded isolation", () =>
            {
                MpCompat.harmony.Patch(doTick,
                    prefix:  new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(SeededPushPrefixThing))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(VanillaPatches), nameof(PopStatePostfixThing))));
                Log.Message($"MPCompat :: Wrapped Thing.{doTick.Name} with seeded PushPopRand (per-thing per-tick isolation)");
            });
        }

        public static void SeededPushPrefixThing(Thing __instance, out bool __state)
        {
            // __state records whether we actually pushed. The postfix only
            // pops if we pushed, so any early-out here leaves Rand's state
            // stack balanced. Critical during client load / catch-up: do NOT
            // push during non-playing states (map loading, world gen, etc.),
            // because the Rand state stack interacts with MP's own load-time
            // rand handling and can hang the client at ~90% load if unbalanced.
            __state = false;
            try
            {
                if (Current.ProgramState != ProgramState.Playing) return;

                // STEP 14c: one-time per-map deterministic sort of non-Rand
                // divergence sources (BreakdownManager.brokenDownThings,
                // PowerNet internal CompPower lists). Piggybacks on this
                // already-installed prefix so we don't add new Harmony patch
                // targets that could hang the client loader.
                var map = __instance?.Map;
                if (map != null && sortedMapIds.Add(map.uniqueID))
                {
                    TrySortMapCollectionsOnce(map);
                }

                int id = __instance?.thingIDNumber ?? 0;
                int tick = (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
                int seed = Gen.HashCombineInt(id, tick);
                Rand.PushState(seed);
                __state = true;
            }
            catch (Exception e)
            {
                // Ensure we never leave state pushed on exception
                if (__state)
                {
                    try { Rand.PopState(); } catch { }
                    __state = false;
                }
                Log.Warning($"MPCompat :: SeededPushPrefixThing — {e.GetType().Name}: {e.Message}");
            }
        }

        public static void PopStatePostfixThing(bool __state)
        {
            if (!__state) return;
            try { Rand.PopState(); }
            catch (Exception e) { Log.Warning($"MPCompat :: PopStatePostfixThing — {e.GetType().Name}: {e.Message}"); }
        }

        /// <summary>
        /// Wraps a single patch operation in try/catch so one failure doesn't
        /// skip the rest of the patches in the same group. Logs a warning with
        /// the label and continues.
        /// </summary>
        private static bool TryPatch(string label, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: {label} — patch failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Cascade-containment via PushPopRand wrapping at carefully chosen
        /// tick boundaries. Targets are picked to be <i>narrow enough</i> that
        /// the unseeded Rand state sharing (which makes every entity in a tick
        /// start from the same Rand state within the scope) has negligible
        /// gameplay impact, but <i>wide enough</i> to contain per-entity drift
        /// so it cannot leak into unrelated entities in the same tick.
        ///
        /// <para><b>Note on unseeded PushState semantics</b>: The MP API's
        /// <c>Rand.PushState()</c> saves the current state and continues from
        /// it, then restores on pop. So every entity that enters one of these
        /// scopes in the same tick sees the <i>same</i> starting Rand values.
        /// That is fine for the chosen targets — severity/mtb rolls, filth
        /// creation coin flips, wander destination jitter — because symmetry
        /// across entities within a tick is not gameplay-load-bearing here.
        /// Do NOT expand this to <c>Pawn.TickInterval</c> or the whole think
        /// tree without switching to a seeded <c>PushState(seed)</c>.</para>
        /// </summary>
        private static void PatchTickIsolation()
        {
            int patched = 0;

            // 1. Pawn_HealthTracker.HealthTickInterval — wraps every hediff's
            //    tick for one pawn. Severity drift / MTB branch splits inside
            //    one pawn's hediffs can no longer contaminate other pawns.
            //    Parent scope of STEP 8's HediffGiver patches; nested
            //    Push/Pop is stack-safe.
            var health = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.HealthTickInterval));
            if (health != null)
            {
                if (TryPatch("Pawn_HealthTracker.HealthTickInterval isolate", () =>
                {
                    PatchingUtilities.PatchPushPopRand(health);
                    Log.Message("MPCompat :: Wrapped Pawn_HealthTracker.HealthTickInterval with PushPopRand");
                }))
                    patched++;
            }
            else
            {
                Log.Warning("MPCompat :: Pawn_HealthTracker.HealthTickInterval not found — isolation skipped");
            }

            // 2. FilthMaker.TryMakeFilth (all overloads).
            int filthOverloads = 0;
            foreach (var m in typeof(FilthMaker).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != nameof(FilthMaker.TryMakeFilth)) continue;
                var captured = m;
                if (TryPatch($"FilthMaker.TryMakeFilth({string.Join(",", captured.GetParameters().Select(p => p.ParameterType.Name))}) isolate", () =>
                {
                    PatchingUtilities.PatchPushPopRand(captured);
                }))
                    filthOverloads++;
            }
            if (filthOverloads > 0)
            {
                Log.Message($"MPCompat :: Wrapped {filthOverloads} FilthMaker.TryMakeFilth overload(s) with PushPopRand");
                patched += filthOverloads;
            }
            else
            {
                Log.Warning("MPCompat :: FilthMaker.TryMakeFilth overloads not found — isolation skipped");
            }

            // 3. JobGiver_Wander.GetExactWanderDest — base + every subclass
            //    that declares its own override. Virtual dispatch means
            //    patching base alone misses overrides; we enumerate all
            //    JobGiver_Wander subtypes in the Verse assembly and patch any
            //    that DeclaredMethod returns non-null for.
            int wanderCount = 0;
            var wanderBase = typeof(JobGiver_Wander);
            var wanderAssembly = wanderBase.Assembly;

            Type[] wanderTypes;
            try { wanderTypes = wanderAssembly.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { wanderTypes = rtle.Types.Where(t => t != null).ToArray(); }

            foreach (var t in wanderTypes)
            {
                if (t == null || !wanderBase.IsAssignableFrom(t)) continue;
                // DeclaredOnly ensures we pick up only types that DEFINE their
                // own body (base or override), not inherited ones.
                var m = AccessTools.DeclaredMethod(t, "GetExactWanderDest");
                if (m == null || m.IsAbstract) continue;
                var captured = m;
                var tName = t.Name;
                if (TryPatch($"{tName}.GetExactWanderDest isolate", () =>
                {
                    PatchingUtilities.PatchPushPopRand(captured);
                }))
                    wanderCount++;
            }
            if (wanderCount > 0)
            {
                Log.Message($"MPCompat :: Wrapped {wanderCount} JobGiver_Wander.GetExactWanderDest declaration(s) with PushPopRand");
                patched += wanderCount;
            }
            else
            {
                Log.Warning("MPCompat :: No JobGiver_Wander.GetExactWanderDest declarations found — isolation skipped");
            }

            Log.Message($"MPCompat :: Tick isolation applied to {patched} method(s)");
        }

        /// <summary>
        /// PushPopRand on Frame.CompleteConstruction to isolate Rand calls
        /// (quality rolls, spark motes, etc.) that fire when a frame finishes.
        /// The MP mod already rounds workDone to prevent float drift, but if
        /// completion still triggers on different ticks, the Rand calls inside
        /// CompleteConstruction pollute the global Rand state and cascade desyncs.
        /// </summary>
        private static void PatchFrameConstruction()
        {
            // Frame.CompleteConstruction — isolate all Rand calls inside
            var completeMethod = AccessTools.Method(typeof(Frame), nameof(Frame.CompleteConstruction));
            if (completeMethod == null)
            {
                Log.Warning("MPCompat :: Frame.CompleteConstruction not found — patch skipped");
                return;
            }
            TryPatch("Frame.CompleteConstruction PushPopRand", () =>
            {
                PatchingUtilities.PatchPushPopRand(completeMethod);
                Log.Message("MPCompat :: Patched Frame.CompleteConstruction with PushPopRand");
            });
        }
        /// <summary>
        /// PushPopRand on ThinkNode_PrioritySorter.TryIssueJobPackage to isolate
        /// the Rand.Range(-priorityNoise, priorityNoise) call that adds noise to
        /// think node priorities. If upstream float boundary issues cause pawns to
        /// evaluate jobs at different points in the tick sequence, this Rand call
        /// diverges and cascades across all subsequent pawn decisions.
        /// </summary>
        private static void PatchThinkNodePrioritySorter()
        {
            var method = AccessTools.Method(typeof(ThinkNode_PrioritySorter), nameof(ThinkNode_PrioritySorter.TryIssueJobPackage));
            if (method == null)
            {
                Log.Warning("MPCompat :: ThinkNode_PrioritySorter.TryIssueJobPackage not found — patch skipped");
                return;
            }
            TryPatch("ThinkNode_PrioritySorter.TryIssueJobPackage PushPopRand", () =>
            {
                PatchingUtilities.PatchPushPopRand(method);
                Log.Message("MPCompat :: Patched ThinkNode_PrioritySorter.TryIssueJobPackage with PushPopRand");
            });
        }

        /// <summary>
        /// PushPopRand on HediffGiver subclasses whose OnIntervalPassed uses
        /// MTB curves with float math (Mathf.Pow, AgeBiologicalYearsFloat, etc.).
        /// Cross-CPU FP divergence causes different Rand.Value outcomes → one side
        /// fires the hediff event (consuming extra Rand calls in callbacks) while
        /// the other does not, cascading into a full desync.
        /// </summary>
        private static void PatchHediffGivers()
        {
            // Desync-14 lesson: only declared (implemented) methods are
            // patchable. HediffGiver_Birthday does NOT override
            // OnIntervalPassed — it inherits the base HediffGiver
            // implementation. Harmony throws ArgumentException if you hand it
            // the subclass reference. The robust fix is to patch the BASE
            // declared method, which via virtual dispatch covers every
            // subclass that does not override it (Birthday, Disease, any mod
            // subclass that inherits without override). We still patch the
            // two known-override subclasses individually so their overridden
            // bodies are also isolated.
            //
            // Each call is wrapped in TryPatch so a single failure can no
            // longer kill the rest of Apply().
            string[] hediffGiverMethods =
            {
                "Verse.HediffGiver:OnIntervalPassed",                  // base — covers Birthday + any subclass without override
                "Verse.HediffGiver_RandomAgeCurved:OnIntervalPassed",  // confirmed desync-06
                "Verse.HediffGiver_RandomDrugEffect:OnIntervalPassed",
            };

            foreach (var methodName in hediffGiverMethods)
            {
                var captured = methodName;
                var method = AccessTools.DeclaredMethod(captured) ?? AccessTools.Method(captured);
                if (method == null)
                {
                    Log.Warning($"MPCompat :: {captured} not found — patch skipped");
                    continue;
                }
                TryPatch($"{captured} PushPopRand", () =>
                {
                    PatchingUtilities.PatchPushPopRand(method);
                    Log.Message($"MPCompat :: Patched {captured} with PushPopRand");
                });
            }
        }

        /// <summary>
        /// STEP 20 Plan B — Desync-18 structural fix: when a synced command
        /// (e.g. TryTakeOrderedJob, set_Drafted) triggers Reserve → EndCurrentJob
        /// → TryFindAndStartJob, the entire think tree runs inside command
        /// context (ExecuteCmd), OUTSIDE both STEP 12 (Thing.DoTick) and
        /// STEP 17 (MapPostTick) seeded scopes. Every WorkGiver's
        /// HasJobOnThing/JobOnThing calls Rand and MakeJob → GetNextJobID,
        /// contaminating the global state.
        ///
        /// DetermineNextJob is the think-tree entry that produces the Job but
        /// does NOT start it — TryFindAndStartJob calls DetermineNextJob then
        /// starts the resulting job.
        ///
        /// This patch isolates BOTH:
        /// - Rand state (PushState/PopState) — prevents Rand divergence from
        ///   leaking out, regardless of how many WorkGivers call Rand.Range
        /// - UniqueIDsManager.nextJobID counter — prevents JobID counter skew
        ///   from MakeJob calls during think-tree evaluation. The counter is
        ///   saved before evaluation, restored after, and only the winning
        ///   Job gets a fresh deterministic ID from the restored counter.
        ///
        /// Desync-18 trace 2 confirmed vanilla WorkGiver_Tend.JobOnThing also
        /// creating extra jobs inside this path — not just PickUpAndHaul.
        /// </summary>
        private static AccessTools.FieldRef<UniqueIDsManager, int> nextJobIdRef;

        private static void PatchDetermineNextJobPushPopRand()
        {
            var method = AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob");
            if (method == null)
            {
                Log.Warning("MPCompat :: Pawn_JobTracker.DetermineNextJob not found — patch skipped");
                return;
            }

            try { nextJobIdRef = AccessTools.FieldRefAccess<UniqueIDsManager, int>("nextJobID"); }
            catch { nextJobIdRef = null; }

            if (nextJobIdRef == null)
            {
                Log.Warning("MPCompat :: UniqueIDsManager.nextJobID field not found — falling back to PushPopRand only");
                PatchingUtilities.PatchPushPopRand(method);
                return;
            }

            MpCompat.harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(VanillaPatches), nameof(DetermineNextJobPrefix)),
                finalizer: new HarmonyMethod(typeof(VanillaPatches), nameof(DetermineNextJobFinalizer)));
            Log.Message("MPCompat :: Wrapped Pawn_JobTracker.DetermineNextJob with Rand+JobID isolation (STEP 20 Plan B)");
        }

        /// <summary>Save Rand state and nextJobID counter before think-tree evaluation.</summary>
        private static void DetermineNextJobPrefix(out int __state)
        {
            Rand.PushState();
            __state = nextJobIdRef(Find.UniqueIDsManager);
        }

        /// <summary>
        /// Finalizer (runs even on exception) — restore nextJobID counter,
        /// re-assign the winning Job a fresh deterministic ID, then pop Rand.
        ///
        /// Using finalizer instead of postfix ensures Rand.PopState() always
        /// runs, preventing Rand stack leaks on exception.
        ///
        /// Note: Rand.PushState() also suppresses MP trace recording
        /// (DeferredStackTracing.ShouldAddStackTraceForDesyncLog checks
        /// Rand.stateStack.Count > 1 → returns false). So all Rand/UniqueID
        /// traces inside DetermineNextJob are automatically hidden.
        /// </summary>
        private static Exception DetermineNextJobFinalizer(Exception __exception, int __state, Verse.AI.ThinkResult __result)
        {
            try
            {
                // Restore job ID counter to pre-evaluation value
                nextJobIdRef(Find.UniqueIDsManager) = __state;

                // Re-assign the winning Job's ID from the restored counter.
                // Both sides now get the same nextJobID regardless of how many
                // intermediate Jobs were created during evaluation.
                // Only re-assign ID for freshly determined jobs, not jobs
                // dequeued from jobQueue (those already have a consistent ID).
                if (__exception == null && __result.Job != null && !__result.FromQueue)
                    __result.Job.loadID = Find.UniqueIDsManager.GetNextJobID();
            }
            catch (Exception e)
            {
                Log.Warning($"MPCompat :: DetermineNextJobFinalizer — {e.GetType().Name}: {e.Message}");
            }

            Rand.PopState();
            return __exception;
        }

        /// <summary>
        /// STEP 20 Plan C — Desync-22 mitigation: Designator_Install.DesignateSingleCell
        /// throws NRE when the install target Thing does not exist on one side
        /// (due to prior state divergence). The command succeeds on host but
        /// aborts on local, creating entity count divergence (e.g. 31 vs 28
        /// in Desync-23). MP's DesignateFinalizer catches the exception but
        /// the damage is done — host has a placed building, local does not.
        ///
        /// This prefix validates that the thing to install is not null or
        /// destroyed before allowing DesignateSingleCell to proceed.
        ///
        /// IMPORTANT: Does NOT check Spawned — minified items carried by pawns
        /// are not Spawned (they live in pawn inventory). Checking Spawned
        /// would block valid install commands.
        ///
        /// Priority.Last (0, below default 400) ensures this runs AFTER MP's own
        /// DesignateSingleCell prefix, so it only fires during command
        /// execution — never during interface mode where MP's prefix
        /// serializes and sends the command.
        /// </summary>
        private static void PatchDesignatorInstallNullGuard()
        {
            var method = AccessTools.Method(typeof(Designator_Install), nameof(Designator_Install.DesignateSingleCell));
            if (method == null)
            {
                Log.Warning("MPCompat :: Designator_Install.DesignateSingleCell not found — patch skipped");
                return;
            }

            MpCompat.harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(VanillaPatches), nameof(DesignatorInstallGuardPrefix))
                { priority = Priority.Last });
            Log.Message("MPCompat :: Designator_Install.DesignateSingleCell — null-guard prefix installed (STEP 20 Plan C)");
        }

        /// <summary>
        /// Prefix for Designator_Install.DesignateSingleCell — skips execution
        /// if the thing to install is null or destroyed, preventing one-sided
        /// NRE that causes entity count divergence. Only active in multiplayer.
        /// </summary>
        private static bool DesignatorInstallGuardPrefix(Designator_Install __instance)
        {
            if (!MP.IsInMultiplayer)
                return true; // single-player: always proceed

            Thing thingToInstall;
            try { thingToInstall = __instance.ThingToInstall; }
            catch { return true; } // if property itself throws, let original handle it

            if (thingToInstall == null || thingToInstall.Destroyed)
            {
                Log.Warning($"MPCompat :: Designator_Install skipped — ThingToInstall is " +
                            $"{(thingToInstall == null ? "null" : "destroyed")}");
                return false; // skip original to prevent one-sided NRE
            }
            return true; // proceed normally
        }

        /// <summary>
        /// STEP 57 — Desync-57 mitigation: Designator_Cancel.DesignateSingleCell
        /// produces asymmetric thing-list state when a Blueprint_Install that was
        /// already destroyed on one peer is cancelled on the other. The cancel
        /// command runs at a different tick offset and the target cell contains
        /// nothing cancelable, so the cancel either NREs or destroys a different
        /// entity, leaving the two peers with different thing counts and diverged
        /// Rand streams.
        ///
        /// Mirrors STEP 20 Plan C (PatchDesignatorInstallNullGuard, lines 1405-1418):
        /// register a Priority.Last prefix that fires only during synced command
        /// execution (after MP's own prefix has serialized/sent the command) and
        /// skips the designator cleanly when no cancelable blueprint exists at the
        /// target cell.
        ///
        /// Also patches DesignateMultiCell via the same guard so that area-cancel
        /// sweeps behave consistently when some cells have already been cleared.
        /// </summary>
        private static void PatchDesignatorCancelNullGuard()
        {
            var single = AccessTools.Method(typeof(Designator_Cancel), nameof(Designator_Cancel.DesignateSingleCell));
            if (single == null)
            {
                Log.Warning("MPCompat :: Designator_Cancel.DesignateSingleCell not found — patch skipped");
            }
            else
            {
                MpCompat.harmony.Patch(single,
                    prefix: new HarmonyMethod(typeof(VanillaPatches), nameof(DesignatorCancelSingleCellGuardPrefix))
                    { priority = Priority.Last });
                Log.Message("MPCompat :: Designator_Cancel.DesignateSingleCell — null-guard prefix installed (STEP 57)");
            }

            var multi = AccessTools.Method(typeof(Designator_Cancel), nameof(Designator_Cancel.DesignateMultiCell));
            if (multi == null)
            {
                Log.Warning("MPCompat :: Designator_Cancel.DesignateMultiCell not found — patch skipped");
            }
            else
            {
                MpCompat.harmony.Patch(multi,
                    prefix: new HarmonyMethod(typeof(VanillaPatches), nameof(DesignatorCancelMultiCellGuardPrefix))
                    { priority = Priority.Last });
                Log.Message("MPCompat :: Designator_Cancel.DesignateMultiCell — null-guard prefix installed (STEP 57)");
            }
        }

        /// <summary>
        /// Prefix for Designator_Cancel.DesignateSingleCell — skips execution
        /// if there are no cancelable designations at the target cell, preventing
        /// one-sided entity destruction that causes thing-count divergence
        /// (Desync-57). Only active inside a synced MP command.
        /// </summary>
        private static bool DesignatorCancelSingleCellGuardPrefix(Designator_Cancel __instance, IntVec3 c)
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand)
                return true; // single-player or interface mode: always proceed

            Map map;
            try { map = __instance.Map; }
            catch { return true; } // if property throws, let original handle it

            if (map == null)
            {
                Log.Warning("MPCompat :: Designator_Cancel.DesignateSingleCell skipped — Map is null");
                return false;
            }

            // Check whether there is at least one Thing at the cell that the
            // cancel designator accepts. Cell-level designations (zone wipes,
            // etc.) carry no Thing and cannot cause asymmetric entity-count
            // divergence, so we do not filter those — only Thing-targeted
            // blueprints/frames can produce the off-by-N-things desync.
            bool hasCancelable = false;
            try
            {
                foreach (var t in map.thingGrid.ThingsAt(c))
                {
                    if (__instance.CanDesignateThing(t).Accepted)
                    {
                        hasCancelable = true;
                        break;
                    }
                }
                // Also accept if there are any cell-level designations at this
                // position (those are harmless to let through — they can't
                // create Thing count divergence, but blocking them would break
                // zone-cancel commands).
                if (!hasCancelable)
                {
                    foreach (var _ in map.designationManager.AllDesignationsAt(c))
                    {
                        hasCancelable = true;
                        break;
                    }
                }
            }
            catch
            {
                return true; // on any iteration error, let original decide
            }

            if (!hasCancelable)
            {
                Log.Warning($"MPCompat :: Designator_Cancel.DesignateSingleCell skipped — no cancelable target at {c}");
                return false;
            }
            return true;
        }

        // Helper: grab first Thing at a cell without LINQ allocation.
        private static Thing ThingAt(Map map, IntVec3 c)
        {
            foreach (var t in map.thingGrid.ThingsAt(c))
                return t;
            return null;
        }

        /// <summary>
        /// Prefix for Designator_Cancel.DesignateMultiCell — when executing a
        /// synced command, filters the incoming cell list to only those that still
        /// contain something cancelable on this peer. Replacing the enumerable in
        /// a prefix is not straightforward (Harmony passes IEnumerable by value),
        /// so we simply short-circuit to false and re-invoke the single-cell path
        /// for each valid cell manually — exactly as vanilla's base class does.
        /// </summary>
        private static bool DesignatorCancelMultiCellGuardPrefix(Designator_Cancel __instance, IEnumerable<IntVec3> cells)
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand)
                return true;

            Map map;
            try { map = __instance.Map; }
            catch { return true; }

            if (map == null)
                return true;

            // Replay only the cells that still have a cancelable target
            // (Thing-targeted or cell-level designation). Cells with neither
            // are silently dropped to prevent one-sided entity destruction.
            try
            {
                foreach (var c in cells)
                {
                    bool hasCancelable = false;
                    foreach (var t in map.thingGrid.ThingsAt(c))
                    {
                        if (__instance.CanDesignateThing(t).Accepted)
                        { hasCancelable = true; break; }
                    }
                    if (!hasCancelable)
                    {
                        foreach (var _ in map.designationManager.AllDesignationsAt(c))
                        { hasCancelable = true; break; }
                    }
                    if (hasCancelable)
                        __instance.DesignateSingleCell(c);
                }
            }
            catch
            {
                return true; // on any error fall back to vanilla
            }
            return false; // we handled it
        }

        /// <summary>
        /// Desync-08/11/13/14 fix: <c>GenLeaving.DoLeavingsFor</c> calls
        /// <c>GenCollection.InRandomOrder</c> → <c>Rand.Range</c> when scattering
        /// leavings from destroyed blueprints/frames. This fires during
        /// <c>Designator_Cancel</c> and <c>Designator_Build</c> (via
        /// <c>GenSpawn.WipeExistingThings</c>) command execution, leaking Rand
        /// calls into the global state without isolation.
        /// </summary>
        private static void PatchGenLeavingsDoLeavingsFor()
        {
            var methods = AccessTools.GetDeclaredMethods(typeof(GenLeaving))
                .Where(m => m.Name == nameof(GenLeaving.DoLeavingsFor))
                .ToList();

            if (methods.Count == 0)
            {
                Log.Warning("MPCompat :: GenLeaving.DoLeavingsFor not found — patch skipped");
                return;
            }

            foreach (var method in methods)
            {
                var captured = method;
                TryPatch($"GenLeaving.DoLeavingsFor({string.Join(",", captured.GetParameters().Select(p => p.ParameterType.Name))}) PushPopRand", () =>
                {
                    PatchingUtilities.PatchPushPopRand(captured);
                    Log.Message($"MPCompat :: Patched GenLeaving.DoLeavingsFor({string.Join(",", captured.GetParameters().Select(p => p.ParameterType.Name))}) with PushPopRand");
                });
            }
        }
    }
}
