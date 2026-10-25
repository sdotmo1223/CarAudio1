import xml.etree.ElementTree as ET
def mem(f, mi):
    r = ET.parse(f).getroot()
    m = r.find("memory[@index='%d']" % mi)
    g = {}
    for out in m.findall('output'):
        g[int(out.get('index'))] = float(out.find('gain').text)
    return g
for mi in (0,1,2,3):
    try:
        a = mem('web/1-car-tune-2026-10-20.idsp', mi)
        b = mem('web/2-bor8f3-improved.idsp', mi)
    except Exception as e:
        print('mem', mi, 'skip', e); continue
    print('== memory', mi + 1)
    for i in range(8):
        print('  path %d  %7.1f %7.1f  delta %+6.1f' % (i, a[i], b[i], b[i]-a[i]))
