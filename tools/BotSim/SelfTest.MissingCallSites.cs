using KLink.Bot.Cards;
using KLink.Bot.Engine;
using KLink.Bot.Effects;
using KLink.Bot.Effects.Blueprint;

namespace KLink.Bot.Sim;
internal static partial class SelfTest
{
    private static KismetStep ProvenanceStep(int i, string op, string? fn = null, KismetExpr? src = null) =>
        new(i, op, fn, Array.Empty<KismetExpr>(), Array.Empty<int>(), src, null, -1, "result", null)
        { ByteOffset = i * 7, DestinationScope = "local" };

    private static string? MissingCallSitePaths(CardDatabase db)
    {
        var (engine, state) = EmptyBoard(db);
        var expr = new KismetExpr { Math = "MissingInnerMath" };
        var steps = new[] {
            ProvenanceStep(0,"call","MissingTopCall"),
            ProvenanceStep(1,"math","MissingTopMath"),
            ProvenanceStep(2,"set",src:new KismetExpr {Call="MissingExprCall",Args=new[] {expr,expr}}),
            ProvenanceStep(3,"return") };
        var program = new KismetProgram(steps,0);
        var ctx = new EffectContext {Engine=engine,State=state,Controller=Side.Left};
        engine.Api.Vm.RunLocalProgram(program,ctx,null,"result");
        engine.Api.Vm.RunLocalProgram(program,ctx,null,"result");
        var sites=engine.Api.Vm.MissingCallSites;
        if(sites.Count!=4 || sites.Sum(p=>p.Value)!=10 || engine.Api.Vm.MissingCallSiteCaptureFailures!=0)
            return "丢失调用点或聚合计数";
        foreach(var pair in sites)
        {
            var s=pair.Key;
            string path=s.Key=="math:MissingInnerMath" ? "src.args[0]|src.args[1]" : s.Key=="MissingExprCall" ? "src" : "$";
            if(s.ExpressionPath!=path || s.ByteOffset!=s.Instruction*7 || s.AssetName!=null
                || s.FunctionName!=null || s.RuntimeProgramSha256.Length!=64) return "来源路径/未归属身份错误";
            if(state.UnimplementedCalls.GetValueOrDefault(s.Key)!=pair.Value) return "健康计数与诊断不一致";
        }
        return null;
    }

    private static string? MissingCallSiteReentry(CardDatabase db)
    {
        const string hook="ProvenanceNestedProbe";
        using var cleanup=InstallSelectionDrawProbe(EffectDamageProbeName,hook);
        var lib=KismetLibrary.Default!;
        var locals=(IDictionary<string,IReadOnlyList<KismetStep>>)lib.Find(EffectDamageProbeName)!.Locals;
        locals[hook]=new[] {ProvenanceStep(0,"call","MissingNested"),ProvenanceStep(1,"return")};
        var (engine,state)=EmptyBoard(db);
        var self=state.CreateWithId(EffectDamageProbeName,Side.Left,70,CardLocation.BoardHqLeft,1);
        var outer=new KismetProgram(new[] {ProvenanceStep(0,"call",hook),ProvenanceStep(1,"call","MissingOuter"),ProvenanceStep(2,"return")},0)
            {AssetName="outer_asset",FunctionName="outer_export",SourceEntry=50};
        engine.Api.Vm.RunLocalProgram(outer,new EffectContext {Engine=engine,State=state,Controller=Side.Left,Self=self},null,"result");
        var sites=engine.Api.Vm.MissingCallSites.Keys.ToArray();
        var nested=sites.SingleOrDefault(s=>s.Key=="MissingNested");
        var parent=sites.SingleOrDefault(s=>s.Key=="MissingOuter");
        if(nested?.AssetName!=EffectDamageProbeName || nested.FunctionName!=hook || nested.SourceEntry!=0
            || parent?.AssetName!="outer_asset" || parent.FunctionName!="outer_export" || parent.Instruction!=1
            || nested.SelfCard!=EffectDamageProbeName || parent.SourceEntry!=50) return "重入覆盖外层来源";
        var fallback=lib.FindLocalProgram(EffectDamageProbeName+"_vet",hook);
        if(fallback?.AssetName!=EffectDamageProbeName || fallback.FunctionName!=hook) return "变体回退被误标为变体资产";
        var card=lib.Find(EffectDamageProbeName)!;
        foreach(var entry in card.Entrypoints)
        {
            var p=lib.FindProgram(EffectDamageProbeName,entry.Key)!;
            if(p.AssetName!=card.Name || p.FunctionName!=entry.Key || p.SourceEntry!=entry.Value) return "事件原始入口身份丢失";
        }
        return null;
    }

