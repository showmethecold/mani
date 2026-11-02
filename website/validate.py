"""Check the generated site's links, bilingual coverage, assets and public surface."""
from pathlib import Path
from html.parser import HTMLParser
import json
from urllib.parse import urlsplit, unquote

ROOT = Path(__file__).resolve().parent
DIST = ROOT.parent / 'artifacts/website/dist'
DOWNLOAD = ROOT.parent / 'artifacts/website/release/Mani-Beta-0.1-Windows-x64-Setup.exe'

class Page(HTMLParser):
    def __init__(self, text):
        super().__init__();self.ids=[];self.links=[];self.images=[];self.lang=None;self.feed(text)
    def handle_starttag(self, tag, attrs):
        a=dict(attrs)
        if 'id' in a:self.ids.append(a['id'])
        if tag=='html':self.lang=a.get('lang')
        if tag in ('a','link') and a.get('href'):self.links.append(a['href'])
        if tag=='script' and a.get('src'):self.links.append(a['src'])
        if tag=='img' and a.get('src'):self.images.append(a);self.links.append(a['src'])

content=json.loads((ROOT/'content.json').read_text(encoding='utf-8'))
assert content['en'].keys()==content['zh'].keys(), 'Missing translation keys'
pages={p:Page(p.read_text(encoding='utf-8')) for p in [DIST/'index.html',DIST/'zh/index.html']}
checks=[]
for path, page in pages.items():
    assert page.lang == ('zh-CN' if path.parent.name=='zh' else 'en')
    assert len(page.ids)==len(set(page.ids)), 'Duplicate HTML IDs'
    assert all('alt' in image for image in page.images), 'Image without alternative text'
    for link in page.links:
        url=urlsplit(link)
        if url.scheme:
            assert url.scheme=='https', 'Non-HTTPS external link'
            assert not url.username and not url.password, 'Credentials must never be in website URLs'
            continue
        if not url.path:
            assert not url.fragment or url.fragment in page.ids, 'Broken anchor: '+link
            continue
        target=(path.parent/unquote(url.path)).resolve()
        assert target.is_relative_to(DIST.resolve()), 'Path outside website: '+link
        if '/downloads/' in str(target).replace('\\','/'):
            assert target.name==DOWNLOAD.name and DOWNLOAD.is_file(), 'Preview download missing'
        else:
            assert (target/'index.html').is_file() if target.is_dir() else target.is_file(), 'Broken local link: '+link
    checks.append({'page':str(path.relative_to(DIST)),'language':page.lang,'links':len(page.links),'images':len(page.images),'passed':True})
expected_files={Path('index.html'),Path('zh/index.html'),Path('404.html'),Path('LICENSE.txt'),Path('robots.txt'),Path('.nojekyll')}
expected_files.update(Path('assets')/p.name for p in (ROOT/'assets').iterdir() if p.is_file())
assert {p.relative_to(DIST) for p in DIST.rglob('*') if p.is_file()}==expected_files, 'Unexpected or missing website output file'
for p in DIST.rglob('*'):
    if p.is_file():
        assert p.suffix in ('.html','.css','.js','.jpg','.png','.txt','') or p.name=='.nojekyll', 'Unexpected distribution file: '+str(p)
        assert not p.is_symlink(), 'No symbolic links in Pages payload'
for part in ('playback','subtitles','audio'):
    for language in ('en','zh'):
        assert (DIST/'assets'/f'{part}-{language}.jpg').stat().st_size>10000, 'Missing screenshot content'
print(json.dumps({'checks':checks,'translationKeys':len(content['en']),'staticBytes':sum(p.stat().st_size for p in DIST.rglob('*') if p.is_file()),'noUnexpectedPayloadFiles':True},indent=2))
