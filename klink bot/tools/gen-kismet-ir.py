"""把卡牌蓝图的完整字节码（cards.full.json）压成可解释的 IR（card-ir.json）。

为什么要这一步
--------------
反编译产物原本只有「调用了哪些函数」的**集合**，没有数据流。
但完整字节码里的局部变量命名天然携带数据流信息：

    {"Inst":"Let","Variable":{"Variable Name":"tempCard"},
     "Expression":{"Variable Name":"CallFunc_GetLocationCardBySide_card"}}

`CallFunc_<函数名>_<参数名>` 就是那个调用的输出槽。配合 JumpIfNot 给的控制流，
就能把一张卡的效果还原成「一串有输入输出的步骤」。

事件入口
--------
卡的事件 stub（OnPlayedFromHand 等）里写死了 `ExecuteUbergraph_X(<entry>)`，
而 `<entry>` 正好等于 ubergraph 里该事件链第一条语句的 StatementIndex
（已在 1636 张卡上验证，1636/1636 命中）。所以执行就是：
从 StatementIndex == entry 的语句开始顺序执行，遇到 Return 结束。

输出 IR 形状
------------
{
  "card_event_aans": {
    "programs": {
      "OnPlayedFromHand": {"entry": 10, "steps": [ ... ]}
    }
  }
}

每步（step）：
  {"i":10, "op":"call", "fn":"GainKreditSlot", "args":[...], "outs":[]}
  {"i":119,"op":"set",  "dst":"tempCard", "src":{...}}
  {"i":167,"op":"jumpIfNot", "cond":{...}, "to":252}
  {"i":252,"op":"return"}

每个参数/表达式（expr）：
  {"var":"tempCard"}                    变量读取
  {"int":3} {"bool":true} {"str":"x"} {"obj":"/Script/..."}
  {"self":true} {"none":true}
  {"call":"IsValid","args":[...]}       内联调用
  {"math":"Add_IntInt","args":[...]}    数学节点
  {"unknown":"EX_Foo"}                  未支持的指令（VM 会记录并跳过）

用法：
    python gen-kismet-ir.py <cards.full.json> <输出.json> [--only-card 卡名]
"""

import argparse
import json
import sys
from pathlib import Path

# ---- 指令分类 ----

LITERALS = {
    "IntConst": lambda e: {"int": e.get("Value", 0)},
    "ByteConst": lambda e: {"int": e.get("Value", 0)},
    "Int64Const": lambda e: {"int": e.get("Value", 0)},
    "FloatConst": lambda e: {"float": e.get("Value", 0.0)},
    "StringConst": lambda e: {"str": e.get("Value", "")},
    "ObjectConst": lambda e: {"obj": e.get("Object", "")},
    "NameConst": lambda e: {"name": e.get("Value", "")},
    "True": lambda e: {"bool": True},
    "False": lambda e: {"bool": False},
    "BoolConst": lambda e: {"bool": bool(e.get("Value", False))},
    "Self": lambda e: {"self": True},
    "Nothing": lambda e: {"none": True},
    "NoObject": lambda e: {"none": True},
    "EndOfScript": lambda e: {"none": True},
}

CALL_INSTS = {
    "LocalVirtualFunction", "LocalFinalFunction", "VirtualFunction",
    "FinalFunction", "LocalFinalFunctionPtr", "LocalVirtualFunctionPtr",
}

SET_INSTS = {
    "Let", "LetObj", "LetBool", "LetInt", "LetInt64", "LetFloat", "LetStr",
    "LetName", "LetText", "LetMulticastDelegate", "LetDelegate",
    "LetValueOnPersistentFrame", "LetDynamic", "LetInterface",
}

JUMP_INSTS = {"JumpIfNot", "JumpIf", "Jump", "ComputedJump", "PushExecutionFlow", "PopExecutionFlow"}

