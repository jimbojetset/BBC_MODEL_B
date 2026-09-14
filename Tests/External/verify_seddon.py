"""Check each C# adaptation against the pinned tokenised BBC BASIC program."""
from pathlib import Path
import re

root = Path(__file__).resolve().parent
raw = (root / 'seddon/TIMINGS').read_bytes()
lines = {}
offset = 0
while offset + 4 < len(raw) and raw[offset] == 13 and raw[offset + 1] != 255:
    n = raw[offset+1] * 256 + raw[offset+2]
    size = raw[offset+3]
    lines[n] = raw[offset+4:offset+size].decode('latin1').replace('\xf2', 'PROC')
    offset += size

opcodes = {('LDA', ''): 0xAD, ('LDA', 'X'): 0xBD, ('LDA', 'Y'): 0xB9,
           ('STA', 'X'): 0x9D, ('STA', 'Y'): 0x99}
cs = (root.parent / 'BBC_TIMING_TESTS/SeddonTimingTests.cs').read_text()
count = 0
for id, line, hexcode, x, y, cycles in re.findall(r'Add\("([0-9A-F]+)", (\d+), "([0-9A-F]+)", (\d+), (\d+), (\d+)\)', cs):
    source = lines[int(line)]
    case = re.search(r'PROCT2?\(&' + id + r',([\d+]+)[,)]', source)
    assert case, (id, line, source)
    assert sum(map(int, case[1].split('+'))) == int(cycles), (id, 'cycle expectation')
    assembly = re.search(r'\[(.*?):\]', source)[1]
    code = []
    for instruction in assembly.split(':'):
        if instruction == 'BIT0':
            code.extend([0x24, 0])
        else:
            op, addr, index = re.fullmatch(r'(LDA|STA)&([0-9A-F]+)(?:,([XY]))?', instruction).groups()
            address = int(addr,16)
            code.extend([opcodes[(op,index or '')], address & 255, address >> 8])
    assert bytes(code).hex().upper() == hexcode, (id, 'instruction bytes')
    for register, value in [('X', x), ('Y', y)]:
        if ','+register in assembly:
            settings = re.findall(register+r'%=(\d+)', source[:case.start()])
            assert settings and int(settings[-1]) == int(value), (id, register)
    count += 1
print(f'Verified {count} Seddon IDs, BASIC line numbers, instruction sequences, index values and NMOS expectations')
