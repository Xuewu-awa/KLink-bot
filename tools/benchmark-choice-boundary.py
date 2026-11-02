"""Compare two built NNTrain snapshots without changing paired-v6 validation.

Each sample is a fresh process running the complete dump-clone-actions pipeline.
The caller supplies isolated snapshots with identical data and controlled sources.
Outputs must be new; a failed sample or any behavioral difference stops the run.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import time


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before', required=True, type=Path)
    parser.add_argument('--after', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    parser.add_argument('--repeats', type=int, default=3)
    args = parser.parse_args()
    require(1 <= args.repeats <= 10, 'Expected 1..10 repeats')
    roots = {k: getattr(args, k).resolve() for k in ('before', 'after')}
    binaries = {k: p/'tools/NNTrain/bin/Release/net10.0/NNTrain.dll' for k, p in roots.items()}
    for p in binaries.values():
        require(p.is_file(), f'Missing binary: {p}')
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    fixtures = [
        ('m1-native', 1, ['--fixture', 'm1'], 'native'),
        ('m1-last', 1, ['--fixture', 'm1'], 'last'),
        ('meta1-seed1', 1, ['--left-deck', '英日', '--right-deck', '日澳快攻'], 'native'),
        ('meta1-seed2', 2, ['--left-deck', '英日', '--right-deck', '日澳快攻'], 'native'),
        ('meta2-seed1', 1, ['--left-deck', '苏英爆破', '--right-deck', '德澳老兵'], 'native'),
        ('meta2-seed2', 2, ['--left-deck', '苏英爆破', '--right-deck', '德澳老兵'], 'native'),
    ]
    report = {'repeats': args.repeats, 'roots': {k: str(v) for k, v in roots.items()},
              'binarySha256': {k: {'NNTrain.dll': sha(p), 'KLink.Bot.dll': sha(p.with_name('KLink.Bot.dll'))}
                               for k, p in binaries.items()},
              'scriptSha256': sha(Path(__file__)), 'samples': [], 'summary': [],
              'scope': 'Fresh-process full paired-v6 producer including admission, gzip serialization and verification; no timer instrumentation. Simulator consistency, not KARDS rule certification.'}
    expected = {}
    expected_inputs = None
    for repeat in range(args.repeats):
        for name, seed, fixture, policy in fixtures:
            order = ('before', 'after') if repeat % 2 == 0 else ('after', 'before')
            for mode in order:
                path = out/f'{name}-{repeat}-{mode}.jsonl.gz'
                command = ['dotnet', str(binaries[mode]), 'dump-clone-actions', '--games', '1',
                           '--seed', str(seed), '--choose-one-policy', policy, *fixture, '--out', str(path)]
                start = time.perf_counter()
                with path.with_suffix('.log').open('wb') as log:
                    result = subprocess.run(command, cwd=roots[mode], stdout=log, stderr=subprocess.STDOUT)
                elapsed = time.perf_counter()-start
                require(result.returncode == 0, f'Producer failed: {path}')
                manifest = json.loads(Path(str(path)+'.quality.json').read_text(encoding='utf-8-sig'))
                require(manifest['acceptedGames'] == 1 and manifest['rejectedGames'] == 0 and manifest['rows'] > 0,
                        f'Unexpected admission: {path}')
                require(manifest['header']['version'] == 6, 'Expected v6')
                inputs = {k: v for k, v in manifest['header']['inputsSha256'].items() if not k.endswith('.dll')}
                if expected_inputs is None:
                    expected_inputs = inputs
                    report['inputsSha256'] = inputs
                require(inputs == expected_inputs, f'Input data differs: {path}')
                for game in manifest['games']:
                    evidence = game['cloneEvidence']
                    require(evidence['cloneFingerprint'] == evidence['referenceFingerprint'], f'Paired mismatch: {path}')
                with gzip.open(path, 'rb') as data:
                    data.readline()  # Header includes intentionally different producer DLL hashes.
                    rows_hash = hashlib.sha256(data.read()).hexdigest()
                signature = (manifest['games'], manifest['rows'], rows_hash)
                if name not in expected:
                    expected[name] = signature
                require(signature == expected[name], f'Before/after or repeat behavior differs: {path}')
                report['samples'].append({'fixture': name, 'repeat': repeat, 'mode': mode,
                    'elapsedSeconds': elapsed, 'rows': manifest['rows'], 'rowBytesSha256': rows_hash,
                    'games': manifest['games'], 'command': command, 'dataSha256': manifest['dataSha256']})
                (out/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
                print(f'{name} repeat={repeat} {mode}: {elapsed:.3f}s, {manifest["rows"]} rows, exact match', flush=True)
    for name, *_ in fixtures:
        medians = {mode: statistics.median(s['elapsedSeconds'] for s in report['samples']
                                          if s['fixture'] == name and s['mode'] == mode) for mode in roots}
        report['summary'].append({'fixture': name, 'medianSeconds': medians,
                                 'reductionPercent': 100*(1-medians['after']/medians['before'])})
    report['complete'] = True
    (out/'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(report['summary'], ensure_ascii=False, indent=2), flush=True)


if __name__ == '__main__':
    main()