# ---- 卡自己的「局部函数」（不是事件，也不是 ubergraph）----
#
# 这些是每个卡蓝图自己实现的普通函数，编译成独立 export，**不在 ubergraph 里**，
# 所以旧版生成器完全没编它们 —— IR 里只剩一个 `call GetPlayFromHandDamage`，
# 解释器查派发表查不到，返回 0。
#
# 目前收两个：
#   · `GetPlayFromHandDamage`（全卡池 129 张卡定义它，其中 78 张的函数体
#     就是 `damage = <字面量>`）；
#   · `GetChooseSpawnCards`（36 张卡定义它）—— `selectCardToDraw` 的候选表来源，
#     签名是 `GetChooseSpawnCards(out cards, out markAsSeen, out keepOrder)`
#     （见 `BP_CardFunctions.selectCardToDraw` 的 `L_0680` 调用点）。
#     它的函数体是每卡不同的过滤（例：`card_event_pams` = 「英国 + 指令 + 总费<5」）。
# 不收别的，是为了把改动面压到最小 —— 别的局部函数（`CanPlayFromHand` /
# `ShouldHighlightInHand` …）有各自的调用路径，不在这次修复范围内。
#
# ⚠️ 2026-09-27（P0 第 3 族第一步）：把范围扩到**卡私有函数的实测清单**
# —— `out/audit/private-fns-cards-only.txt`（扫描脚本 `out/audit/scan-private-cards.py`，
# 判据：定义在 `card_*` 资产里、被 ubergraph 以 `FunctionName` 调用、且不是事件 stub）。
# 一共 76 个；其中 9 个派发表里已有手写 C# 替身（那 9 个**保留替身优先**，见 KismetVm 的注释），
# 剩下 67 个此前既没有函数体、也没有替身 ⇒ 调用被记成 Unimplemented、等于什么都不做。
#
# 收录范围是**直接调用**的 76 个；它们的函数体若再调用别的私有函数，那次调用仍会被记
# Unimplemented（= 与其它未实现原语同一种处理），不影响本次落地的安全性。
# 清单生成脚本：`python out/audit/p0-make-locals.py`。
LOCAL_FUNCTIONS = {
    "GetPlayFromHandDamage",
    "GetChooseSpawnCards",
    # ---- `CanPlayFromHand`（2026-10-02，目标合法性门）----
    #
    # 权威依据：客户端选目标时**逐个候选**调它。`BP_Logic` 里那段枚举
    # （`Generated/_deps/BP_Logic.g.cs:1235-1355`）就是：
    #   遍历 `GetAllCardInBattle` → `_card.targetOverride = 候选` →
    #   `_card.CanPlayFromHand(out canIt, …, out targetedCard)` →
    #   `IsValid(targetedCard)` → `CanSelectAsTarget(候选, _card, byPlayFromHand=true)`
    # ⇒ **「这张牌能指哪些目标」的判据在卡自己的 `CanPlayFromHand` 里**，
    #   `CanSelectAsTarget`（规则库）只管隐蔽/敌方指令/费用/被指方自身那几道。
    #
    # 全卡池实测（`klink bot/docs/card-effects.json` 的 `functions.CanPlayFromHand`）：
    # **438 张卡**定义它，其中带目标类型判据的 —— `IsAirUnit` 13 张 / `IsGroundUnit` 17 /
    # `IsVeteran` 2 / `IsBomber` 2 / `IsFighter` 3 / `IsSameSideUnit` 131 /
    # `IsUnit` 196 / `IsInfantry` 27 / `IsTank` 26 / `IsArtillery` 2。
    # 不收它 ⇒ 内核**完全没有**「目标类型」这个概念，`NnPolicy` 只能按
    # 「敌方场上 + 敌方 HQ」的**超集**枚举，于是 AI 会指定客户端认为非法的目标
    # （真人玩家报告：`card_event_aa_barrage`「Target air unit must retreat」被指到地面单位上，
    #  客户端静默不执行 ⇒ 记牌器 +1、场上无变化 = 「虚空牌」的第三个来源）。
    "CanPlayFromHand",
    # ---- 以下 76 个来自 out/audit/private-fns-cards-only.txt ----
    "ApplyTheBuff",
    "RemoveBuff",
    "didPlayBritishInfantryLastTurn",
    "RemoveTheBuff",
    "ApplyBuff",
    "isSecondOrderThisTurn",
    "Random Card",
    "CheckAndApplyKreditBuff",
    "hasGuardAdjacentUnit",
    "updateCustomJsonIfNeeded",
    "DeactivateOtherHTDP",
    "ApplyAttackBuff",
    "RemoveAttackBuff",
    "get_adjacent_unit_count",
    "checkAndUpdateBuff",
    "getSkirmishBP",
    "RandomChooseCards",
    "CheckAndUpdateCost",
    "GetRandomBritishAir",
    "anyOrderPlayedThisTurn",
    "CheckFriendlyAirUnitsOnBoard",
    "hasPlayedOrderThisTurn",
    "DisableOtherFriendly7th",
    "getUnseenCardsOppositeSide",
    "ApplyAndCorrectBuff",
    "UpdateBuff",
    "updateBuffs",
    "clearBuffs",
    "clearTargetCard",
    "RemoveAllOverrides",
    "AddPinnedOverride",
    "AddOverrideToPinnedCardsOnYourSide",
    "_qualifiesForUnpinning",
    "RemovePinnedOverride",
    "PickRandomCard",
    "TriggerMultipleDeploymentEffects",
    "alreadyHasTheAbility",
    "isAlreadyAffectedByOtherGreater",
    "isAlreadyAffectedByOtherYamato",
    "Has Opposite Side Air On Board",
    "_checkAndSetBuff",
    "DisableDoubleCostAbility",
    "anyFriendlyA6M2Active",
    "EnableDoubleCostAbility",
    "get_adjacent_japan_units_count",
    "CompareHandCards",
    "DiscardHighestCostCardBySide",
    "getMoveReason",
    "MoveCards",
    "_onCardDefenseChanged",
    "CheckFriendlyUnitsOnBoard",
    "GetOppositeUnitsOnBoard",
    "getUnpinnedEnemyUnits",
    "GetSeenCardsFromOppositeSide",
    "RandomlyChooseTank",
    "RandomChooseA_Tank",
    "GetUSAUnitsAndKredits",
    "GetRandomKreditCombo",
    "doIControl3opCostUnit",
    "SpawnShermanInHand",
    "SpawnUnitsWithKreditCombo",
    "ShouldSpawnFrontline",
    "CreateLightInfantryArr",
    "GiveAlpineToLightInfantry",
    "SpawnChooseOneCards",
    "getPossibleCardsFromStaticCards",
    "getTwoCardsFromPossibleCards",
    "_isBigRedOne",
    "checkAndUpdateBuffOnCard",
    "checkAndUpdateBuffOnAllCards",
    "isPattonPlayedThisTurn",
    "Get Random Unit on Board",
    "Random_3_SovietUnits",
    "ShouldTriggerAbility",
}


