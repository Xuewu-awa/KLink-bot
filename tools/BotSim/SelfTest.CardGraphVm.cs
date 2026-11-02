using KLink.Bot.Cards;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static string? CardGraphVmPayloads(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var card = state.Create("card_event_pams", Side.Left, CardLocation.HandLeft, 0);
        var other = state.Create("card_unit_arado_ar_196", Side.Left, Side.Left.HqOf(), 1);
        card.ReceivedAbilitiesFromCards["ability"] = new() { other.CardId };
        state.AddBuffsToRemoveEndOfTurn(7, other.CardId);
        var ctx = new EffectContext { Engine = engine, State = state, Self = card, Target = other, Controller = Side.Left };
        KismetExpr Slot(string name, string? element = null) => new() { Var = name, Scope = "instance", ElementType = element };
        KismetExpr Call(string name, params KismetExpr[] args) => new() { Call = name, Args = args };
        var steps = new List<KismetStep>();
        void Store(string name, KismetExpr value) => steps.Add(new(steps.Count, "set", null, [], [], value, null, -1, name, null)
            { DestinationScope = "instance" });
        void Invoke(string name, params KismetExpr[] args) => steps.Add(new(steps.Count, "call", name, args, [], null, null, -1, null, null));
        Store("savedCard", new() { Var = "targetCard", Scope = "local" });
        Store("savedNull", new() { None = true });
        Store("literal", new() { Array = [new() { Int = 3 }, new() { Float = 1.5 }, new() { Self = true }] });
        Store("abilities", new() { Var = "receivedAbilitiesFromCards", Scope = "instance" });
        Store("choices", new() { Var = "chooseOneCards", Scope = "instance" });
        Store("expiry", Call("GetBuffsToRemoveEndOfTurn"));
        Store("board", Call("GetAllCardsOnBoard", new KismetExpr { Bool = false }));
        Store("tableFound", Call("GetDataTableRowFromName",
            new() { Obj = "/Game/Structs/kreditCombinationsUSUnits.kreditCombinationsUSUnits" }, new() { Name = "6" }, Slot("row")));
        Invoke("Array_Add", Slot("ids", "Int"), new() { Int = other.CardId });
        Invoke("Array_Add", Slot("objects", "Object"), new() { Var = "targetCard", Scope = "local" });
        Invoke("Array_Add", Slot("names", "Name"), new() { Name = "alpha" });
        Invoke("Set_Add", Slot("pinned_units_with_override"), new() { Var = "targetCard", Scope = "local" });
        Invoke("Map_Add", Slot("lookup"), new() { Var = "targetCard", Scope = "local" }, Slot("objects"));
        Store("alias", Slot("objects"));
        engine.Api.Vm.Run(new KismetProgram(steps, 0), ctx);
        if (engine.Api.Vm.FaultedPrograms != 0 || engine.Api.Vm.UnsupportedInstructions != 0
            || engine.Api.Vm.UnimplementedCalls.Count != 0 || state.UnimplementedCalls.Count != 0)
            return "VM payload producer failed or used an unsupported call";
        var vars = card.BlueprintVariables;
        if (vars["tableFound"] is not true || vars["ids"] is not List<int>
            || vars["objects"] is not List<CardInstance> || vars["names"] is not List<object?>
            || vars["lookup"] is not Dictionary<object, object?> || !vars.ContainsKey("savedNull"))
            return "VM producer runtime shape changed";
        var graph = CardGraphClone.Create(new[] { card });
        var copy = graph.Roots[0]; var target = graph.Cards[other]; var cv = copy.BlueprintVariables;
        var objects = (List<CardInstance>)cv["objects"]!;
        if (!ReferenceEquals(cv["savedCard"], target) || cv["savedNull"] is not null
            || !ReferenceEquals(cv["alias"], objects) || !ReferenceEquals(objects[0], target)
            || !ReferenceEquals(((Dictionary<object, object?>)cv["lookup"]!)[target], objects)
            || !copy.BlueprintSets["pinned_units_with_override"].Contains(target)
            || !ReferenceEquals(cv["pinned_units_with_override"], copy.BlueprintSets["pinned_units_with_override"])
            || !ReferenceEquals(((List<object?>)cv["literal"]!)[2], copy))
            return "VM-produced graph alias or card ownership lost";
        const string comboField = "combos_4_561CC77D4BB75053570144B4E1CA91A4";
        var sourceCombos = (List<object?>)((Dictionary<string, object?>)vars["row"]!)[comboField]!;
        var copyCombos = (List<object?>)((Dictionary<string, object?>)cv["row"]!)[comboField]!;
        ((List<object?>)copyCombos[0]!).Clear();
        var expiry = (Dictionary<object, object?>)cv["expiry"]!;
        ((int[])((Dictionary<string, object?>)expiry[7]!)["CardIDs"]!)[0] = -7;
        objects.Clear();
        ((List<object?>)((Dictionary<string, object?>)((Dictionary<object, object?>)cv["abilities"]!)["ability"]!)["cardsGivingAbility"]!).Clear();
        if (((List<object?>)sourceCombos[0]!).Count != 3 || ((List<CardInstance>)vars["objects"]!).Count != 1
            || state.GetBuffsToRemoveEndOfTurn()[7][0] != other.CardId
            || ((int[])((Dictionary<string, object?>)((Dictionary<object, object?>)vars["expiry"]!)[7]!)["CardIDs"]!)[0] != other.CardId
            || ((List<object?>)((Dictionary<string, object?>)((Dictionary<object, object?>)vars["abilities"]!)["ability"]!)["cardsGivingAbility"]!).Count != 1)
            return "VM-produced nested payload shares mutable storage";
        // Continue through the VM, rather than only inspecting the copied buffers.
        var continueProgram = new KismetProgram(new[] {
            new KismetStep(0, "call", "Array_Add", [Slot("objects", "Object"), new() { Self = true }], [], null, null, -1, null, null)
        }, 0);
        engine.Api.Vm.Run(continueProgram, new EffectContext
            { Engine = engine, State = state, Self = copy, Target = target, Controller = Side.Left });
        if (objects.Count != 1 || !ReferenceEquals(objects[0], copy)
            || !ReferenceEquals(((List<CardInstance>)vars["objects"]!)[0], other))
            return "continued VM mutation reached the source graph";
        // This owner-linked VM handle is intentionally outside the payload
        // whitelist. If a future program persists it, copying must fail closed.
        var handleProgram = new KismetProgram(new[] {
            new KismetStep(0, "set", null, [], [], new() { Var = "GameplayTags", Scope = "instance" }, null, -1, "tagHandle", null)
                { DestinationScope = "instance" }
        }, 0);
        engine.Api.Vm.Run(handleProgram, ctx);
        try { CardGraphClone.Create(new[] { card }); return "owner-linked VM handle was silently copied"; }
        catch (NotSupportedException ex) when (ex.Message.Contains("GameplayTagContainerRef", StringComparison.Ordinal)) { }
        return null;
    }

    private static string? CardGraphRealCandidatePayload(CardDatabase db)
    {
        const string name = "card_event_pams";
        var program = KismetLibrary.Default?.FindLocalProgram(name, "GetChooseSpawnCards");
        if (program is null) return "real candidate program missing";
        var (engine, state) = EmptyBoard(db);
        var card = state.Create(name, Side.Left, CardLocation.HandLeft, 0);
        var ctx = new EffectContext { Engine = engine, State = state, Self = card, Controller = Side.Left };
        var outputs = engine.Api.Vm.RunLocalProgramMulti(program, ctx, null, "cards", "markAsSeen", "keepOrder");
        if (engine.Api.Vm.FaultedPrograms != 0 || engine.Api.Vm.UnsupportedInstructions != 0
            || engine.Api.Vm.UnimplementedCalls.Count != 0 || state.UnimplementedCalls.Count != 0)
            return "real candidate program diagnostics are not clean";
        if (card.BlueprintVariables.GetValueOrDefault("PossibleCards") is not List<CardInstance> candidates || candidates.Count == 0
            || !ReferenceEquals(outputs["cards"], candidates)) return "real persisted candidate list missing";
        var graph = CardGraphClone.Create(new[] { card });
        var copy = graph.Roots[0]; var cc = (List<CardInstance>)copy.BlueprintVariables["PossibleCards"]!;
        if (!candidates.Select(x => graph.Cards[x]).SequenceEqual(cc)
            || candidates.Where((x, i) => ReferenceEquals(x, cc[i]) || x.StaticTemplateIdentity != cc[i].StaticTemplateIdentity).Any())
            return "real candidate order, identity or remapping differs";
        cc[0].BlueprintVariables["clone-only"] = 1;
        if (candidates[0].BlueprintVariables.ContainsKey("clone-only")) return "static candidate payload shared";
        cc.Clear();
        if (candidates.Count == 0) return "real candidate list buffer shared";
        return null;
    }
}
