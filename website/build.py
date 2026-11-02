"""Build a bilingual, dependency-free static site. Preview is the safe default."""
from __future__ import annotations
import argparse
import html
import json
from pathlib import Path
import re
import shutil
import urllib.request
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parent
REPO = ROOT.parent
OUTPUT = REPO / 'artifacts/website/dist'
ASSETS = ROOT / 'assets'
RELEASE_REPOSITORY = 'showmethecold/mani'
RELEASE_TAG = 'beta-0.1'
NATIVE_RELEASE_TAG = 'native-sup-20261101-r2'
DOWNLOAD_NAME = 'Mani-Beta-0.1-Windows-x64-Setup.exe'
MSIX_NAME = 'Mani.Beta.Test_0.5.6.0_x64.msix'
SETUP_INFO = 'setup-info.json'
REQUIRED_BETA_FILES = [DOWNLOAD_NAME, SETUP_INFO, MSIX_NAME, 'Mani.Beta.Test.cer',
    'Mani-Setup-source.zip', 'setup-source-info.json', 'setup-source-files.json', 'SETUP-SHA256SUMS.txt',
    'Mani-Beta-0.1-Windows-x64.zip']
REQUIRED_NATIVE_FILES = ['native-engine-source.zip', 'native-dependency-sources.zip',
    'mingw-w64-angleproject-2.1.r25748.890b5d8f-6.src.tar.zst', 'native-source-delivery.json', 'SOURCE-SHA256SUMS.txt',
    'mani-sup-msys2-ucrt64-20261101-r2.zip']
PATHS = {
 'windows': '<path d="M3 4.5 10.5 3v8H3Zm10.5-2L21 1v10h-7.5ZM3 13h7.5v8L3 19.5Zm10.5 0H21v10l-7.5-1.5Z"/>',
 'arrowdown': '<path d="M12 3v13m-5-5 5 5 5-5M4 19v2h16v-2"/>',
 'play': '<path d="m9 6 10 6-10 6Z"/><circle cx="12" cy="12" r="10"/>',
 'globe': '<circle cx="12" cy="12" r="9"/><ellipse cx="12" cy="12" rx="4" ry="9"/><path d="M3 12h18"/>',
 'folder': '<path d="M3 7V5h6l2 3h10v12H3Zm0 5h18"/>',
 'list': '<path d="M8 5h12M8 12h12M8 19h8M3 5h.1M3 12h.1M3 19h.1"/>',
 'captions': '<rect x="2" y="4" width="20" height="16" rx="4"/><path d="M10 9a3 3 0 1 0 0 6m8-6a3 3 0 1 0 0 6"/>',
 'sliders': '<path d="M5 3v5m0 5v8m7-18v10m0 5v3m7-18v3m0 5v10M2 8h6m1 5h6m1-7h6"/>',
 'window': '<rect x="2" y="4" width="20" height="16" rx="3"/><path d="M2 8h20"/><rect x="13" y="12" width="9" height="8" rx="2"/>',
 'expand': '<path d="M9 3H3v6m12-6h6v6M3 15v6h6m12-6v6h-6"/>'
}

def icon(name):
    return '<svg viewBox="0 0 24 24" aria-hidden="true">' + PATHS[name] + '</svg>'

def escaped(value):
    # Only editorial line breaks are allowed in translated strings.
    return html.escape(str(value), quote=True).replace('&lt;br&gt;', '<br>')

def public_download_urls():
    base = f'https://github.com/{RELEASE_REPOSITORY}/releases/download/'
    return [base + RELEASE_TAG + '/' + name for name in REQUIRED_BETA_FILES] + [
        base + NATIVE_RELEASE_TAG + '/' + name for name in REQUIRED_NATIVE_FILES]