# ⚠️ 2026-09-30（P1 Deployment）：**带出参的事件处理函数**是独立函数图，不在 ubergraph 里。
#
# 蓝图的 `ExecuteUbergraph_X(<entry>)` 模型只覆盖「无出参的事件」。一旦事件带 `out` 参数
# （`OnBeforeOtherCardDeploymentTrigger(..., out cancelDeploymentEffect)`、
# `OnDeploymentEffectTriggered(..., out TriggerMultiple)`、
# `OnDestructionEffectTriggered(...)`、`OnCardDealDamage_ModifyDamageDealt(..., out damage)`…），
# UE 就把它编成**独立 export 的函数**，函数体自己就是从 StatementIndex 0 开始的一串语句，
# 根本不出现 `ExecuteUbergraph` 调用。
#
# 后果（实测，脚本 `out/audit/p1-standalone-events.py`）：全部资产里这类函数有 193 个，
# 其中 `card_*` 卡上的 163 个**全部**不在 `entrypoints` 里 —— 内核 `FireTrigger` 按名字查
# 不到程序，于是这些事件永远不触发。命中的正是 P1 的几条主线：
#   `OnCardDealDamage_ModifyDamageDealt`(33 张) / `OnOtherCardDealDamageAddDamage`(29) /
#   `OnCardDealDamage`(28) / `OnOtherCardAttacks`(20) /
#   `OnOtherCardDealDamageAddDamageAfterCalc`(15) / `OnCounterMeasureTriggered`(13) /
#   `OnBeforeOtherCardDeploymentTrigger`(4) / `OnDestructionEffectTriggered`(4) / …
#
# 处理方式：**编进 `locals`**（和卡私有函数同一张表）。`locals` 本来就是
# 「独立 export、语句下标自成一套」的容器，形状完全吻合；内核侧
# `KismetLibrary.FindProgram` 在 `entrypoints` 查不到时回退查 `locals`。
#
# 判据（三条同时成立）：
#   1. 函数名以 `On` 开头（事件命名约定）
#   2. 函数体里**没有** `ExecuteUbergraph` 调用（有的话它就是 stub，入口已经在 `entrypoints` 里）
#   3. 不是 `__DelegateSignature`（那是 UI 委托签名，不是事件实现）
def is_standalone_event(fname, finfo):
    if not fname.startswith("On"):
        return False
    if fname.endswith("__DelegateSignature"):
        return False
    bc = (finfo or {}).get("bytecode") or []
    if not bc:
        return False
    return "ExecuteUbergraph" not in json.dumps(bc, ensure_ascii=False)


