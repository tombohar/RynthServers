#!/usr/bin/env python3
"""Writes RynthDreamWeave's DreamWeave/RetailData.g.cs from a retail client_portal.dat.

The DreamWeave Skills panel draws the retail Character panel's Skills page 1:1, so it carries the
numbers and glyph tables the retail client reads from portal.dat:

  0x0E000018 XpTable      the level chart (total XP for levels 0..275) and the skill-credit levels
  0x0E000004 SkillTable   each skill's name, icon, whether it is usable untrained (MinLevel 1), its
                          formula and description (the hover tooltip)
  0x2300000E (local dat)  the character titles, keyed by AC's string hash of "ID_CharacterTitle_<name>"
  0x400000xx Font         the glyph tables of the five faces the page uses (Palatino_Linotype_14/16/18,
                          Chat_PalatinoLinotype_Large = 20, Stonehenge_36), ASCII and Latin-1 only;
                          the engine draws their glyph sheets (GetIconTexture kind FontSheet)

Run it again only if the dat changes (it hasn't since end of retail):

  python gen_retail_data.py [client_portal.dat] [client_local_English.dat] [ACE CharacterTitle.cs] >..\\..\\Plugins\\RynthCore.Plugin.RynthDreamWeave\\DreamWeave\\RetailData.g.cs

No dependencies; the dat reader is the minimal B-tree walk the AC dat format needs.
"""
import struct
import sys

PORTAL = sys.argv[1] if len(sys.argv) > 1 else r"C:\Games\ACE\Dats\client_portal.dat"
LOCAL = sys.argv[2] if len(sys.argv) > 2 else r"C:\Games\ACE\Dats\client_local_English.dat"
# The CharacterTitle names (ACE.Entity/Enum/CharacterTitle.cs of any ACE checkout): only the names are read.
ENUM = sys.argv[3] if len(sys.argv) > 3 else r"C:\Projects\_dwsrc\ACE-DreamWeave\Source\ACE.Entity\Enum\CharacterTitle.cs"


def cs(s):
    """A C# string literal."""
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\r', '') + '"'

HDR = 0x140
BRANCHES = 0x3E
MAX_ENTRIES = 0x3D
ENTRY_SIZE = 24


class Dat:
    def __init__(self, path):
        self.f = open(path, 'rb')
        self.f.seek(HDR)
        (_ft, self.block_size, _fs, _ds, _dss, _fh, _ftl, _fc, self.root) = struct.unpack('<9I', self.f.read(36))
        self.entries = {}
        self._walk(self.root)

    def _chain(self, offset, size):
        out = bytearray()
        nxt = offset
        while len(out) < size and nxt:
            self.f.seek(nxt)
            n = struct.unpack('<I', self.f.read(4))[0] & 0x7FFFFFFF
            out += self.f.read(min(self.block_size - 4, size - len(out)))
            nxt = n
        return bytes(out[:size])

    def _walk(self, offset):
        node = self._chain(offset, 4 * BRANCHES + 4 + ENTRY_SIZE * MAX_ENTRIES)
        branches = struct.unpack_from('<%dI' % BRANCHES, node, 0)
        count = struct.unpack_from('<I', node, 4 * BRANCHES)[0]
        for i in range(count):
            _flags, did, off, size, _date, _it = struct.unpack_from('<6I', node, 4 * BRANCHES + 4 + i * ENTRY_SIZE)
            self.entries[did] = (off, size)
        if branches[0] != 0:
            for i in range(count + 1):
                self._walk(branches[i])

    def read(self, did):
        off, size = self.entries[did]
        return self._chain(off, size)


def xp_table(dat):
    b = dat.read(0x0E000018)
    o = 4
    a, v, t, s, levels = struct.unpack_from('<iiiiI', b, o)
    o += 20 + 4 * (a + 1 + v + 1 + t + 1 + s + 1)
    attrs = list(struct.unpack_from('<%dI' % (a + 1), b, 4 + 20))
    vitals = list(struct.unpack_from('<%dI' % (v + 1), b, 4 + 20 + 4 * (a + 1)))
    o0 = 4 + 20 + 4 * (a + 1 + v + 1)
    trained = list(struct.unpack_from('<%dI' % (t + 1), b, o0))
    special = list(struct.unpack_from('<%dI' % (s + 1), b, o0 + 4 * (t + 1)))
    totals = list(struct.unpack_from('<%dQ' % (levels + 1), b, o))
    o += 8 * (levels + 1)
    credits = list(struct.unpack_from('<%dI' % (levels + 1), b, o))
    return totals, credits, trained, special, attrs, vitals