def setup_info(public):
    if public:
        url = f'https://github.com/{RELEASE_REPOSITORY}/releases/download/{RELEASE_TAG}/{SETUP_INFO}'
        with urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent':'Mani-Website-Release-Check'}), timeout=45) as response:
            info = json.load(response)
    else:
        info = json.loads((REPO / 'artifacts/website/release' / SETUP_INFO).read_text(encoding='utf-8-sig'))
    pinned = json.loads((REPO / 'config/beta-0.1.json').read_text(encoding='utf-8'))
    if info.get('setupFile') != DOWNLOAD_NAME or info.get('packageVersion') != pinned['packageVersion']:
        raise ValueError('Unexpected setup identity.')
    if info.get('packageSha256') != pinned['files'][0]['sha256'] or info.get('certificateSha256') != pinned['files'][1]['sha256']:
        raise ValueError('The setup must contain the pinned Beta MSIX and certificate.')
    if not re.fullmatch('[0-9a-f]{64}', info.get('setupSha256', '')):
        raise ValueError('Missing setup checksum.')
    if public and info.get('signatureStatus') not in {'Valid', 'NotTrusted', 'UnknownError'}:
        raise ValueError('An unsigned test setup must not be published.')
    return info

def validate_public_release():
    for url in public_download_urls():
        request = urllib.request.Request(url, method='HEAD', headers={'User-Agent': 'Mani-Website-Release-Check'})
        with urllib.request.urlopen(request, timeout=45) as response:
            if response.status != 200:
                raise RuntimeError('Public release file unavailable: ' + url.rsplit('/', 1)[-1])

