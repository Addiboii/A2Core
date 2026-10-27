"""Preserve observed placements and spawn fields from WorldProbe JSON exports."""
import argparse
import json
from pathlib import Path


def summarize(root):
    summary = json.loads((root / 'summary.json').read_text())
    result = {'packages': [], 'placements': [], 'spawns': [], 'cells': [],
              'limitations': ['Relative transforms are preserved, not composed into world transforms.',
                             'Absent properties are unknown; class defaults are not inferred.',
                             'Spawn names and fields do not establish server behavior.']}
    for package in summary['packages']:
        entries = {x['index']: x for x in package.get('exports', []) if x['status'] == 'decoded'}
        cache = {}
        def read(index):
            if index not in cache:
                path = (root / entries[index]['file']).resolve()
                if not path.is_relative_to(root.resolve()): raise ValueError('Export path escapes report directory')
                cache[index] = json.loads(path.read_text())
            return cache[index]
        expected = '/' + package['path'].replace('AION2/Content/', 'Game/', 1)
        def resolve(reference):
            path = (reference or {}).get('ObjectPath', '')
            prefix, _, suffix = path.rpartition('.')
            if prefix.lower() != expected.lower() or not suffix.isdigit() or int(suffix) not in entries:
                return None
            return read(int(suffix))
        def transform(obj):
            props = obj.get('Properties', {}) if obj else {}
            return {k: props[k] for k in ('RelativeLocation','RelativeRotation','RelativeScale3D','AttachParent') if k in props}
        result['packages'].append({'path':package['path'], 'status':package['status'], 'decoded_exports':len(entries),
                                   'failed_exports':sum(x['status']=='failed' for x in package.get('exports',[]))})
        for index, entry in entries.items():
            kind = entry.get('ExportType', '')
            if kind not in ('StaticMeshComponent','WorldPartitionRuntimeCellDataSpatialHash') and not kind.startswith('AionSpawn'):
                continue
            obj = read(index); props = obj.get('Properties', {})
            provenance = {'package':package['path'], 'export_index':index,'source_file':entry['file'],'name':obj['Name']}
            if kind == 'StaticMeshComponent' and props.get('StaticMesh'):
                result['placements'].append({**provenance,'mesh':props['StaticMesh'],'transform':transform(obj),
                    'override_materials':props.get('OverrideMaterials')})
            elif kind.startswith('AionSpawn'):
                component = resolve(props.get('RootComponent'))
                result['spawns'].append({**provenance,'class':kind,'observed_properties':props,
                    'root_component_resolved':component is not None,'transform':transform(component)})
            elif kind == 'WorldPartitionRuntimeCellDataSpatialHash':
                result['cells'].append({**provenance,'outer':obj.get('Outer'),'observed_properties':props})
    return result


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('report',type=Path);p.add_argument('--output',type=Path,required=True)
    a=p.parse_args()
    local=Path(__file__).resolve().parents[1]/'research/local'
    if not a.output.resolve().is_relative_to(local.resolve()):p.error('Output must be under research/local/')
    report=summarize(a.report)
    with a.output.open('x',encoding='utf-8') as f:json.dump(report,f,indent=2)
    print(json.dumps({k:len(report[k]) for k in ('packages','placements','spawns','cells')}))


if __name__ == '__main__':main()