def local_program(bc):
    """把一个局部函数的字节码编成步骤表（形状与 ubergraph 的 steps 一致）。"""
    steps = []
    for e in bc:
        if not isinstance(e, dict):
            continue
        s = step_of(e)
        if s is None:
            continue
        s["i"] = e.get("StatementIndex", 0)
        s["bo"] = e.get("bo", 0)
        steps.append(s)
    steps.sort(key=lambda s: s["i"])
    return steps


def expr(e):
    """递归把一个表达式压成紧凑形式。"""
    if e is None:
        return {"none": True}
    if not isinstance(e, dict):
        return {"int": 0}

    inst = e.get("Inst", "")

    if inst in LITERALS:
        return LITERALS[inst](e)

    if inst in ("LocalVariable", "InstanceVariable"):
        return {"var": e.get("Variable Name", "")}

    if inst in CALL_INSTS:
        fn = e.get("FunctionName") or e.get("Function") or ""
        return {"call": fn, "args": [expr(p) for p in (e.get("Parameters") or [])]}

    if inst == "CallMath":
        fn = e.get("Function", "")
        return {"math": fn, "args": [expr(p) for p in (e.get("Parameters") or [])]}

    if inst == "Context":
        # Context 只是「在哪个对象上调用」，直接透传内部表达式
        inner = expr(e.get("Expression"))
        ctx = expr(e.get("Context"))
        if "ctx" not in inner:
            inner["ctx"] = ctx
        return inner

    if inst == "StructConst":
        # ⚠️ 不能直接 `{"struct": e.get("Value")}` —— 那样标签名会被静默丢掉。
        #
        # `getHasGameplayTag` 的实参是 `/Script/GameplayTags.GameplayTag` 结构体常量，
        # UAssetAPI 序列化出来是：
        #     {"Inst":"StructConst","Struct":"/Script/GameplayTags.GameplayTag",
        #      "Properties":{"Missing property name0":[{"Inst":"NameConst","Value":"subtype.t34"}]}}
        # 而 `Value` 键**根本不存在** ⇒ 旧版输出 `{"struct": null}`，
        # 于是 VM 里 `getHasGameplayTag` 拿到的 tag 是 null，
        # 所有判子类型的卡（`card_unit_214th_amur` 的 subtype.t34、
        # `card_event_committed_crew` 的 subtype.spitfire …）全部静默失效。
        # 实测：全部 46 个 getHasGameplayTag 调用点都是这个形状。
        #
        # 结构体成员没有名字（UAssetAPI 给的是 "Missing property name0"），
        # 所以把成员里的标量值**摊平成数组**交给 VM，由它当「tag 列表」用。
        members = []
        for props in (e.get("Properties") or {}).values():
            for item in (props if isinstance(props, list) else [props]):
                v = expr(item)
                if any(k in v for k in ("str", "name", "int", "bool")):
                    members.append(v)
        return {"struct": e.get("Struct", ""), "array": members}

    if inst in ("ArrayConst", "SetArray"):
        return {"array": [expr(x) for x in (e.get("Values") or [])]}

    if inst == "StringConstArray":
        return {"array": [{"str": s} for s in (e.get("Values") or [])]}

    if inst == "PropertyConst":
        return {"prop": e.get("Value", "")}

    if inst == "TextConst":
        return {"str": json.dumps(e.get("Value"), ensure_ascii=False)[:120]}

    # 兜底：记下来，VM 执行时会计入未实现
    return {"unknown": inst or e.get("Inst", "?")}