def build(public=False, base_url=''):
    if base_url and (urlparse(base_url).scheme != 'https' or not urlparse(base_url).netloc or urlparse(base_url).query):
        raise ValueError('The published base URL must be an absolute HTTPS URL without a query.')
    if public and not base_url:
        raise ValueError('Public builds require --base-url.')
    release_page = f'https://github.com/{RELEASE_REPOSITORY}/releases/tag/{RELEASE_TAG}'
    native_page = f'https://github.com/{RELEASE_REPOSITORY}/releases/tag/{NATIVE_RELEASE_TAG}'
    release_base = f'https://github.com/{RELEASE_REPOSITORY}/releases/download/{RELEASE_TAG}'
    # Never deploy a download button that points at missing/private installer or source assets.
    if public:
        validate_public_release()
    installer = setup_info(public)
    content = json.loads((ROOT / 'content.json').read_text(encoding='utf-8'))
    template = (ROOT / 'index.template.html').read_text(encoding='utf-8')
    OUTPUT.mkdir(parents=True, exist_ok=True)
    (OUTPUT / 'assets').mkdir(exist_ok=True)
    for path in ASSETS.iterdir():
        if path.suffix in {'.css', '.js', '.png', '.jpg'} and path.is_file():
            shutil.copyfile(path, OUTPUT / 'assets' / path.name)
    for lang, words in content.items():
        prefix = './' if lang == 'en' else '../'
        data = {key: escaped(value) for key, value in words.items() if isinstance(value, str)}
        data.update(root=prefix, languageKey=lang, mode='public' if public else 'preview',
                    englishUrl='./' if lang == 'en' else '../', chineseUrl='./zh/' if lang == 'en' else './',
                    englishCurrent='aria-current="page"' if lang == 'en' else '',
                    chineseCurrent='aria-current="page"' if lang == 'zh' else '',
                    previewHidden='hidden' if public else '',
                    downloadUrl=escaped(release_base + '/' + DOWNLOAD_NAME if public else prefix + 'downloads/' + DOWNLOAD_NAME),
                    downloadSha256=installer['setupSha256'])
        for name in ('Windows', 'ArrowDown', 'Play', 'Globe'):
            data['icon' + name] = icon(name.lower())
        data['canonical'] = ''
        if base_url:
            url = base_url.rstrip('/') + '/'
            data['canonical'] = f'<link rel="canonical" href="{escaped(url + ("zh/" if lang == "zh" else ""))}"><link rel="alternate" hreflang="en" href="{escaped(url)}"><link rel="alternate" hreflang="zh-CN" href="{escaped(url + "zh/")}"><link rel="alternate" hreflang="x-default" href="{escaped(url)}"><meta property="og:image" content="{escaped(url + "assets/playback-" + lang + ".jpg")}">'
        data['releaseAnchor'] = f'<a href="{release_page}">{data["releaseLink"]} ↗</a>' if public else f'<button class="text-button" data-dialog="credits">{data["releaseLink"]} ↗</button>'
        data['msixAnchor'] = f'<a href="{release_base}/{MSIX_NAME}">{data["manualMsix"]} ↗</a>' if public else ''
        data['sourceAnchor'] = (f'<a href="{release_page}">{data["source"]} ↗</a>'
                                f'<a href="{native_page}">{data["nativeSource"]} ↗</a>') if public else ''
        tabs, panels = [], []
        for number, key in enumerate(('playback', 'subtitles', 'audio'), 1):
            image = key + '-' + lang + '.jpg'
            if not (ASSETS / image).is_file():
                raise FileNotFoundError('A real app screenshot is required: ' + image)
            selected = number == 1
            tabs.append(f'<button id="tab-{key}" role="tab" aria-selected="{str(selected).lower()}" aria-controls="panel-{key}" tabindex="{0 if selected else -1}">{data[key + "Tab"]}</button>')
            alt = data['heroAlt' if key == 'playback' else key + 'Alt']
            panels.append(f'<div class="showcase-panel" role="tabpanel" tabindex="0" id="panel-{key}" aria-labelledby="tab-{key}" {"" if selected else "hidden"}><div class="panel-copy"><span class="panel-number">0{number}</span><h3>{data[key + "Title"]}</h3><p>{data[key + "Description"]}</p></div><button class="shot-button" aria-label="{data["viewImage"]}: {data[key + "Tab"]}"><img src="{prefix}assets/{image}" width="2768" height="1634" alt="{alt}" loading="lazy"><span class="zoom-icon">{icon("expand")}</span></button></div>')
        data['tabs'], data['panels'] = ''.join(tabs), ''.join(panels)
        data['featureCards'] = ''.join(f'<article class="feature"><span class="feature-icon">{icon(item["icon"])}</span><h3>{escaped(item["title"])}</h3><p>{escaped(item["body"])}</p></article>' for item in words['features'])
        data['installSteps'] = ''.join(f'<li><h3>{escaped(item["title"])}</h3><p>{escaped(item["body"])}</p></li>' for item in words['installSteps'])
        rendered = re.sub(r'\{\{(\w+)\}\}', lambda m: data[m[1]], template)
        if lang == 'en':
            rendered = rendered.replace('<link rel="stylesheet"', '<script>try{if(localStorage.getItem("mani-site-language")==="zh")location.replace(new URL("./zh/"+location.hash,location.href))}catch{}</script>\n  <link rel="stylesheet"', 1)
        destination = OUTPUT / ('zh/index.html' if lang == 'zh' else 'index.html')
        destination.parent.mkdir(exist_ok=True)
        destination.write_text(rendered, encoding='utf-8', newline='\n')
    shutil.copyfile(REPO / 'LICENSE', OUTPUT / 'LICENSE.txt')
    (OUTPUT / '.nojekyll').touch()
    (OUTPUT / 'robots.txt').write_text('User-agent: *\n' + ('Allow: /\n' if public else 'Disallow: /\n'), encoding='utf-8')
    (OUTPUT / '404.html').write_text('<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Mani · Page not found</title><body style="font:18px system-ui;padding:10vw;background:#f4f5fb;color:#202943"><h1>Nothing playing here.</h1><p>这个页面不存在。</p><a href="' + escaped(base_url.rstrip('/') + '/' if base_url else './') + '">Return to Mani · 返回首页</a></body></html>', encoding='utf-8')
    print(json.dumps({'directory': str(OUTPUT), 'mode': 'public' if public else 'preview', 'languages': list(content), 'files': len(list(OUTPUT.rglob('*')))}, indent=2))

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--public', action='store_true')
    parser.add_argument('--base-url', default='')
    args = parser.parse_args()
    build(args.public, args.base_url)
