"""Moves the visible English text out of ProGuard.UI's XAML into strings.json (key -> English), and points the XAML at
{x:Static r:Strings.Key}. Identical text shares a key. Run once; build_resx.py then writes Strings.resx."""
import html, json, os, re, glob

UI = 'E:/projects/proguard-wt/i18n/src/ProGuard.UI'
STORE = 'C:/Users/Tee/AppData/Local/Temp/claude/C--Users-Tee--gemini-antigravity-worktrees-intelligent-brahmagupta-better-process-lasso-gui/1b7d6924-c058-4c63-8152-a9f1c7b00130/scratchpad/strings.json'

strings = json.load(open(STORE, encoding='utf-8')) if os.path.exists(STORE) else {}
by_text = {v: k for k, v in strings.items()}

def key_for(text):
    if text in by_text:
        return by_text[text]
    words = re.findall(r"[A-Za-z0-9]+", text.replace("'", ''))
    base = ''.join(w[:1].upper() + w[1:].lower() for w in words[:6]) or 'Text'
    if base[0].isdigit():
        base = 'N' + base
    key, n = base, 2
    while key in strings:
        key = f'{base}{n}'; n += 1
    strings[key] = text
    by_text[text] = key
    return key

def translatable(text):
    return re.search(r'[A-Za-z]{2,}', text) is not None

def decode(value):
    # XAML attribute value -> the real text ({} escape removed, entities decoded).
    if value.startswith('{}'):
        value = value[2:]
    return html.unescape(value)

ATTR = re.compile(r'(\s)(Text|Content|ToolTip|Header|Tag)="([^"]*)"')
SETTER = re.compile(r'(<Setter\s+Property="(?:Text|Content|ToolTip|Header|Tag)"\s+Value=")([^"]*)(")')
FORMAT = re.compile(r"StringFormat='([^']*)'")

changed_files = []
for path in sorted(glob.glob(UI + '/**/*.xaml', recursive=True)):
    if re.search(r'[\\/](bin|obj)[\\/]', path):
        continue
    raw = open(path, 'rb').read(); bom = raw[:3] == b'\xef\xbb\xbf'
    s = raw[3 if bom else 0:].decode('utf-8'); crlf = '\r\n' in s; s = s.replace('\r\n', '\n')
    count = 0

    def attr(m):
        global count
        value = m.group(3)
        if value.startswith('{') and not value.startswith('{}'):
            return m.group(0)
        text = decode(value)
        if not translatable(text):
            return m.group(0)
        nonlocal_count[0] += 1
        return f'{m.group(1)}{m.group(2)}="{{x:Static r:Strings.{key_for(text)}}}"'

    def setter(m):
        value = m.group(2)
        if value.startswith('{') and not value.startswith('{}'):
            return m.group(0)
        text = decode(value)
        if not translatable(text):
            return m.group(0)
        nonlocal_count[0] += 1
        return f'{m.group(1)}{{x:Static r:Strings.{key_for(text)}}}{m.group(3)}'

    def fmt(m):
        text = decode(m.group(1))
        if not translatable(text):
            return m.group(0)
        nonlocal_count[0] += 1
        return f'StringFormat={{x:Static r:Strings.{key_for(text)}}}'

    nonlocal_count = [0]
    s = SETTER.sub(setter, s)
    s = ATTR.sub(attr, s)
    s = FORMAT.sub(fmt, s)
    if nonlocal_count[0]:
        if 'xmlns:r=' not in s:
            s = re.sub(r'(xmlns="http://schemas\.microsoft\.com/winfx/2006/xaml/presentation")',
                       r'\1\n             xmlns:r="clr-namespace:ProGuard.UI.Resources"', s, count=1)
            assert 'xmlns:r=' in s, path
        if crlf:
            s = s.replace('\n', '\r\n')
        open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
        changed_files.append((os.path.relpath(path, UI), nonlocal_count[0]))

json.dump(strings, open(STORE, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
for f, n in changed_files:
    print(f'{n:4}  {f}')
print(f'{len(strings)} distinct strings in {STORE}')
