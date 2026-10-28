using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

public sealed partial class CardApi
{
    /// <summary>BP_CardFunctions.DamageCard action path; the returned boolean
    /// rereads total defense after the apply/death/callback chain completes.</summary>
    internal bool ResolveEffectDamage(CardInstance target, int amount, CardInstance? source,
                                      bool isRedirected, bool fromFight, bool isFightDefenderDamage)
    {
        // i=94/135: a valid object must be on the board. No positive-request
        // or positive-defense guard precedes the modifier callbacks.
        if (!IsLocatedOnBoard(target)) return false;
        int damage = amount;
        if (!isRedirected)
            damage = ExecuteOnDealDamageAddDamage(source, target, damage, false, fromFight, false);

        // i=149 skips only i=690. Both branches rejoin i=190; redirected
        // damage still passes immunity and the own/T38 final modifiers.
        damage = ExecuteOnDealDamageAddDamageAfterCalc(source, target, damage, false, false, isRedirected);
        ApplyCalculatedDamageCore(target, damage, source, false, false, isRedirected,
            isFightDefenderDamage: isFightDefenderDamage);
        // i=310..385 is a fresh getter, not the saved destruction decision
        // used inside ApplyDamageToCard and not an IsAlive/location query.
        return Math.Clamp(target.Defense, 0, 99) <= 0;
    }
}
