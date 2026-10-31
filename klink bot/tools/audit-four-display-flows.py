"""Reproduce the four scoped dispatch-gap dependency reviews; not a card certifier."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
IR = ROOT / 'klink bot/docs/card-ir.json'
PARSED = Path('D:/Codex Workshop/kards-install-review/parsed')
OUT = ROOT / 'out/audit/systematic-rules'
STEM = 'four-display-flows-20261031'
CASES = {
    'card_event_carrier_battle': ('steps', 'Format', 680, 791, 'SpawnCardInHandBySide', 6),
    'card_event_seize_the_initiative': ('steps', 'Format', 684, 801, 'SpawnCardInHandBySide', 6),
    'card_unit_115th_separate_btn': ('steps', 'Format', 2725, 2810, 'AddNumberToText', 1),
    'card_unit_75th_rifle_regiment': ('SpawnCopy', 'GetDisplayName', 0, 90, 'PrintString', 1),
}


def walk(value):
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from walk(child)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def refs(value):
    return {v['var'] for v in walk(value) if isinstance(v, dict)
            and 'var' in v and v.get('scope') == 'local'}


def review(card, obj, spec):
    program, source_fn, source_i, sink_i, sink_fn, sink_arg = spec
    steps = obj['steps'] if program == 'steps' else obj['locals'][program]
    source = next(s for s in steps if s['i'] == source_i)
    require(source['src']['math'] == source_fn, f'{card}: source changed')
    tainted = {source['dst']}
    # Flow-insensitive overapproximation in one function. Reassignment never
    # kills taint; every use is inspected, including branches and receivers.
    # Treat all call outputs as dependent, even at the reviewed display sink.
    while True:
        before = set(tainted)
        for step in steps:
            if refs(step) & tainted:
                if step['op'] == 'set' and step.get('dst_scope') == 'local':
                    tainted.add(step['dst'])
                tainted.update(o['slot'] for o in step.get('outs', []) if 'slot' in o)
        if tainted == before:
            break
    uses = []
    for step in steps:
        touched = sorted(refs(step) & tainted)
        if not touched:
            continue
        if step['op'] == 'set' and step.get('dst_scope') == 'local':
            require(step.get('src', {}).get('math') in {'Conv_TextToString', 'Concat_StrStr'},
                    f'{card}: unreviewed dependent assignment {step}')
            kind = 'local_conversion'
        else:
            require(step['i'] == sink_i and step.get('fn') == sink_fn,
                    f'{card}: unexpected dependent consumer {step}')
            indexes = [i for i, arg in enumerate(step['args']) if refs(arg) & tainted]
            # An output slot is represented in args too. Exclude only declared
            # output parameters; output reads elsewhere are still checked.
            out_indexes = {o['param'] for o in step.get('outs', [])}
            require([i for i in indexes if i not in out_indexes] == [sink_arg],
                    f'{card}: unexpected sink arguments {indexes}')
            require(not refs(step.get('recv')) & tainted, f'{card}: tainted receiver')
            kind = 'reviewed_display_or_log_argument'
        uses.append({'i': step['i'], 'bo': step.get('bo'), 'kind': kind,
                     'local_references': touched, 'step': step})
    require(any(u['i'] == sink_i for u in uses), f'{card}: missing sink')

    matches = list(PARSED.rglob(card + '.functions.json'))
    require(len(matches) == 1, f'{card}: ambiguous/missing parsed evidence')
    raw_path = matches[0]
    raw = json.loads(raw_path.read_text(encoding='utf-8'))
    raw_fn = 'ExecuteUbergraph_' + card if program == 'steps' else program
    statements = raw[raw_fn]['bytecode']
    raw_uses = [s for s in statements if any(
        isinstance(n, dict) and n.get('Inst') == 'LocalVariable'
        and n.get('Variable Name') in tainted for n in walk(s))]
    require({s['StatementIndex'] for s in raw_uses} == {source_i, *(u['i'] for u in uses)},
            f'{card}: raw/IR reference statement sets differ')
    raw_source = next(s for s in statements if s.get('StatementIndex') == source_i)
    raw_calls = [n for n in walk(raw_source) if isinstance(n, dict)
                 and n.get('Function') == source_fn]
    require(len(raw_calls) == 1, f'{card}: raw source mismatch')
    templates = [n['SourceString'] for n in walk(raw_calls[0])
                 if isinstance(n, dict) and n.get('Inst') == 'TextConst']
    return {'card': card, 'asset': obj['asset'], 'program': program,
            'source': source, 'dependent_locals': sorted(tainted), 'uses': uses,
            'sink': {'i': sink_i, 'fn': sink_fn, 'input_arg_zero_based': sink_arg},
            'parsed_evidence': {'path': str(raw_path), 'sha256': digest(raw_path),
                                'function': raw_fn, 'reference_statements': raw_uses},
            'raw_text_templates': templates,
            'ir_text_templates': [n['str'] for n in walk(source) if isinstance(n, dict) and 'str' in n],
            'conclusion': 'scoped result dependency terminates at reviewed display/log argument; no other consumer in this function'}


def main():
    cards = json.loads(IR.read_text(encoding='utf-8'))
    rows = [review(name, cards[name], spec) for name, spec in CASES.items()]
    sources = ['src/KLink.Bot/Effects/Blueprint/KismetVm.cs',
               'src/KLink.Bot/Effects/CardApi.cs', 'src/KLink.Bot/Effects/CardApiDispatch.cs',
               'klink bot/tools/audit-dynamic-text-boundary.py']
    report = {
        'schema_version': 1, 'kind': 'four_scoped_display_dependency_reviews',
        'ir': {'path': str(IR), 'sha256': digest(IR), 'authority': 'derived IR'},
        'source_files': {s: digest(ROOT / s) for s in sources},
        'method': 'function-local, flow-insensitive taint closure; fail on unexpected consumers; compare raw parsed reference statement sets',
        'summary': {'reviewed_assets': len(rows), 'sources': 4,
                    'reviewed_sinks': 4, 'unexpected_consumers': 0,
                    'raw_ir_reference_sets_match': True,
                    'lost_ir_text_templates': sum(bool(r['raw_text_templates']) and r['ir_text_templates'] == ['null'] for r in rows)},
        'independent_review': {
            'worker': 'deepseek-worker', 'job': '20261031-234547-four-display-flow-b124734d',
            'accepted_scope': 'Primary independently compared all four source indices, dependent-step indices, terminal functions and input argument positions: 4/4 match. Additional worker observations are not adopted as established facts.',
        },
        'validation': {'report_regeneration': 'byte-identical on repeat run',
                       'dispatch_gap': {'names': 45, 'calls': 1013, 'fingerprint': '16DB07EFB256B189'},
                       'runtime_changes': False},
        'reviews': rows,
        'runtime_review': [
            'KismetVm.EvalMath default records math:<name> then returns null; set/math steps advance pc. CardApi.NotifyUnimplemented only updates counters.',
            'Concat_StrStr tolerates null; missing display-name math does not itself skip SpawnCopy gameplay calls.',
            'DoSpawnInHand does not read argument 6; AddNumberToText dispatch is a no-op. These are current simulator adaptations, not proof of all client callee behavior.',
            'Carrier battle and seize initiative use explicit level numeric effects, ChangeKreditCost and JSON_SetInt/PersistCustomFields; see prior dynamic-text-boundary report.',
        ],
        'limitations': [
            'Parsed bytecode is a local extraction artifact with unresolved fields, not a fresh independently version-matched pak decode. Hashes identify evidence, not version equivalence.',
            'Raw text templates survive in parsed bytecode but are null strings in current IR; FormatArgumentData fields remain unresolved. Text is not fully restored.',
            'This checks dependencies of four specific return values, not whole cards, CFG reachability, inherited native behavior or every callee notification.',
            'Static gap baseline remains 45 names / 1013 calls; no runtime implementation or coverage accounting is changed.',
            'Display/log omission may still affect unsupported counters and training admission policies; no claim that these cards are fully certified for training.',
        ],
    }
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / (STEM + '.json')).write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    lines = ['# 四个 PvP 候选资产的显示依赖审查', '',
             '仅审查四个缺口表达式的结果用途；不认证整卡或全部原生调用语义。', '',
             '| 资产 | 来源 i | 最终用途（参数从 0 开始） |', '|---|---:|---|']
    for row in rows:
        sink = row['sink']
        lines.append(f"| `{row['card']}` | {row['source']['i']} | `{sink['fn']}` i={sink['i']} 参数 {sink['input_arg_zero_based']} |")
    lines += ['', '四条链均未发现其他局部消费者；原始解析记录与 IR 的相关变量引用语句索引集合一致。'
              '两张指令通过显式 level 数值和持久化实现递增效果，第 115 独立营的该结果进入文本显示，第 75 步兵团的名称进入 PrintString。', '',
              '原始模板分别为 `card_event_carrier_battle:{level}`、`card_event_seize_the_initiative:{level}`、`+{x}+{y}`；'
              '当前 IR 均丢失为字符串 `null`。无头规则范围内保留当前显示省略，不据此认定文本已还原。', '',
              'VM 未实现数学节点记录计数并返回 null；普通执行继续，第 75 步兵团后续生成调用不会仅因名称缺失而被跳过。'
              '未实现计数对训练准入的影响仍须单独核查。', '',
              '## 验证与边界', '',
              '脚本固定点传播局部变量依赖，检查所有消费者及调用接收者，并核对原始解析函数的引用语句集合；出现额外消费者立即失败。'
              '它是当前四条链的可重复范围检查，不是通用语义证明器。', '',
              '静态缺口口径保持 45 类 / 1013 调用，运行时代码未修改。'
              '原生调用内部、继承行为、结构体缺失字段及版本对应关系仍不由本报告认证。', '']
    (OUT / (STEM + '.md')).write_text('\n'.join(lines), encoding='utf-8')
    print(json.dumps(report['summary'], ensure_ascii=False))


if __name__ == '__main__':
    main()
