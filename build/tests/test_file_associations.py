"""Guard the packaged Explorer contract and keep registration within playable video."""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


class FileAssociationTests(unittest.TestCase):
    def test_explorer_contract_preserves_quoted_paths_and_multi_selection(self):
        manifest = ET.parse(ROOT / 'src/Mani.App/Package.appxmanifest')
        extensions = manifest.findall('.//{*}Extension[@Category="windows.fileTypeAssociation"]')
        self.assertEqual(1, len(extensions))
        association = extensions[0].find('{*}FileTypeAssociation')
        self.assertEqual('"%1"', association.get('Parameters'))
        self.assertEqual('Player', association.get('MultiSelectModel'))
        self.assertEqual('Mani', association.find('{*}DisplayName').text)
        logo = association.find('{*}Logo').text.replace('\\', '/')
        self.assertTrue((ROOT / 'src/Mani.App' / logo).is_file())
        # Registration must not migrate another application's default choices.
        self.assertEqual([], manifest.findall('.//{*}MigrationProgId'))

    def test_registered_formats_are_playable_and_do_not_claim_subtitles_or_images(self):
        manifest = ET.parse(ROOT / 'src/Mani.App/Package.appxmanifest')
        formats = [node.text for node in manifest.findall('.//{*}FileType')]
        source = (ROOT / 'src/Mani.Playback/MediaSources.cs').read_text(encoding='utf-8')
        playable = re.search(r'Playable = new\("([^"]+)"', source)[1].split()
        self.assertEqual(len(formats), len(set(formats)))
        self.assertTrue({'.mkv', '.mp4', '.avi', '.mov', '.webm'} <= set(formats))
        self.assertTrue(all(ext.startswith('.') and ext[1:] in playable for ext in formats))
        self.assertFalse(set(formats) & {'.srt', '.ass', '.sup', '.jpg', '.png', '.gif', '.exe', '.ps1'})


if __name__ == '__main__':
    unittest.main()