def outs_of(call_expr, fn_name):
    """挑出这次调用的输出槽。

    判据：参数是 LocalVariable，且名字形如 CallFunc_<函数名>_<参数名>。
    （已用 card_event_aans 的 GetLocationCardBySide / ChangeDefense 验证过。）
    """
    prefix = f"CallFunc_{fn_name}_"
    outs = []
    for i, p in enumerate(call_expr.get("args", [])):
        if isinstance(p, dict) and "var" in p and p["var"].startswith(prefix):
            outs.append({"param": i, "slot": p["var"]})
    return outs


def step_of(e):
    """把一条语句压成 IR step。返回 None 表示跳过。"""
    inst = e.get("Inst", "")

    if inst == "ComputedJump":
        return None  # 入口分派，由 entries 处理

    # ---- Execution Flow 栈：Blueprint 用它做「共享代码块 + 返回」----
    # UAssetAPI 的 KismetSerializer 把 EX_PushExecutionFlow.PushingAddress 序列化成 "Offset"，
    # EX_PopExecutionFlowIfNot.BooleanExpression 序列化成 "Condition"。
    if inst == "PushExecutionFlow":
        return {"op": "pushFlow", "to": e.get("Offset", -1)}
    if inst == "PopExecutionFlow":
        return {"op": "popFlow"}
    if inst == "PopExecutionFlowIfNot":
        return {"op": "popFlowIfNot", "cond": expr(e.get("Condition"))}

    if inst in JUMP_INSTS:
        if inst == "JumpIfNot":
            return {"op": "jumpIfNot", "cond": expr(e.get("Condition")), "to": e.get("Offset", -1)}
        if inst == "JumpIf":
            return {"op": "jumpIf", "cond": expr(e.get("Condition")), "to": e.get("Offset", -1)}
        if inst == "Jump":
            return {"op": "jump", "to": e.get("Offset", -1)}
        return None

    if inst == "Return":
        return {"op": "return"}

    # SetArray：MakeArray 节点 —— 把若干值组装成一个数组变量
    if inst == "SetArray":
        left = e.get("LeftSideExpression") or {}
        dst = ((left.get("Variable") or {}).get("Variable Name")) or left.get("Variable Name", "")
        vals = e.get("Values") or []
        return {"op": "setArray", "dst": dst, "values": [expr(v) for v in vals]}

    if inst in SET_INSTS:
        var = e.get("Variable") or {}
        dst = var.get("Variable Name", "")
        # 输出槽的赋值（CallFunc_X_Y = ...）由 call 的 outs 处理，这里可能是二次赋值
        return {"op": "set", "dst": dst, "src": expr(e.get("Expression"))}

    # 顶层调用：可能是裸调用，也可能包在 Context 里
    if inst in CALL_INSTS or inst == "CallMath" or inst == "Context":
        ex = expr(e)
        if "call" in ex:
            step = {"op": "call", "fn": ex["call"], "args": ex.get("args", []),
                    "outs": outs_of(ex, ex["call"])}
            # Context 里的 Context 就是**接收者** —— 很多谓词只有一个输出参数，
            # 例如 card.IsUnit() 编译成 Context(card) → IsUnit(out isIt)。
            # 丢了它，IsUnit 就没有主语。
            if "ctx" in ex:
                step["recv"] = ex["ctx"]
            return step
        if "math" in ex:
            # ⚠️ **必须带 outs**。`CallMath` 也可能有输出参数 —— 实测
            # `/Script/kards.FunctionLibrary.EnumCompareFaction(卡.faction, 1, out Branches)`
            # 就被编成 `CallMath`，结果写在 `CallFunc_EnumCompareFaction_Branches` 里。
            # 旧版只对 `call` 算 outs，于是那个 out 槽永远是 null：
            #   `CmpSuccess = NotEqual_ByteByte(null, 0)` → false
            #   → `JumpIfNot(CmpSuccess, <效果>)` 恒成立 → **效果无条件执行**。
            # 实测症状：`card_unit_3_panzergrenadier`（"after you operate a **German** unit"）
            # 对任何阵营的攻击都会 +1+1，连美国 M2A4 操作也给它涨。
            return {"op": "math", "fn": ex["math"], "args": ex.get("args", []),
                    "outs": outs_of(ex, ex["math"])}
        # Context 里的 Let 之类
        inner = e.get("Expression")
        if isinstance(inner, dict):
            return step_of(inner)
        return {"op": "expr", "e": ex}

    if inst in SET_INSTS:
        return {"op": "set", "dst": (e.get("Variable") or {}).get("Variable Name", ""),
                "src": expr(e.get("Expression"))}

    return {"op": "unknown", "inst": inst}


