"""Generates Helix's default keymaps for Helide to embed, one file per Helix release.

Usage: python tools/keymaps/helix_defaults.py <out dir> [--since 23.10]

Helix builds its default keymap into the program and has no way to print it, so it is
read from Helix's own source for each release tag: helix-term/src/keymap/default.rs for
the bindings and helix-term/src/commands.rs for each command's one-line description.
Each release becomes helix-<version>.json in the out dir; Helide.csproj embeds every
one, and Helide picks the one matching the Helix installed.

A release's source never changes, so a file already there is not fetched again. Only
this script goes online, at build time; Helide itself never does.
"""
import json
import os
import re
import sys
import urllib.request

RAW = 'https://raw.githubusercontent.com/helix-editor/helix/{tag}/helix-term/src/{path}'
TAGS = 'https://api.github.com/repos/helix-editor/helix/tags?per_page=100'


def fetch(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'helide-keymaps'})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read().decode('utf-8')


def version_key(tag):
    return tuple(int(part) for part in tag.split('.'))


def release_tags(since):
    tags = [tag['name'] for tag in json.loads(fetch(TAGS))]
    # Helix's own releases are dated, YY.MM[.patch]; the v0.x tags are from before.
    dated = [tag for tag in tags if re.fullmatch(r'\d{2}\.\d{2}(\.\d+)?', tag)]
    return sorted((tag for tag in dated if version_key(tag) >= version_key(since)), key=version_key)


# ---- reading Helix's source -----------------------------------------------------------

TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|(=>)|([{}|,()])|(\w+(?:=\w+)?)|(//[^\n]*)|(\s+)|(.)', re.S)


def tokens(text):
    for m in TOKEN.finditer(text):
        s, arrow, punct, word, comment, space, other = m.groups()
        if comment or space:
            continue
        if s is not None:
            yield ('str', s)
        elif arrow:
            yield ('=>', arrow)
        elif punct:
            yield (punct, punct)
        elif word:
            yield ('word', word)
        else:
            yield ('other', other)


def parse_trie(toks, i):
    """At '{': returns (title, entries, index after '}'). entries: [(keys, value)]."""
    assert toks[i][0] == '{', toks[i]
    i += 1
    title = toks[i][1]
    i += 1
    while toks[i][0] == 'word' and '=' in toks[i][1]:  # sticky=true
        i += 1
    entries = []
    while toks[i][0] != '}':
        keys = [toks[i][1]]
        i += 1
        while toks[i][0] == '|':
            keys.append(toks[i + 1][1])
            i += 2
        assert toks[i][0] == '=>', toks[i:i + 3]
        i += 1
        if toks[i][0] == '{':
            sub_title, sub, i = parse_trie(toks, i)
            value = ('menu', sub_title, sub)
        else:
            value = ('cmd', toks[i][1])
            i += 1
        entries.append((keys, value))
        if toks[i][0] == ',':
            i += 1
    return title, entries, i + 1


def trie(keymap, name):
    start = keymap.index('keymap!({ "' + name + '"') + len('keymap!(')
    return parse_trie(list(tokens(keymap[start:])), 0)[1]


def flatten(entries, prefix, menus, out):
    for keys, value in entries:
        for key in keys:
            path = prefix + [key]
            if value[0] == 'menu':
                flatten(value[2], path, menus + [value[1]], out)
            else:
                out[tuple(path)] = (value[1], menus)


def generate(tag):
    keymap = fetch(RAW.format(tag=tag, path='keymap/default.rs'))
    commands = fetch(RAW.format(tag=tag, path='commands.rs'))

    docs = {}
    block = commands[commands.index('static_commands!('):]
    block = block[:block.index(');')]
    for name, doc in re.findall(r'^\s*(\w+),\s*"((?:[^"\\]|\\.)*)",?\s*$', block, re.M):
        docs[name] = doc.replace('\\"', '"')

    normal, select_over, insert = {}, {}, {}
    flatten(trie(keymap, 'Normal mode'), [], [], normal)
    flatten(trie(keymap, 'Select mode'), [], [], select_over)
    flatten(trie(keymap, 'Insert mode'), [], [], insert)
    select = dict(normal)
    select.update(select_over)  # select mode is normal mode with its own keys laid over

    result = {'tool': 'helix', 'version': tag, 'commands': docs, 'bindings': []}
    for mode, table in (('normal', normal), ('select', select), ('insert', insert)):
        for keys, (command, menus) in table.items():
            result['bindings'].append({'mode': mode, 'keys': list(keys), 'menus': menus, 'command': command})
    return result


def main():
    args = sys.argv[1:]
    since = '23.10'
    if '--since' in args:
        at = args.index('--since')
        since = args[at + 1]
        del args[at:at + 2]
    out_dir = args[0]
    os.makedirs(out_dir, exist_ok=True)

    try:
        tags = release_tags(since)
    except Exception as error:  # offline: the files already there are what ships
        print(f'could not list Helix releases ({error}); keeping the files already generated')
        return

    for tag in tags:
        out = os.path.join(out_dir, f'helix-{tag}.json')
        if os.path.exists(out):
            continue
        try:
            result = generate(tag)
        except Exception as error:
            print(f'{tag}: skipped ({error})')
            continue
        with open(out, 'w', encoding='utf-8', newline='\n') as file:
            json.dump(result, file, separators=(',', ':'))
        print(f'{tag}: {len(result["bindings"])} bindings, {len(result["commands"])} commands')


if __name__ == '__main__':
    main()