    private static string? MissingCallSiteIsolation(CardDatabase db)
    {
        string? snapshot=null;
        foreach(bool capture in new[] {false,true})
        {
            var (engine,state)=EmptyBoard(db);
            engine.Api.Vm.CaptureMissingCallSites=capture;
            // A cycle in an unused later source makes diagnostic serialization fail,
            // while the VM's return instruction never evaluates that source.
            var args=new List<KismetExpr>(); var cycle=new KismetExpr {Args=args};args.Add(cycle);
            var p=new KismetProgram(new[] {ProvenanceStep(0,"call","MissingIsolated"),ProvenanceStep(1,"return",src:cycle)},0);
            engine.Api.Vm.RunLocalProgram(p,new EffectContext {Engine=engine,State=state,Controller=Side.Left},null,"result");
            string actual=state.SnapshotJson();
            if(snapshot!=null && snapshot!=actual || state.Random.ConsumedCount!=0
                || engine.Api.Vm.FaultedPrograms!=0 || state.UnimplementedCalls.GetValueOrDefault("MissingIsolated")!=1
                || engine.Api.Vm.MissingCallSites.Count!=0 || engine.Api.Vm.MissingCallSiteCaptureFailures!=(capture?1:0))
                return "可选诊断改变执行状态或缺口计数";
            snapshot=actual;
        }
        return null;
    }

    private static string? MissingCallSiteLimits(CardDatabase db)
    {
        var (engine,state)=EmptyBoard(db);
        var ctx=new EffectContext {Engine=engine,State=state,Controller=Side.Left};
        var p=new KismetProgram(new[] {ProvenanceStep(0,"call","MissingBounded"),ProvenanceStep(1,"return")},0);
        void Run(KismetProgram program)=>engine.Api.Vm.RunLocalProgram(program,ctx,null,"result");
        Run(p);Run(p with {AssetName="asset"});Run(p with {Steps=new[] {ProvenanceStep(0,"call","MissingChanged"),ProvenanceStep(1,"return")}});
        if(engine.Api.Vm.MissingCallSites.Keys.Select(s=>s.RuntimeProgramSha256).Distinct().Count()!=3) return "程序变更未改变指纹";
        for(int i=3;i<KismetVm.MaxMissingCallSites+2;i++)Run(p with {FunctionName="bounded_"+i});
        Run(p);
        if(engine.Api.Vm.MissingCallSites.Count!=KismetVm.MaxMissingCallSites || engine.Api.Vm.MissingCallSitesOmitted!=2
            || engine.Api.Vm.MissingCallSites.First().Value!=2 || engine.Api.Vm.FaultedPrograms!=0
            || state.UnimplementedCalls.Values.Sum()!=KismetVm.MaxMissingCallSites+3) return "容量限制丢失健康计数或已存在站点聚合";
        return null;
    }

    private static string? MissingCallSiteRealDisplay(CardDatabase db)
    {
        const string name="card_unit_75th_rifle_regiment";
        var p=KismetLibrary.Default!.FindLocalProgram(name,"SpawnCopy");
        if(p==null)return "缺少真实SpawnCopy程序";
        string? snapshot=null;long? random=null;
        foreach(bool capture in new[] {false,true})
        {
            var (engine,state)=EmptyBoard(db);
            var self=state.CreateWithId(name,Side.Left,70,CardLocation.BoardHqLeft,1);
            engine.Api.Vm.CaptureMissingCallSites=capture;
            engine.Api.Vm.RunLocalProgram(p,new EffectContext {Engine=engine,State=state,Controller=Side.Left,Self=self},null,"result");
            if(snapshot!=null && snapshot!=state.SnapshotJson() || random!=null && random!=state.Random.ConsumedCount
                || engine.Api.Vm.FaultedPrograms!=0 || state.UnimplementedCalls.GetValueOrDefault("math:GetDisplayName")!=1
                || state.UnimplementedCalls.GetValueOrDefault("math:PrintString")!=1) return "真实链执行发生变化或未经过显示缺口";
            snapshot=state.SnapshotJson();random=state.Random.ConsumedCount;
            if(!capture)continue;
            foreach(var (key,i,bo,path) in new[] {("math:GetDisplayName",0,0,"src"),("math:PrintString",90,122,"$")})
            {
                var site=engine.Api.Vm.MissingCallSites.Keys.SingleOrDefault(s=>s.Key==key);
                if(site?.SelfCard!=name || site.AssetName!=name || site.FunctionName!="SpawnCopy" || site.SourceEntry!=0
                    || site.Instruction!=i || site.ByteOffset!=bo || site.ExpressionPath!=path) return "真实显示调用来源不符IR";
            }
        }
        return null;
    }
}
