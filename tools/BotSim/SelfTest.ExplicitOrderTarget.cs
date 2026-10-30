using System.Text.Json;
using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Training;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    // Deliberately excludes VM counters and BlueprintVariables: target queries
    // are not pure. Assert that rejected roots spend no resources or effects.
    private static string TargetRootEffects(MatchEngine e, CardInstance source, CardInstance[] units)
        => JsonSerializer.Serialize(new { kredits=e.State.Kredits(source.Owner),
            source.Location, source.CurrentTarget, log=e.State.ActionLog.Count,
            rng=e.State.Random.State, units=units.Select(c=>new {c.CardId,c.Defense,c.PinnedTurns,c.Location}) });

    private static string? ExplicitOrderRejectsWrongTargets(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        foreach(var kind in new[]{"friendly","own_hq","enemy_hq","zero","missing","hand","covert"})
        {
            var (e,source,units)=ActionTargetSmoke.Fixture(db,side);
            int target=kind switch {"friendly"=>82,"own_hq"=>side==Side.Left?1:41,
                "enemy_hq"=>side==Side.Left?41:1,"zero"=>0,"missing"=>99999,_=>80};
            if(kind=="hand")e.State.Move(units[0],side.Opposite().HandOf(),0);
            if(kind=="covert"){units[0].Keywords.Add(Keyword.Covert);units[0].IsRevealed=false;}
            // A different valid target exists, so availability alone must not suffice.
            if(!e.CanPlay(source,out _))return "fixture lacks a valid alternative: "+kind;
            string before=TargetRootEffects(e,source,units);
            if(MainActionPolicyAdapter.ApplyRoot(e,side,new(MainActionKind.Play,70,target)))
                return "invalid explicit order target accepted: "+side+"/"+kind;
            if(before!=TargetRootEffects(e,source,units))return "rejected root changed resources/effects: "+kind;
            if(e.Api.Vm.FaultedPrograms!=0)return "target query VM fault: "+kind;
        }
        return null;
    }

    private static string? ExplicitOrderDamagePredicate(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        foreach(var target in new[]{80,81,82})
        {
            var (e,source,units)=ActionTargetSmoke.Fixture(db,side,"card_event_critical_hit");
            units[1].Defense=4;units[2].Defense=4;
            bool expected=target!=80;
            string before=TargetRootEffects(e,source,units);
            bool accepted=MainActionPolicyAdapter.ApplyRoot(e,side,new(MainActionKind.Play,70,target));
            if(accepted!=expected)return "damaged-unit predicate ignored: "+side+"/"+target;
            if(!expected && before!=TargetRootEffects(e,source,units))return "healthy target rejection had effects";
            if(expected && units.Single(c=>c.CardId==target).Location.IsBoard())return "accepted damaged target not destroyed";
            if(e.Api.Vm.FaultedPrograms!=0)return "damage predicate VM fault";
        }
        return null;
    }

    private static string? ExplicitOrderFriendlyPredicate(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        foreach(var target in new[]{80,82})
        {
            var (e,source,units)=ActionTargetSmoke.Fixture(db,side,"card_event_air_corps_ferrying");
            string before=TargetRootEffects(e,source,units);
            bool accepted=MainActionPolicyAdapter.ApplyRoot(e,side,new(MainActionKind.Play,70,target));
            if(accepted!=(target==82))return "friendly-unit predicate ignored: "+side+"/"+target;
            if(target==80 && before!=TargetRootEffects(e,source,units))return "enemy target rejection had effects";
            if(target==82 && units[2].Defense!=6)return "friendly target did not receive buff";
        }
        return null;
    }

    private static string? ExplicitOrderNoTargetAndLegacyQueries(CardDatabase db)
    {
        foreach(var side in new[]{Side.Left,Side.Right})
        {
            var (e,source,units)=ActionTargetSmoke.Fixture(db,side);
            if(!e.CanPlay(source,out _))return "availability query incorrectly requires explicit target";
            source.BlueprintVariables["targetOverride"]=units[1];
            if(e.Api.CanTarget(source,units[2]).Can
                || !ReferenceEquals(source.BlueprintVariables["targetOverride"],units[1]))
                return "explicit query failed to restore override binding";
            var (plain,order,_)=ActionTargetSmoke.Fixture(db,side,"card_event_war_bonds");
            if(order.SelectTargetOnPlayedFromHand || !MainActionPolicyAdapter.ApplyRoot(plain,side,new(MainActionKind.Play,70)))
                return "untargeted order incorrectly rejected";
        }
        return null;
    }
}
