"""Asset checks reflecting Timberborn's three-column localization CSV contract."""
import csv
import io
from pathlib import Path
import unittest


def validate(text):
    rows = list(csv.reader(io.StringIO(text), strict=True))
    if not rows or rows[0] != ['ID', 'Text', 'Comment']:
        raise ValueError('Expected ID,Text,Comment header')
    keys = set()
    for row in rows[1:]:
        if len(row) != 3:
            raise ValueError('Empty record or invalid column count')
        # An untranslated value may be blank; Timberborn uses its fallback locale.
        if not row[0]:
            raise ValueError('Missing localization key')
        keys.add(row[0])
    for line in text.splitlines():
        line = line.replace('""', '')
        if ', "' in line or '" ,' in line:
            raise ValueError('Whitespace outside quoted CSV column')
    return keys


class LocalizationTests(unittest.TestCase):
    def test_all_shipped_locales_have_valid_records(self):
        folder = Path(__file__).resolve().parents[1] / 'BeaverBuddies/Localizations'
        files = list(folder.glob('*.csv'))
        self.assertEqual(len(files), 15)
        for path in files:
            with self.subTest(locale=path.name):
                validate(path.read_text(encoding='utf-8-sig'))

    def test_blank_records_that_crash_game_are_rejected(self):
        with self.assertRaises(ValueError):
            validate('ID,Text,Comment\n\nA,Text,\n')

    def test_missing_comment_column_is_rejected(self):
        with self.assertRaises(ValueError):
            validate('ID,Text,Comment\nA,Text\n')

    def test_multiline_text_and_embedded_quotes_are_preserved(self):
        self.assertEqual(validate('ID,Text,Comment\nA,"Hello\n\n""friend"", welcome",\n'), {'A'})

    def test_current_settings_have_english_localization(self):
        root = Path(__file__).resolve().parents[1] / 'BeaverBuddies'
        keys = validate((root / 'Localizations/enUS_BeaverBuddie.csv').read_text(encoding='utf-8-sig'))
        import re
        settings = (root / 'Settings.cs').read_text(encoding='utf-8-sig')
        for key in re.findall(r'"(BeaverBuddies\.Settings\.[^"]+)"', settings):
            self.assertIn(key, keys)


if __name__ == '__main__':
    unittest.main()
