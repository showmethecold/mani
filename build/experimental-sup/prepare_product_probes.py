"""Reuse the existing M5 probes with explicit experimental DLL arguments.

Original probe sources/evidence and the main runtime lock remain unchanged.
Args for each generated probe: repository, new output directory, DLL, SHA256.
"""
from pathlib import Path
import hashlib
import json
import re

ROOT = Path(__file__).resolve().parents[2]
EXP = ROOT / "artifacts/m5/sup-engine-integration"
SOURCES = {
    "native": "artifacts/m5/review-fixes/native-check/Program.cs",
    "render": "artifacts/m5/review-fixes/render-check/Program.cs",
    "matching": "artifacts/m5/followup/match-check/Program.cs",
    "regression": "artifacts/m5/followup/regression-check/Program.cs",
    "audio": "artifacts/m5/review-fixes/audio-check/Program.cs",
}


def main():
    provenance = {}
    for name, relative in SOURCES.items():
        source = ROOT / relative
        code = source.read_text(encoding="utf-8-sig")
        if name == "regression":
            code, n = re.subn(r'var output=Path.Combine\(root,"artifacts/m5/followup/regression-"\+DateTime.Now.ToString\("HHmmss"\)\);',
                'var output=Path.GetFullPath(args[1]);', code)
            assert n == 1
            code = code.replace('var app=Path.Combine(root,"artifacts/m5/followup/app");',
                'var app=Path.GetDirectoryName(Path.GetFullPath(args[2]))!;')
            marker = 'var options=new PlaybackOptions('
        else:
            code, n = re.subn(r'string dll\s*=\s*Path.Combine\(root,\s*(?:locked|config).RootElement.GetProperty\("dll"\).GetString\(\)!\);',
                'string dll=Path.GetFullPath(args[2]);', code)
            assert n == 1, name
            marker = 'string dll=Path.GetFullPath(args[2]);'
        guard = '\nif (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.GetFullPath(args[2])))).Equals(args[3],StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Experimental DLL hash mismatch");\n'
        if name == "regression":
            code = code.replace(marker, guard + marker, 1)
        else:
            code = code.replace(marker, marker + guard, 1)
        destination = EXP / "probes" / name
        destination.mkdir(parents=True, exist_ok=False)
        (destination / "Program.cs").write_text(code, encoding="utf-8")
        library = (EXP / "publish-app/Mani.Playback.dll").as_posix()
        project = f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.26100.0</TargetFramework><UseWindowsForms>true</UseWindowsForms><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PlatformTarget>x64</PlatformTarget></PropertyGroup>
  <ItemGroup><Reference Include="Mani.Playback"><HintPath>{library}</HintPath></Reference></ItemGroup>
</Project>
'''
        (destination / "Probe.csproj").write_text(project, encoding="utf-8")
        provenance[name] = dict(original=relative,
            originalSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
            adaptedSha256=hashlib.sha256(code.encode()).hexdigest(),
            changes="Only DLL/output arguments and explicit hash guard; same assertions")
    (EXP / "probe-provenance.json").write_text(json.dumps(provenance, indent=2))
    print("Prepared five isolated copies of existing M5 playback probes")


if __name__ == "__main__":
    main()
