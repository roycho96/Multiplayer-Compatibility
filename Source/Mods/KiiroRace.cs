using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Kiiro Race by Ancot</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2988200143"/>
    /// <remarks>
    /// Two Command_Toggle gizmos in Kiiro apparel directly mutate simulation
    /// state from their <c>toggleAction</c> delegates instead of going through
    /// a sync'd method:
    ///
    /// <list type="number">
    /// <item>
    /// <description>
    /// <c>Kiiro.Apparel_PoisonBottle.GetWornGizmos</c> lambda 0 —
    /// <c>toggleAction = delegate { Switch = !Switch; }</c>
    /// flips a public bool that gates which damage def
    /// <c>Kiiro.Projectile_CarryingPoison.Launch</c> selects
    /// (Kiiro_DeadlyPoison / Kiiro_ParalyticPoison / vanilla). Without
    /// sync, the clicker flips the bool locally and the next shot
    /// carries a different poison on different clients → instant desync.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>Kiiro.Apparel_Stealth.GetWornGizmos</c> lambda 0 —
    /// <c>toggleAction</c> flips <c>IF_CanStealth</c> and, if disabling,
    /// directly calls <c>Wearer.health.RemoveHediff(...)</c> to strip
    /// the <c>Kiiro_DarkStealth</c> hediff. Direct hediff-set mutation
    /// from a UI click is a textbook desync source.
    /// </description>
    /// </item>
    /// </list>
    ///
    /// Lambda ordinals confirmed via <c>ilspycmd -il Kiiro.dll</c>:
    /// <c>&lt;GetWornGizmos&gt;b__0_0</c> and <c>&lt;GetWornGizmos&gt;b__4_0</c>
    /// are both instance methods declared directly on the parent class
    /// (non-capturing beyond <c>this</c>).
    ///
    /// Other Kiiro surfaces audited and found MP-safe as-is:
    /// <list type="bullet">
    /// <item><description>
    /// <c>Apparel_Stealth.Tick</c> / <c>DoSomething_Stealth</c> — deterministic
    /// reads (glowGrid, GasUtility, stance, IF_CanStealth); ticks synchronously
    /// under MP world tick.
    /// </description></item>
    /// <item><description>
    /// <c>Kiiro_WorldManager.WorldComponentTick</c> — saved counters, no wall
    /// clock, deterministic iteration over <c>WorldObjectsHolder.Settlements</c>.
    /// </description></item>
    /// <item><description>
    /// <c>Projectile_CarryingPoison.Impact</c>, <c>Verb_SmokePop</c>,
    /// <c>Verb_PhantomDaggerAttackDamage</c> — projectile impact / verb cast
    /// paths, already under the MP-seeded Rand context.
    /// </description></item>
    /// <item><description>
    /// Harmony patches on <c>Pawn_GuestTracker</c>, <c>FactionUtility</c>,
    /// <c>SettlementProximityGoodwillUtility</c>, <c>PawnUIOverlay</c>,
    /// <c>JobDriver_Wait</c> — all postfix-only readers or target
    /// deterministic game paths.
    /// </description></item>
    /// </list>
    ///
    /// Known latent Kiiro bug NOT patched here (not MP-specific):
    /// <c>Apparel_PoisonBottle</c> has no <c>ExposeData</c> override, so
    /// <c>Switch</c> resets on every save/load. Identical on all clients
    /// (not a desync) — flag upstream rather than patch here.
    /// </remarks>
    [MpCompatFor("Ancot.KiiroRace")]
    internal class KiiroRace
    {
        public KiiroRace(ModContentPack mod)
        {
            // Apparel_PoisonBottle: toggle the "apply poison on next shot" bool.
            //   b__0_0 = toggleAction (mutates Switch) — sync this.
            //   b__0_1 = isActive (reads Switch) — DO NOT sync, it runs every UI frame.
            MpCompat.RegisterLambdaMethod("Kiiro.Apparel_PoisonBottle", "GetWornGizmos", 0);

            // Apparel_Stealth: toggle the "allow auto-stealth" bool + strip hediff on disable.
            //   b__4_0 = toggleAction — sync this.
            //   b__4_1 = isActive — read-only, do NOT sync.
            MpCompat.RegisterLambdaMethod("Kiiro.Apparel_Stealth", "GetWornGizmos", 0);
        }
    }
}
