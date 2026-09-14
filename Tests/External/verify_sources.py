"""Verify the retained upstream files against the recorded source manifest."""
from pathlib import Path
import hashlib
import json

root = Path(__file__).resolve().parent
sources = json.loads((root / 'sources.json').read_text())
for source in sources:
    actual = hashlib.sha256((root / source['file']).read_bytes()).hexdigest()
    if actual != source['sha256']:
        raise SystemExit(f"Source hash mismatch: {source['file']}")
print(f'Verified {len(sources)} pinned upstream files')
