"""Rebuild the selected straight-line BBC programs from the pinned jsbeeb source.

Only the instructions used by these cases are accepted. R% is $0100, as in
jsbeeb. No timing or expected-result values are obtained from our emulator.
"""
from pathlib import Path
import re
import json

root = Path(__file__).resolve().parent
source = (root / 'jsbeeb/via.js').read_text()
names = [f'VIA.AC{i}' for i in range(1, 8)] + ['VIA.I1', 'VIA.PB2', 'VIA.T12']
implied = {'SEI': 0x78, 'CLI': 0x58, 'RTS': 0x60, 'NOP': 0xEA}
immediate = {'LDA': 0xA9, 'LDX': 0xA2, 'LDY': 0xA0}
absolute = {'LDA': 0xAD, 'LDX': 0xAE, 'LDY': 0xAC, 'STA': 0x8D, 'STX': 0x8E, 'STY': 0x8C}

def number(value):
    if value.startswith('R%'):
        return 0x100 + (int(value[3:]) if value.startswith('R%+') else 0)
    return int(value[1:], 16) if value.startswith('&') else int(value)

cases = []
for name in names:
    start = source.index(f'it("{name} -')
    end = source.index('expectArray(testMachine, [', start)
    text = source[start:end]
    assembly = re.search(r'\n\[\n(.*?)\n\]', text, re.S).group(1)
    code = []
    for instruction in re.split(r'[:\n]', assembly):
        instruction = instruction.strip()
        if not instruction or instruction.startswith('OPT '):
            continue
        parts = instruction.split(maxsplit=1)
        op = parts[0]
        if len(parts) == 1:
            code.append(implied[op])
        elif parts[1].startswith('#'):
            code.extend([immediate[op], number(parts[1][1:])])
        else:
            address = number(parts[1])
            code.extend([absolute[op], address & 255, address >> 8])
    assert code[-1] == 0x60
    expected = json.loads(re.match(r'expectArray\(testMachine, (\[.*?\])', source[end:]).group(1))
    assert 'REAL BBC' in text
    cases.append(dict(Name=name, SourceLine=source[:start].count('\n')+1,
                      Assembly=assembly, Code=bytes(code).hex().upper(), Expected=expected))
(root / 'jsbeeb/via-cases.json').write_text(json.dumps(cases, indent=2)+'\n')
print(f'Extracted {len(cases)} real-BBC-labelled cases')
