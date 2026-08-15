#!/usr/bin/env python3
"""Convert `<xx>{l:Loc Key}</xx>` element-content localization markup into
attribute form, e.g. `<xx Text="{l:Loc Key}"/>` or `<xx Content="{l:Loc Key}"/>`.

Only elements whose entire content is a single `{l:Loc ...}` markup extension
(no nested elements, no surrounding literal text) are rewritten.  Mixed content
such as `<Label>{l:Loc A}</Label>` are handled; things like
`<Label>{l:Loc A}:</Label>` or `<TextBlock>{l:Loc A} (…)</TextBlock>` are left
untouched.

Usage:
    python convert_loc_attributes.py [path ...]
If no paths are given, the script scans all `*.axaml` files under the
`UndertaleModToolAvalonia` directory.
"""

import re
import sys
from pathlib import Path

# Controls that carry their text through the `Text` property.
TEXT_PROPERTY = {
    "TextBlock",
    "TextBox",
    "PasswordBox",
    "SelectableTextBlock",
    "Run",
    "Watermark",
}

# Everything else is a ContentControl-style element and uses `Content`.
DEFAULT_PROPERTY = "Content"

# Matches  <tag  attrs...>   {l:Loc Key}   </tag>
# attrs may contain braces (e.g. `Command="{Binding X}"`), quotes, spaces and
# span multiple lines; `[^<>]*?` stops at the first `>`, and a trailing `\s*`
# tolerates whitespace between the opening tag and the markup extension.
MAGIC = re.compile(
    r'<(?P<tag>[A-Za-z_][\w.]*)'
    r'(?P<attrs>[^<>]*?)\s*>'
    r'\s*\{l:Loc\s+(?P<key>[A-Za-z_][\w.]*)\}\s*'
    r'</(?P=tag)>',
    re.DOTALL,
)


def property_for(tag: str) -> str:
    return "Text" if tag in TEXT_PROPERTY else DEFAULT_PROPERTY


def rewrite(content: str) -> tuple[str, int]:
    def repl(m: re.Match) -> str:
        tag = m.group("tag")
        attrs = m.group("attrs").rstrip()
        key = m.group("key")
        prop = property_for(tag)
        if re.search(rf'\b{prop}="', attrs):
            # The element already sets the target property; keep it as-is.
            return m.group(0)
        return f"<{tag}{attrs} {prop}=\"{{l:Loc {key}}}\"/>"

    new, n = MAGIC.subn(repl, content)
    return new, n


def main() -> None:
    paths: list[Path]
    if len(sys.argv) > 1:
        paths = [Path(a) for a in sys.argv[1:]]
    else:
        paths = sorted(Path("UndertaleModToolAvalonia").rglob("*.axaml"))

    total = 0
    for path in paths:
        if not path.is_file():
            continue
        original = path.read_text(encoding="utf-8")
        rewritten, count = rewrite(original)
        if count:
            path.write_text(rewritten, encoding="utf-8", newline="")
            print(f"{path}: converted {count} element(s)")
            total += count
    print(f"Total converted: {total}")


if __name__ == "__main__":
    main()