def build_program(ubergraph_bc, entry):
    """按 StatementIndex 排序，从 entry 开始取该事件链的语句。"""
    ordered = sorted((e for e in ubergraph_bc if isinstance(e, dict)),
                     key=lambda e: e.get("StatementIndex", 0))

    steps = []
    started = False
    for e in ordered:
        idx = e.get("StatementIndex", 0)
        if not started:
            if idx != entry:
                continue
            started = True
        s = step_of(e)
        if s is None:
            continue
        s["i"] = idx
        steps.append(s)
        if s["op"] == "return":
            break

    return steps


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--only-card", help="只处理这一张卡（调试用）")
    args = ap.parse_args()

    print(f"读取 {args.src} ...")
    assets = json.loads(Path(args.src).read_text(encoding="utf-8-sig"))["assets"]
    print(f"  资产 {len(assets)}")

    result = {}
    stat = {"cards": 0, "entries": 0, "steps": 0, "unknown_ops": 0, "no_entry": 0, "locals": 0}

    for path, info in assets.items():
        if not info.get("ok"):
            continue
        name = Path(path).stem
        if args.only_card and name != args.only_card:
            continue

        fns = info.get("functions") or {}
        ug_name = next((k for k in fns if k.startswith("ExecuteUbergraph")), None)

        # 卡自己的局部函数 / 独立事件函数（见 LOCAL_FUNCTIONS 与 is_standalone_event 的注释）。
        # ⚠️ 必须在 `ug_name is None` 的提前返回**之前**收集：
        #    `card_unit_petlyakov_pe_2ft` 这类卡**只有**一个独立事件函数、
        #    根本没有 ubergraph —— 旧实现在这里 `continue`，整张卡进不了 IR。
        locals_ = {}
        for fname, finfo in fns.items():
            if fname == ug_name:
                continue
            if not (fname in LOCAL_FUNCTIONS or is_standalone_event(fname, finfo)):
                continue
            fsteps = local_program((finfo or {}).get("bytecode") or [])
            if fsteps:
                locals_[fname] = fsteps

        if ug_name is None:
            if locals_:
                result[name] = {"asset": path, "entrypoints": {}, "steps": [], "locals": locals_}
                stat["cards"] += 1
                stat["locals"] += len(locals_)
                stat["no_ug"] = stat.get("no_ug", 0) + 1
            continue

        ug_bc = (fns[ug_name] or {}).get("bytecode") or []
        if not ug_bc:
            continue

        # 整个 ubergraph 编成一个程序；每个事件只是不同的入口点。
        #
        # 为什么不再「按事件切片」：实测全部 14,659 条跳转的 CodeOffset
        # **100% 都落在整个函数的 StatementIndex 集合里**，而按
        # 「entry → 第一个 Return」切出来的子集只能覆盖 69%。
        # 也就是说事件链之间共享语句（尤其循环体），切片必然丢控制流。
        steps = []
        for e in ug_bc:
            s = step_of(e)
            if s is None:
                continue
            s["i"] = e.get("StatementIndex", 0)
            # `bo` = 字节码偏移。**必须留着**：整条 ubergraph 的语句在 IR 里是按
            # `i`（StatementIndex）排序的，而真正的执行顺序（"下一条语句"）要按 `bo` 排。
            # 两者**不等价** —— 实测 `card_unit_85_pioneer_company` 的初始化块
            # 在 `bo` 顺序里排在事件体**后面**（初始化块 bo 575..1990，
            # 事件体 bo 2006+），按 `i` 顺序把两条链拼起来就会拼错。
            # 解释器要靠它算"顺序下落的下一条"，否则会掉进初始化块、死循环。
            s["bo"] = e.get("bo", 0)
            steps.append(s)

        if not steps:
            continue
        steps.sort(key=lambda s: s["i"])

        # 收集入口点：事件 stub 里的 ExecuteUbergraph_X(<entry>)
        entries = {}
        for fname, finfo in fns.items():
            if fname == ug_name:
                continue
            for e in ((finfo or {}).get("bytecode") or []):
                if "ExecuteUbergraph" not in json.dumps(e, ensure_ascii=False):
                    continue
                for p in e.get("Parameters", []):
                    if p.get("Inst") == "IntConst":
                        entries[fname] = p["Value"]
                        break
                break

        if not entries:
            stat["no_entry"] += 1
            continue

        # 卡自己的局部函数（见 LOCAL_FUNCTIONS 的注释）。
        # 关键：局部函数是**独立 export**，语句下标从 0 开始，与 ubergraph 的
        # StatementIndex 不共享命名空间 —— 所以必须单独存一份，不能拼进 steps。
        # （`locals_` 已经在上面收集好了，含独立事件函数。）

        node = {"asset": path, "entrypoints": entries, "steps": steps}
        if locals_:
            node["locals"] = locals_
            stat["locals"] = stat.get("locals", 0) + len(locals_)

        result[name] = node
        stat["cards"] += 1
        stat["entries"] += len(entries)
        stat["steps"] += len(steps)
        stat["unknown_ops"] += sum(1 for s in steps if s.get("op") == "unknown")

    Path(args.dst).parent.mkdir(parents=True, exist_ok=True)
    Path(args.dst).write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    size = Path(args.dst).stat().st_size
    print(f"  写出 {stat['cards']} 张卡 / {stat['entries']} 个入口 / {stat['steps']} 步")
    print(f"  局部函数 / 独立事件函数: {stat['locals']}（其中只有独立事件的卡 {stat.get('no_ug', 0)} 张）")
    print(f"  未支持的指令: {stat['unknown_ops']}   无入口的卡: {stat['no_entry']}")
    print(f"  → {args.dst}  ({size/1e6:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
