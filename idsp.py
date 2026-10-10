"""
Byte-exact reader/writer for DS18 (.idsp) DSP parameter files.

The .idsp format is plain XML (CRLF line endings, 2-space indent, no trailing
newline, no self-closing tags). This module round-trips a file byte-for-byte so
that we can edit values without disturbing anything the DSP software cares about.

Verified: load() -> dumps() reproduces the original file exactly (same sha1).
"""
import xml.etree.ElementTree as ET

HEADER = '<?xml version="1.0" standalone="no"?>\r\n'


# --------------------------------------------------------------------------- io
def load(path):
    """Parse an .idsp file, preserving comments."""
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    return ET.parse(path, parser=parser).getroot()


def dumps(root):
    """Serialise back to the exact on-disk byte format."""
    out = [HEADER]

    def rec(el, ind):
        pad = '  ' * ind
        if el.tag is ET.Comment:
            out.append(f'{pad}<!--{el.text}-->\r\n')
            return
        attrs = ''.join(f' {k}="{v}"' for k, v in el.attrib.items())
        kids = list(el)
        if kids:
            out.append(f'{pad}<{el.tag}{attrs}>\r\n')
            for c in kids:
                rec(c, ind + 1)
            out.append(f'{pad}</{el.tag}>\r\n')
        else:
            out.append(f'{pad}<{el.tag}{attrs}>{el.text or ""}</{el.tag}>\r\n')

    rec(root, 0)
    data = ''.join(out)
    if data.endswith('\r\n'):
        data = data[:-2]          # original file has no trailing newline
    return data.encode('utf-8')


def save(root, path):
    with open(path, 'wb') as f:
        f.write(dumps(root))


# -------------------------------------------------------------------- accessors
def memories(root):
    """All <memory> blocks, in index order."""
    return sorted(root.findall('memory'), key=lambda m: int(m.get('index', 0)))


def outputs(memory_el):
    """All <output> channels of a memory, in index order."""
    return sorted(memory_el.findall('output'),
                  key=lambda o: int(o.get('index', 0)))


def output(memory_el, index):
    for o in outputs(memory_el):
        if int(o.get('index', -1)) == index:
            return o
    raise KeyError(f'no output index {index} in memory {memory_el.get("index")}')


def read_eq(output_el):
    """Return the channel EQ as a list of (Fc, Gain, Q) floats."""
    r = []
    for e in sorted(output_el.findall('eq'), key=lambda x: int(x.get('index', 0))):
        r.append((float(e.findtext('Fc')), float(e.findtext('Gain')),
                  float(e.findtext('Q'))))
    return r


# --------------------------------------------------------------------- mutators
def fmt(v, decimals=1):
    """Format a number the way the DSP software does (trim trailing zeros)."""
    s = f'{v:.{decimals}f}'
    if '.' in s:
        s = s.rstrip('0').rstrip('.')
    return s or '0'


def set_eq(output_el, filters, gain_decimals=1, q_decimals=1, fc_decimals=0,
           zero_unused=True):
    """
    Write a list of (Fc, Gain, Q) peaking filters into a channel's <eq> bands.

    Any unused bands are zeroed out so stale cuts/boosts from a previous tune
    cannot leak into the new one.
    """
    bands = sorted(output_el.findall('eq'), key=lambda x: int(x.get('index', 0)))
    if len(filters) > len(bands):
        raise ValueError(f'{len(filters)} filters but only {len(bands)} EQ bands')

    for i, band in enumerate(bands):
        if i < len(filters):
            fc, gain, q = filters[i]
        elif zero_unused:
            fc = float(band.findtext('Fc'))
            gain = 0.0
            q = float(band.findtext('Q'))
        else:
            continue
        band.find('Fc').text = fmt(fc, fc_decimals)
        band.find('Gain').text = fmt(gain, gain_decimals)
        band.find('Q').text = fmt(q, q_decimals)


def set_delay(output_el, ms):
    output_el.find('delay').text = fmt(ms, 2)


def set_gain(output_el, db):
    output_el.find('gain').text = fmt(db, 1)
