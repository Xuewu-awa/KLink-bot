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

    private static string? KismetClampNativeBounds(CardDatabase db)
    {
        // Current-client execClamp tail computes max(minimum, min(value, maximum)),
        // including minimum > maximum. Fixture outputs come from offline execution
        // of that captured instruction slice, not from this C# expression.
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kismet-clamp-native-20261103.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        foreach (var row in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            int value = row[0].GetInt32(), minimum = row[1].GetInt32();
            int maximum = row[2].GetInt32(), expected = row[3].GetInt32();
            long faults = engine.Api.Vm.FaultedPrograms;
            var program = new KismetProgram([SetMathResult(0, "result",
                MathExpr("Clamp", IntExpr(value), IntExpr(minimum), IntExpr(maximum)))], 0);
            var result = engine.Api.Vm.RunLocalProgramMulti(program, ctx, null, "result");
            if (engine.Api.Vm.FaultedPrograms != faults || !Equals(result["result"], expected))
                return $"Clamp({value},{minimum},{maximum}) returned {result["result"] ?? "<null>"}; expected {expected}; faults={engine.Api.Vm.FaultedPrograms - faults}";
        }
        return null;
    }

    private static string? KismetSelectIntContract(CardDatabase db)
    {
        // Native execSelectInt parses A, B, and bPickA and returns A only when
        // the third argument is true; signed endpoints are copied unchanged.
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var cases = new (int A, int B, bool PickA, int Expected)[]
        {
            (17, -9, true, 17),
            (17, -9, false, -9),
            (0, int.MinValue, true, 0),
            (int.MaxValue, 0, false, 0),
            (int.MinValue, int.MaxValue, true, int.MinValue),
            (int.MinValue, int.MaxValue, false, int.MaxValue),
        };
        var steps = new List<KismetStep>(cases.Length);
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            steps.Add(SetMathResult(i, $"result{i}", MathExpr("SelectInt",
                IntExpr(c.A), IntExpr(c.B), new KismetExpr { Bool = c.PickA })));
        }

        string[] outputs = Enumerable.Range(0, cases.Length).Select(i => $"result{i}").ToArray();
        long faults = engine.Api.Vm.FaultedPrograms;
        var result = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, outputs);
        if (engine.Api.Vm.FaultedPrograms != faults)
            return "SelectInt probe unexpectedly faulted the VM";
        for (int i = 0; i < cases.Length; i++)
        {
            if (!Equals(result[outputs[i]], cases[i].Expected))
                return $"SelectInt({cases[i].A},{cases[i].B},{cases[i].PickA}) returned "
                    + $"{result[outputs[i]] ?? "<null>"}; expected {cases[i].Expected}";
        }

        // Malformed IR with a missing predicate currently evaluates the absent
        // predicate as false and selects B. This is simulator fallback behavior,
        // not a claim that the native UFunction accepts an omitted parameter.
        var missingPredicate = new KismetProgram(new[]
        {
            SetMathResult(0, "missingPredicate", MathExpr("SelectInt", IntExpr(4), IntExpr(8))),
        }, 0);
        var malformed = engine.Api.Vm.RunLocalProgramMulti(missingPredicate, ctx, null, "missingPredicate");
        if (!Equals(malformed["missingPredicate"], 8))
            return $"missing-predicate simulator fallback returned {malformed["missingPredicate"] ?? "<null>"}; expected 8";

        return null;
    }

    private static string? KismetDividePercentNativeContract(CardDatabase db)
    {
        // The fixture is bound to the ownership-checked current-client capture.
        // It covers truncation toward zero, remainder sign, zero-divisor output,
        // and representable int32 endpoints. INT_MIN / -1 is excluded because
        // native IDIV raises a processor exception there and its game-level
        // handling has not been established.
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kismet-divide-percent-native-20261103.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "KLink.slnx")))
            repository = repository.Parent;
        if (repository is null)
            return "could not locate repository root for the native capture fixture";
        string capturePath = Path.Combine(repository.FullName,
            root.GetProperty("capture_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(capturePath))
            return $"native capture not found at fixture path {root.GetProperty("capture_path").GetString()}";
        string captureHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(capturePath))).ToLowerInvariant();
        if (!string.Equals(captureHash, root.GetProperty("capture_sha256").GetString(), StringComparison.Ordinal))
            return "Divide/Percent native capture SHA-256 differs from the regression fixture";
        using var capture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(capturePath));
        var captureRoot = capture.RootElement;
        if (!string.Equals(captureRoot.GetProperty("exe_sha256").GetString(),
                root.GetProperty("exe_sha256").GetString(), StringComparison.Ordinal))
            return "Divide/Percent executable identity differs from the regression fixture";
        foreach (var function in root.GetProperty("native_functions").EnumerateObject())
        {
            var native = captureRoot.GetProperty("functions").GetProperty(function.Name);
            if (!string.Equals(native.GetProperty("exec_rva").GetString(),
                    function.Value.GetProperty("exec_rva").GetString(), StringComparison.Ordinal))
                return $"{function.Name} entry RVA differs from the regression fixture";
            foreach (var expected in function.Value.GetProperty("instructions").EnumerateArray())
            {
                string rva = expected.GetProperty("rva").GetString()!;
                string bytes = expected.GetProperty("bytes").GetString()!;
                bool found = native.GetProperty("instructions").EnumerateArray().Any(actual =>
                    string.Equals(actual.GetProperty("rva").GetString(), rva, StringComparison.Ordinal)
                    && string.Equals(actual.GetProperty("bytes").GetString(), bytes, StringComparison.Ordinal));
                if (!found)
                    return $"{function.Name} instruction at {rva} differs from the regression fixture";
            }
        }

        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var cases = root.GetProperty("cases").EnumerateArray()
            .Select(row => (Dividend: row[0].GetInt32(), Divisor: row[1].GetInt32(),
                Quotient: row[2].GetInt32(), Remainder: row[3].GetInt32())).ToArray();
        var steps = new List<KismetStep>(cases.Length * 2);
        var outputs = new List<string>(cases.Length * 2);
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            string quotient = $"quotient{i}", remainder = $"remainder{i}";
            outputs.Add(quotient);
            outputs.Add(remainder);
            steps.Add(SetMathResult(steps.Count, quotient,
                MathExpr("Divide_IntInt", IntExpr(c.Dividend), IntExpr(c.Divisor))));
            steps.Add(SetMathResult(steps.Count, remainder,
                MathExpr("Percent_IntInt", IntExpr(c.Dividend), IntExpr(c.Divisor))));
        }

        long faults = engine.Api.Vm.FaultedPrograms;
        var result = engine.Api.Vm.RunLocalProgramMulti(
            new KismetProgram(steps, 0), ctx, null, outputs.ToArray());
        if (engine.Api.Vm.FaultedPrograms != faults)
            return "Divide_IntInt/Percent_IntInt finite integer probe unexpectedly faulted the VM";
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            if (!Equals(result[$"quotient{i}"], c.Quotient)
                || !Equals(result[$"remainder{i}"], c.Remainder))
                return $"Divide/Percent({c.Dividend},{c.Divisor}) returned "
                    + $"q={result[$"quotient{i}"] ?? "<null>"}, r={result[$"remainder{i}"] ?? "<null>"}; "
                    + $"expected q={c.Quotient}, r={c.Remainder}";
        }

        return null;
    }

    private static string? KismetIntComparisonNativeContract(CardDatabase db)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kismet-int-comparisons-native-20261103.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "KLink.slnx")))
            repository = repository.Parent;
        if (repository is null)
            return "could not locate repository root for the native comparison capture fixture";
        string captureRelative = root.GetProperty("capture_path").GetString()!;
        string capturePath = Path.Combine(repository.FullName, captureRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(capturePath))
            return $"native comparison capture not found at {captureRelative}";
        string captureHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(capturePath))).ToLowerInvariant();
        if (!string.Equals(captureHash, root.GetProperty("capture_sha256").GetString(), StringComparison.Ordinal))
            return "Int32 comparison native capture SHA-256 differs from the regression fixture";
        using var capture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(capturePath));
        var captureRoot = capture.RootElement;
        if (!string.Equals(captureRoot.GetProperty("exe_sha256").GetString(),
                root.GetProperty("exe_sha256").GetString(), StringComparison.Ordinal))
            return "Int32 comparison executable identity differs from the regression fixture";

        foreach (var expected in root.GetProperty("functions").EnumerateObject())
        {
            var native = captureRoot.GetProperty("functions").GetProperty(expected.Name);
            if (!string.Equals(native.GetProperty("exec_rva").GetString(),
                    expected.Value.GetProperty("exec_rva").GetString(), StringComparison.Ordinal))
                return $"{expected.Name} entry RVA differs from the regression fixture";
            string instruction = expected.Value.GetProperty("condition_instruction").GetString()!;
            string bytes = expected.Value.GetProperty("condition_bytes").GetString()!;
            bool found = native.GetProperty("instructions_to_first_ret").EnumerateArray().Any(actual =>
                string.Equals(actual.GetProperty("instruction").GetString(), instruction, StringComparison.Ordinal)
                && string.Equals(actual.GetProperty("bytes").GetString(), bytes, StringComparison.Ordinal));
            if (!found)
                return $"{expected.Name} condition-code instruction differs from the native fixture";
        }

        var cases = root.GetProperty("cases").EnumerateArray()
            .Select(row => (A: row[0].GetInt32(), B: row[1].GetInt32())).ToArray();
        var steps = new List<KismetStep>(cases.Length * root.GetProperty("functions").EnumerateObject().Count());
        var outputs = new List<string>();
        foreach (var function in root.GetProperty("functions").EnumerateObject())
        {
            var expectedResults = function.Value.GetProperty("results").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
            if (expectedResults.Length != cases.Length)
                return $"{function.Name} fixture case count does not match the input pairs";
            for (int i = 0; i < cases.Length; i++)
            {
                string output = $"{function.Name}_{i}";
                outputs.Add(output);
                steps.Add(SetMathResult(steps.Count, output,
                    MathExpr(function.Name, IntExpr(cases[i].A), IntExpr(cases[i].B))));
            }
        }

        var (engine, state) = EmptyBoard(db);
        var context = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        long faults = engine.Api.Vm.FaultedPrograms;
        var actualResults = engine.Api.Vm.RunLocalProgramMulti(
            new KismetProgram(steps, 0), context, null, outputs.ToArray());
        if (engine.Api.Vm.FaultedPrograms != faults)
            return "Int32 comparison finite boundary probe unexpectedly faulted the VM";

        foreach (var function in root.GetProperty("functions").EnumerateObject())
        {
            var expectedResults = function.Value.GetProperty("results").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
            for (int i = 0; i < cases.Length; i++)
            {
                string output = $"{function.Name}_{i}";
                if (!Equals(actualResults[output], expectedResults[i]))
                    return $"{function.Name}({cases[i].A},{cases[i].B}) returned "
                        + $"{actualResults[output] ?? "<null>"}; expected {expectedResults[i]}";
            }
        }

        return null;
    }

    private static string? KismetFModFiniteContract(CardDatabase db)
    {
        // The current client wrapper computes the quotient with cvttsd2si
        // (truncate toward zero) and delegates the remainder to UCRT fmod.
        // Keep this regression limited to finite, in-range operands: the
        // zero-divisor, NaN/Infinity, and conversion-overflow paths remain
        // explicitly unresolved in the native audit.
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var cases = new (double Dividend, double Divisor, int Quotient, double Remainder)[]
        {
            (7.5, 2.0, 3, 1.5),
            (-7.5, 2.0, -3, -1.5),
            (7.5, -2.0, -3, 1.5),
            (-7.5, -2.0, 3, -1.5),
        };
        var steps = new List<KismetStep>(cases.Length);
        var outputs = new List<string>(cases.Length * 2);
        for (int i = 0; i < cases.Length; i++)
        {
            string quotient = $"quotient{i}";
            string remainder = $"remainder{i}";
            outputs.Add(quotient);
            outputs.Add(remainder);
            steps.Add(SetMathResult(i, quotient, MathExpr("FMod",
                new KismetExpr { Float = cases[i].Dividend },
                new KismetExpr { Float = cases[i].Divisor },
                LocalExpr(remainder))));
        }

        var result = engine.Api.Vm.RunLocalProgramMulti(
            new KismetProgram(steps, 0), ctx, null, outputs.ToArray());
        for (int i = 0; i < cases.Length; i++)
        {
            int actualQuotient = result[$"quotient{i}"] is int q ? q : int.MinValue;
            double actualRemainder = result[$"remainder{i}"] is double r ? r : double.NaN;
            if (actualQuotient != cases[i].Quotient || actualRemainder != cases[i].Remainder)
            {
                return $"FMod({cases[i].Dividend},{cases[i].Divisor}) returned "
                    + $"q={result[$"quotient{i}"] ?? "<null>"}, r={result[$"remainder{i}"] ?? "<null>"}; "
                    + $"expected q={cases[i].Quotient}, r={cases[i].Remainder}";
            }
        }

        return null;
    }

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

    private static string? KismetGetFactionEnumSdkMapping(CardDatabase db)
    {
        // Ordinals copied from the current client's KardsCore_enums.hpp EFactionEnum.
        // This guards the simulator's canonical-name projection only; it is not a
        // black-box claim about the native GetFactionEnum implementation.
        string[] factionNames =
        [
            "NotAvailable", "Germany", "Britain", "Japan", "Soviet", "USA",
            "France", "Italy", "Poland", "Finland", "Anzac", "Allies", "Neutral",
        ];
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var slots = Enumerable.Range(0, factionNames.Length).Select(i => $"faction{i}").ToArray();
        var steps = factionNames.Select((name, i) => SetMathResult(i, slots[i],
            MathExpr("GetFactionEnum", MathExpr("Conv_StringToName", StringExpr(name))))).ToArray();
        var result = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null, slots);

        for (int i = 0; i < factionNames.Length; i++)
        {
            if (!Equals(result[slots[i]], i))
                return $"GetFactionEnum canonical SDK value {factionNames[i]} returned {result[slots[i]] ?? "<null>"}; expected {i}";
        }

        return null;
    }

    private static string? KismetEnumCompareFactionSdkBranchContract(CardDatabase db)
    {
        // SDK KardsCore_enums.hpp defines EFactionEnum as 0..12 and
        // kards_enums.hpp labels EnumAreTheyEqual as Equal=0 / NotEqual=1.
        // This exhaustively guards the simulator's branch projection only;
        // the native EnumCompareFaction body is not present in the SDK dump.
        const int factionCount = 13;
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var steps = new List<KismetStep>(factionCount * factionCount);
        var slots = new List<string>(factionCount * factionCount);
        for (int left = 0; left < factionCount; left++)
        {
            for (int right = 0; right < factionCount; right++)
            {
                string slot = $"branch_{left}_{right}";
                slots.Add(slot);
                steps.Add(new KismetStep(steps.Count, "math", "EnumCompareFaction",
                    [IntExpr(left), IntExpr(right), LocalExpr(slot)], [2],
                    null, null, -1, null, null));
            }
        }

        var result = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null,
            slots.ToArray());
        for (int left = 0; left < factionCount; left++)
        {
            for (int right = 0; right < factionCount; right++)
            {
                int expected = left == right ? 0 : 1;
                string slot = $"branch_{left}_{right}";
                if (!Equals(result[slot], expected))
                    return $"EnumCompareFaction SDK branch ({left},{right}) returned {result[slot] ?? "<null>"}; expected {expected}";
            }
        }

        return null;
    }

    private static string? KismetEnumCompareSideSdkBranchContract(CardDatabase db)
    {
        // SDK kards_enums.hpp defines ESideEnum as 0..2 and
        // EnumAreTheyEqual as Equal=0 / NotEqual=1. This exhaustively guards
        // the simulator projection only; the native function body is absent.
        const int sideCount = 3;
        var (engine, state) = EmptyBoard(db);
        var ctx = new EffectContext { Engine = engine, State = state, Controller = Side.Left };
        var steps = new List<KismetStep>(sideCount * sideCount);
        var slots = new List<string>(sideCount * sideCount);
        for (int left = 0; left < sideCount; left++)
        {
            for (int right = 0; right < sideCount; right++)
            {
                string slot = $"branch_{left}_{right}";
                slots.Add(slot);
                steps.Add(new KismetStep(steps.Count, "math", "EnumCompareSide",
                    [IntExpr(left), IntExpr(right), LocalExpr(slot)], [2],
                    null, null, -1, null, null));
            }
        }

        var result = engine.Api.Vm.RunLocalProgramMulti(new KismetProgram(steps, 0), ctx, null,
            slots.ToArray());
        for (int left = 0; left < sideCount; left++)
        {
            for (int right = 0; right < sideCount; right++)
            {
                int expected = left == right ? 0 : 1;
                string slot = $"branch_{left}_{right}";
                if (!Equals(result[slot], expected))
                    return $"EnumCompareSide SDK branch ({left},{right}) returned {result[slot] ?? "<null>"}; expected {expected}";
            }
        }

        return null;
    }
}
