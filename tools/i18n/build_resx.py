"""Writes Strings.resx (or a culture's Strings.<culture>.resx) from a key -> text JSON file, sorted by key."""
import json, sys
from xml.sax.saxutils import escape

src = sys.argv[1]
out = sys.argv[2]
strings = json.load(open(src, encoding='utf-8'))

lines = ['<?xml version="1.0" encoding="utf-8"?>', '<root>']
for name, value in [('resmimetype', 'text/microsoft-resx'), ('version', '2.0'),
                    ('reader', 'System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'),
                    ('writer', 'System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089')]:
    lines += [f'  <resheader name="{name}">', f'    <value>{value}</value>', '  </resheader>']
for key in sorted(strings):
    lines += [f'  <data name="{key}" xml:space="preserve">', f'    <value>{escape(strings[key])}</value>', '  </data>']
lines.append('</root>')
open(out, 'wb').write(('\r\n'.join(lines) + '\r\n').encode('utf-8-sig'))
print(f'wrote {out}: {len(strings)} strings')
