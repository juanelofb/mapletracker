"""Build a small, attributed offline name/spawn catalog from OSMS's public v1 API."""
import hashlib
import json
from pathlib import Path
import urllib.request

BASE = 'https://osmsdataexplorer.com/data/api/v1/'
ROOT = Path(__file__).resolve().parents[1]

def fetch(name):
    request = urllib.request.Request(BASE + name, headers={'User-Agent': 'MapleTracker/1.1 (offline name catalog)'})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read()

def main():
    meta = json.loads(fetch('meta.json'))
    if meta.get('schema_version') != 1:
        raise ValueError('Unsupported OSMS schema; existing catalog was left unchanged.')
    sources = {}
    for name in ['items.json', 'maps.json', 'monsters.json']:
        raw = fetch(name)
        info = meta['files'][name]
        if len(raw) != info['bytes'] or hashlib.sha256(raw).hexdigest() != info['sha256']:
            raise ValueError('Catalog changed during download: ' + name)
        sources[name] = json.loads(raw)
        if not isinstance(sources[name], list) or len(sources[name]) != info['records']:
            raise ValueError('Unexpected records: ' + name)
    license_text = fetch('LICENSE').decode('utf-8')
    result = dict(schemaVersion=1, source='https://osmsdataexplorer.com',
        attribution='Data from OSMS Data Explorer (@ohmi), CC BY 4.0. Game content © Nexon. Unofficial.',
        license='https://creativecommons.org/licenses/by/4.0/',
        changes='Reduced to item/map/monster names, categories and unique map monster IDs. No drop tables supplied.',
        dataUpdatedAt=meta['data_updated_at'], clientBuild=meta['client_build'],
        sourceHashes={name: meta['files'][name]['sha256'] for name in sources},
        items=[dict(id=i['id'], name=i['name'], category=i['category']) for i in sources['items.json']],
        monsters=[dict(id=m['id'], name=m['name']) for m in sources['monsters.json']],
        maps=[dict(id=m['id'], name=m['name'], streetName=m.get('street_name', ''),
                   mobIds=sorted({p['mob_id'] for p in m.get('mobs', [])})) for m in sources['maps.json']])
    output = ROOT / 'data/world-catalog.json'
    temporary = output.with_suffix('.json.tmp')
    temporary.write_text(json.dumps(result, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    temporary.replace(output)
    (ROOT / 'data/LICENSE-OSMS.txt').write_text(license_text, encoding='utf-8')
    print(f"Saved {len(result['maps'])} maps, {len(result['monsters'])} monsters and {len(result['items'])} items to {output}")

if __name__ == '__main__':
    main()
