using KLink.Bot.Cards;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;
using KLink.Bot.Engine;

namespace KLink.Bot.Sim;

internal static partial class SelfTest
{
    private static KismetExpr MathExpr(string name, params KismetExpr[] args) => new()
    {
        Math = name,
        Args = args,
    };

    private static KismetExpr StringExpr(string value) => new() { Str = value };
    private static KismetExpr IntExpr(int value) => new() { Int = value };
    private static KismetExpr LocalExpr(string name) => new() { Var = name, Scope = "local" };

    private static KismetStep SetMathResult(int index, string destination, KismetExpr expression) =>
        new(index, "set", null, Array.Empty<KismetExpr>(), Array.Empty<int>(), expression,
            null, -1, destination, null) { DestinationScope = "local" };

    private static string? KismetStringSearchEnumMapping(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var program = new KismetProgram(new[]
        {
            SetMathResult(0, "suffixIgnoreCase", MathExpr("EndsWith",
                StringExpr("unit_EOT"), StringExpr("eot"), IntExpr(1))),
            SetMathResult(1, "suffixCaseSensitive", MathExpr("EndsWith",
                StringExpr("unit_EOT"), StringExpr("eot"), IntExpr(0))),
            SetMathResult(2, "splitFromStartFound", MathExpr("Split",
                StringExpr("aXbXc"), StringExpr("x"), LocalExpr("startLeft"),
                LocalExpr("startRight"), IntExpr(1), IntExpr(0))),
            SetMathResult(3, "splitFromEndFound", MathExpr("Split",
                StringExpr("aXbXc"), StringExpr("x"), LocalExpr("endLeft"),
                LocalExpr("endRight"), IntExpr(1), IntExpr(1))),
            SetMathResult(4, "splitCaseSensitiveFound", MathExpr("Split",
                StringExpr("aXb"), StringExpr("x"), LocalExpr("strictLeft"),
                LocalExpr("strictRight"), IntExpr(0), IntExpr(0))),
        }, 0);
        var result = engine.Api.Vm.RunLocalProgramMulti(program, ctx,
            new Dictionary<string, object?>
            {
                ["strictLeft"] = "unchanged-left",
                ["strictRight"] = "unchanged-right",
            },
            "suffixIgnoreCase", "suffixCaseSensitive",
            "splitFromStartFound", "startLeft", "startRight",
            "splitFromEndFound", "endLeft", "endRight",
            "splitCaseSensitiveFound", "strictLeft", "strictRight");

        if (!Equals(result["suffixIgnoreCase"], true)
            || !Equals(result["suffixCaseSensitive"], false))
            return "EndsWith did not map IgnoreCase=1 and CaseSensitive=0";
        if (!Equals(result["splitFromStartFound"], true)
            || !Equals(result["startLeft"], "a") || !Equals(result["startRight"], "bXc"))
            return "Split did not use case-insensitive matching from the start";
        if (!Equals(result["splitFromEndFound"], true)
            || !Equals(result["endLeft"], "aXb") || !Equals(result["endRight"], "c"))
            return "Split did not honor FromEnd=1 with IgnoreCase=1";
        if (!Equals(result["splitCaseSensitiveFound"], false)
            || !Equals(result["strictLeft"], "unchanged-left")
            || !Equals(result["strictRight"], "unchanged-right"))
            return "Split did not preserve output references on a case-sensitive miss";
        return null;
    }

    private static string? KismetGetValidValueEnumBounds(CardDatabase db)
    {
        const string enumPath = "/Game/Structs/CardMoveReason.CardMoveReason";
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        int[] inputs = [0, 12, 13, 14, 255];
        var steps = inputs.Select((value, i) => SetMathResult(i, $"value{i}",
            MathExpr("GetValidValue", new KismetExpr { Obj = enumPath }, IntExpr(value)))).ToArray();
        var result = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null,
            "value0", "value1", "value2", "value3", "value4");

        int[] expected = [0, 12, 13, 0, 0];
        for (int i = 0; i < expected.Length; i++)
        {
            if (!Equals(result[$"value{i}"], expected[i]))
                return $"GetValidValue enum boundary {inputs[i]} returned {result[$"value{i}"] ?? "<null>"}; expected {expected[i]}";
        }

        return null;
    }
}
