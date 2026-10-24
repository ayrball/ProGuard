"""Applies exact C# edits from a spec and records new strings.

Spec (JSON): {"path relative to src/ProGuard.UI": [[old, new], ...], ...}
In `new`, a token «Name|English text» becomes Strings.<Key>: the existing key for that exact text, or a new key Name.
Each old string must occur exactly once. Adds `using ProGuard.UI.Resources;` to edited files that need it.
"""
import json, os, re, sys

UI = 'E:/projects/proguard-wt/i18n/src/ProGuard.UI/'
STORE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'strings.json')
strings = json.load(open(STORE, encoding='utf-8'))
by_text = {v: k for k, v in strings.items()}
TOKEN = re.compile(r'«([A-Za-z_][A-Za-z0-9_]*)\|(.*?)»', re.S)

def resolve(m):
    name, text = m.group(1), m.group(2)
    if text in by_text:
        return 'Strings.' + by_text[text]
    base, n = name, 2
    while name in strings:
        name = f'{base}{n}'; n += 1
    strings[name] = text
    by_text[text] = name
    return 'Strings.' + name

spec = json.load(open(sys.argv[1], encoding='utf-8'))
for rel, edits in spec.items():
    path = UI + rel
    raw = open(path, 'rb').read(); bom = raw[:3] == b'\xef\xbb\xbf'
    s = raw[3 if bom else 0:].decode('utf-8'); crlf = '\r\n' in s; s = s.replace('\r\n', '\n')
    for old, new in edits:
        every = old.startswith('*')
        if every: old = old[1:]
        n = s.count(old)
        if n != 1 and not (every and n >= 1):
            raise SystemExit(f'{rel}: expected {"1" if not every else "1+"} match, found {n}: {old[:90]!r}')
        s = s.replace(old, TOKEN.sub(resolve, new))
    if 'Strings.' in s and 'using ProGuard.UI.Resources;' not in s and 'namespace ProGuard.UI.Resources' not in s:
        usings = list(re.finditer(r'^using [^;\n]+;\n', s, re.M))
        at = usings[-1].end() if usings else 0
        s = s[:at] + 'using ProGuard.UI.Resources;\n' + s[at:]
    if crlf: s = s.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
    print(f'{len(edits):4}  {rel}')

json.dump(strings, open(STORE, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(f'{len(strings)} strings')