def skill_table(dat):
    b = dat.read(0x0E000004)
    o = 4
    count = struct.unpack_from('<H', b, o)[0]
    o += 4

    def pstring():
        nonlocal o
        n = struct.unpack_from('<H', b, o)[0]
        o += 2
        text = b[o:o + n].decode('latin-1')
        o += n
        o = (o + 3) & ~3
        return text

    rows = []
    for _ in range(count):
        sid = struct.unpack_from('<I', b, o)[0]
        o += 4
        desc = pstring()
        name = pstring()
        icon, _tc, _sc, _cat, _cg, min_level = struct.unpack_from('<IiiIII', b, o)
        o += 24
        # SkillFormula W X Y Z Attr1 Attr2: X = 0 means no attribute part; Z divides.
        _w, fx, _y, fz, a1, a2 = struct.unpack_from('<6I', b, o)
        o += 24 + 24                  # the formula, three doubles
        rows.append((sid, name, icon, min_level == 1, desc, fx, fz, a1, a2))
    return sorted(rows)


ATTR = {1: 'Strength', 2: 'Endurance', 3: 'Quickness', 4: 'Coordination', 5: 'Focus', 6: 'Self'}


def formula(fx, fz, a1, a2):
    """The tooltip's first line as retail writes it: "( Focus + Self ) / 4"."""
    if fx == 0 or a1 not in ATTR:
        return ''
    text = '( ' + ATTR[a1] + (' + ' + ATTR[a2] if a2 in ATTR else '') + ' )'
    return text + (' / %d' % fz if fz > 1 else '')


def ac_hash(s):
    """AC's string-id hash (PStringBase): what string table entries are keyed by."""
    h = 0
    for ch in s:
        h = ((h << 4) + ord(ch)) & 0xFFFFFFFF
        hi = h & 0xF0000000
        if hi:
            h ^= hi >> 24
        h &= 0x0FFFFFFF
    return h


def string_table(dat, fid):
    """A type-0x23 string table: id -> first string (AcClientReborn Dat/AcStringTable.cs has the format)."""
    b = dat.read(fid)
    o = [9]

    def cu32():
        b0 = b[o[0]]
        if not b0 & 0x80:
            o[0] += 1
            return b0
        if not b0 & 0x40:
            v = struct.unpack_from('>H', b, o[0])[0] & 0x3FFF
            o[0] += 2
            return v
        v = struct.unpack_from('>I', b, o[0])[0] & 0x3FFFFFFF
        o[0] += 4
        return v

    def ustr():
        n = cu32()
        s = b[o[0]:o[0] + 2 * n].decode('utf-16-le', 'replace')
        o[0] += 2 * n
        return s

    def u16():
        v = struct.unpack_from('<H', b, o[0])[0]
        o[0] += 2
        return v

    def u32():
        v = struct.unpack_from('<I', b, o[0])[0]
        o[0] += 4
        return v

    out = {}
    for _ in range(cu32()):
        sid = u32()
        for _ in range(u16()):
            ustr()
        for _ in range(u16()):
            ustr()
        strings = [ustr() for _ in range(u32())]
        for _ in range(u32()):
            u32()
        o[0] += 1
        out[sid] = strings[0] if strings else ''
    return out


def enum_names(path):
    """CharacterTitle's members in order (a C# enum: "Name," or "Name = n,"), id -> name."""
    import re
    names, n, inside = {}, 0, False
    for line in open(path, encoding='utf-8'):
        line = line.split('//')[0].strip()
        if line.startswith('public enum'):
            inside = True
            continue
        if not inside or not line or line in ('{',):
            continue
        if line.startswith('}'):
            break
        m = re.match(r'^(\w+)\s*(?:=\s*(\w+))?\s*,?$', line)
        if not m:
            continue
        if m.group(2):
            n = int(m.group(2), 0)
        names[n] = m.group(1)
        n += 1
    return names


def titles(local, enum_path):
    """CharacterTitleId -> the title's text, from string table 0x2300000E keyed by
    hash("ID_CharacterTitle_" + its original name). The names come from ACE's CharacterTitle enum
    (names only, no code), whose spellings drop the original's underscores: each is tried as written,
    then with underscores between its words, then matched by its letters alone."""
    import re
    table = string_table(local, 0x2300000E)
    by_letters = {}
    for text in table.values():
        by_letters.setdefault(re.sub(r'[^a-z0-9]', '', text.lower()), []).append(text)
    out, missing = {}, []
    for tid, name in sorted(enum_names(enum_path).items()):
        if tid == 0 or name.upper().startswith('UNKNOWN'):
            continue
        words = re.sub(r'(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])', '_', name)
        text = None
        for cand in (name, words):
            k = ac_hash('ID_CharacterTitle_' + cand)
            if k in table:
                text = table[k]
                break
        if text is None:
            hits = by_letters.get(re.sub(r'[^a-z0-9]', '', name.lower()), [])
            if len(hits) == 1:
                text = hits[0]
        if text is None:
            missing.append(name)
        else:
            out[tid] = text
    sys.stderr.write('titles: %d named, %d not found (%s)\n' % (len(out), len(missing), ', '.join(missing[:12])))
    return out


FONTS = [
    ('Pal14', 0x40000002, False),
    ('Pal16', 0x40000000, False),
    ('Pal18', 0x40000001, False),
    ('Pal20', 0x40000004, False),
    ('Stone36', 0x40000010, True),
]


def font(dat, did):
    b = dat.read(did)
    o = 4
    max_h, _max_w, n = struct.unpack_from('<III', b, o)
    o += 12
    glyphs = {}
    for _ in range(n):
        u, ox, oy, w, h, hb, ha, vb = struct.unpack_from('<HHHBBbbb', b, o)
        o += 11
        glyphs[u] = (ox, oy, w, h, hb, ha, vb)
    bx, by, base, fg, bg = struct.unpack_from('<IIIII', b, o)
    sheets = []
    for sid in (fg, bg):
        if sid:
            t = dat.read(sid)
            sheets.append(struct.unpack_from('<ii', t, 8))
        else:
            sheets.append((0, 0))
    return max_h, bx, by, base, fg, bg, sheets, glyphs


def main():
    dat = Dat(PORTAL)
    totals, credits, trained, special, attrs, vitals = xp_table(dat)
    skills = skill_table(dat)
    w = sys.stdout.write
    w('// <auto-generated> by Tools/RetailDataGen/gen_retail_data.py from a retail client_portal.dat.\n')
    w('// The numbers the retail client reads to draw its Character panel: do not edit by hand.\n')
    w('namespace RynthCore.Plugin.RynthDreamWeave;\n\n')
    w('internal static partial class RetailData\n{\n')
    w('    /// <summary>XpTable 0x0E000018: total experience to reach each level, 0..%d.</summary>\n' % (len(totals) - 1))
    w('    public static readonly ulong[] LevelTotals =\n    {\n')
    for i in range(0, len(totals), 6):
        w('        ' + ', '.join('%dUL' % v for v in totals[i:i + 6]) + ',\n')
    w('    };\n\n')
    w('    /// <summary>XpTable 0x0E000018: skill credits granted on reaching each level, 0..%d.</summary>\n' % (len(credits) - 1))
    w('    public static readonly byte[] LevelCredits =\n    {\n')
    for i in range(0, len(credits), 30):
        w('        ' + ', '.join(str(v) for v in credits[i:i + 30]) + ',\n')
    w('    };\n\n')
    for field, chart, what in (('TrainedSkillXp', trained, 'a trained skill'), ('SpecializedSkillXp', special, 'a specialized skill'),
                               ('AttributeXp', attrs, 'an attribute'), ('VitalXp', vitals, 'a vital\'s maximum')):
        w('    /// <summary>XpTable 0x0E000018: total XP to reach each rank of %s, 0..%d.</summary>\n'
          % (what, len(chart) - 1))
        w('    public static readonly uint[] %s =\n    {\n' % field)
        for i in range(0, len(chart), 8):
            w('        ' + ', '.join('%dU' % v for v in chart[i:i + 8]) + ',\n')
        w('    };\n\n')
    w('    /// <summary>SkillTable 0x0E000004: id, name, icon, usable untrained (MinLevel 1), and the hover\n')
    w('    /// tooltip\'s formula line and description.</summary>\n')
    w('    public static readonly RetailSkill[] Skills =\n    {\n')
    for sid, name, icon, usable, desc, fx, fz, a1, a2 in skills:
        w('        new(%d, %s, 0x%08Xu, %s, %s, %s),\n' % (sid, cs(name), icon, 'true' if usable else 'false',
                                                     cs(formula(fx, fz, a1, a2)), cs(desc)))
    w('    };\n\n')
    names = titles(Dat(LOCAL), ENUM)
    w('    /// <summary>String table 0x2300000E: CharacterTitleId -> the title (the header\'s second line).</summary>\n')
    w('    public static readonly System.Collections.Generic.Dictionary<int, string> Titles = new()\n    {\n')
    for tid, text in sorted(names.items()):
        w('        [%d] = %s,\n' % (tid, cs(text)))
    w('    };\n\n')
    for field, did, outline in FONTS:
        max_h, bx, by, base, fg, bg, sheets, glyphs = font(dat, did)
        chars = [c for c in sorted(glyphs) if 0x20 <= c <= 0x7E or 0xA0 <= c <= 0xFF]
        w('    /// <summary>Font 0x%08X: glyphs as char, sheet x, y, w, h, before, after, top.</summary>\n' % did)
        w('    public static readonly RetailFont %s = new(0x%08Xu, %d, %d, %d, %d, 0x%08Xu, %d, %d, 0x%08Xu, %d, %d, new short[]\n    {\n'
          % (field, did, max_h, base, bx, by, fg, sheets[0][0], sheets[0][1], bg if outline else 0,
             sheets[1][0] if outline else 0, sheets[1][1] if outline else 0))
        for i in range(0, len(chars), 4):
            parts = []
            for c in chars[i:i + 4]:
                ox, oy, gw, gh, hb, ha, vb = glyphs[c]
                parts.append('%d,%d,%d,%d,%d,%d,%d,%d' % (c, ox, oy, gw, gh, hb, ha, vb))
            w('        ' + ', '.join(parts) + ',\n')
        w('    });\n\n')
    w('}\n')


if __name__ == '__main__':
    main()
